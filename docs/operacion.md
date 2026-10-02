# Operación

Cómo se levanta el gateway, qué mirar cuando algo falla, y las anomalías
observadas contra sistemas reales.

## Levantar el gateway

Desde la raíz del repositorio:

```powershell
$env:Ua__TagsCsvPath = "ruta\al\tags.csv"
dotnet run --project src/Gateway.Host
```

El CSV de mapeo es el único parámetro obligatorio y no tiene default: sin él el
gateway no arranca. Todo lo demás sale de `appsettings.json`, que **se copia al
directorio de build**, así que editarlo en el repo no tiene efecto hasta
recompilar. Es la confusión más frecuente al cambiar configuración.

Un arranque sano imprime, en este orden: la arquitectura del proceso (`X86`, no
es un error — el driver DA obliga a 32 bits), la ruta de certificados confiables,
la cantidad de tags cargados y cuántos quedaron fuera de servicio, el endpoint UA,
y por último la conexión al servidor DA. Si la última línea no aparece, el
gateway igual queda en pie sirviendo el árbol UA con calidad mala: es el
comportamiento de la Fase 4, no una falla.

Dos superficies para verificar que quedó bien: la página de diagnóstico en
`http://localhost:8080` y el endpoint UA en
`opc.tcp://127.0.0.1:4840/GatewayDaUa`.

## Anomalías observadas

### `SourceTimestamp` atrasado — RESUELTO en Fase 6

Durante las fases 2 a 5, el `SourceTimestamp` llegaba intermitentemente ~7 minutos
atrasado. **La causa es un bug de conversión de `FILETIME` en el SDK cliente DA,
no del simulador.** El gateway lo corrige desde la Fase 6 en
`Gateway.Da.SdkTimestamp.Correct()`.

La historia completa, con la aritmética, la evidencia medida y el error de método
que mantuvo viva la hipótesis equivocada durante tres fases, está en
**[bug-filetime-sdk.md](bug-filetime-sdk.md)**.

**Qué se descartó correctamente en su momento** (sigue siendo válido): drift de
reloj, cambios de hora del sistema, suspensión, reinicio del simulador, y
configuración de aliases. También se instrumentó el borde del driver y se
comprobó que transmitía fielmente lo que recibía — correcto, pero incompleto: el
SDK quedaba una capa por debajo del punto donde se estaba midiendo.

**Qué hacer si reaparece.** Buscar timestamps terminados en `.5032704`. Esa firma
es exclusiva de este bug. Si aparece, la corrección no se está aplicando en algún
camino de lectura nuevo: todos tienen que pasar por `SdkTimestamp.Correct()`, y
exactamente una vez (no es idempotente).

**Cómo grabar la demo.** Con el bug corregido, el `SourceTimestamp` sigue al reloj
y el `ServerTimestamp` queda unos cientos de ms después. Esa separación chica es
la que hay que mostrar: son dos relojes distintos, y el de origen no se pisa. Un
atraso de 7 minutos en pantalla ya no es una curiosidad del simulador, es el bug
sin corregir.

**Consecuencia para la Fase 4, sin cambios.** La degradación por antigüedad se
sigue midiendo con `LastUpdateUtc` (reloj del gateway) y no con `SourceTimestamp`.
El motivo original ya no aplica, pero el criterio es correcto igual: el
`SourceTimestamp` lo produce un sistema ajeno y no hay garantía de que su reloj
esté sincronizado con el nuestro.

**Por qué importa metodológicamente.** Ninguna prueba unitaria del gateway podía
detectarlo: el driver cumplía su contrato. Lo que lo acorraló fue medir el
desfase y reconocer el número — 429,4967296 s es 2³² ticks, la firma de un error
de signo. La instrumentación en el borde entre los dos sistemas fue el
instrumento correcto; lo que faltó fue mirar una capa más abajo.

## Operar el simulador MatrikonOPC durante las pruebas

Tres comportamientos que cuestan una sesión entera si se descubren en el momento.

### El servidor DA no es la ventana que se ve en el escritorio

Es un componente COM **out-of-process**: Windows lo lanza a demanda cuando un
cliente pide el ProgID. La ventana que se abre desde el menú de inicio es el
*configurador* (`PSTCFG.exe`), no el servidor (`OPCSim.exe`). Cerrarla no deja al
servidor indisponible.

- Para simular **DA caído**: `Stop-Process -Name OPCSim -Force`. COM lo relanza
  solo en el siguiente intento de conexión, lo cual es bueno: la recuperación no
  depende de que nadie levante nada a mano.
- Para simular **DA ausente de verdad**: apuntar el gateway a un ProgID
  inexistente en `appsettings.json` (se usó `Matrikon.OPC.NoExiste.1`). Da
  `0x80040154 CoCreateInstanceEx: Clase no registrada`.

Cuando el servidor muere, `Read` tira `0x800706BA` (RPC server unavailable) y el
objeto COM queda inservible: no es un error transitorio. Por eso el gateway
recrea el driver entero en vez de reintentar la lectura.

### El servidor relanzado vuelve vacío

Los aliases importados viven en el archivo de configuración, no en el proceso. Al
matar `OPCSim`, COM lo relanza con la configuración por defecto y los ItemIDs
importados dejan de existir: el gateway pide items que del otro lado ya no están y
el alta se rechaza, correctamente.

Para volver a poblarlo hay que cargar el escenario de nuevo en el configurador
(File → Open sobre el `.opcsim.xml`). El gateway se reengancha solo en el
siguiente reintento, sin reiniciarlo.

Esto no se nota con items nativos del simulador (`Random.Real8` y similares), que
existen siempre. Aparece con escenarios de carga importados.

### El configurador crashea si se lo usa con el servidor muerto

Si se intenta cargar un `.opcsim.xml` con `OPCSim` matado, el configurador da
`Error 0x800706BA occurred loading configuration from file` y a continuación un
access violation en `PSTCFG.exe`, quedando inconsistente (`Clients: -1`,
`Server Time: n/a`).

Salida: cerrar el configurador —forzándolo con `Stop-Process -Name PSTCFG -Force`
si no responde— y abrirlo de nuevo. Al arrancar limpio relanza el servidor por COM
y la carga funciona. Es un bug del configurador, no del gateway.

Al cargar una configuración con el gateway conectado, Matrikon avisa que los
items OPC se invalidan. Es el mismo escenario que en planta produce una recarga de
configuración del servidor DA, y el gateway lo resuelve con el reintento de altas.

## Empaquetar el gateway para distribuir

El paquete permite correr el gateway en una máquina sin Visual Studio, sin el
repo y sin .NET instalado. Es una carpeta comprimida, no un instalador: se
descomprime y se ejecuta `Gateway.Host.exe`.

### Publicar

```powershell
dotnet publish .\src\Gateway.Host -c Release -r win-x86 --self-contained true -o .\publish
```

`win-x86` no es opcional: el driver DA es COM de 32 bits y el ejecutable manda
sobre todas las bibliotecas que carga.

**Self-contained** porque el runtime de .NET viaja adentro. La alternativa exige
que la máquina destino tenga instalado el runtime **x86** específicamente —el x64
no sirve—, que es el tipo de requisito donde alguien que quería probar el gateway
diez minutos abandona. Precio: ~110 MB de carpeta.

**Sin `PublishTrimmed`.** El stack de la OPC Foundation y el interop COM resuelven
tipos por reflection; el trimmer los borra sin avisar y la falla aparece en
runtime, no al compilar.

`dotnet publish -o` no limpia el destino: sobreescribe lo que coincide y deja lo
demás. Un publish anterior con otra configuración sobrevive mezclado, y peor,
puede hacer que el paquete arranque en la máquina de desarrollo por un archivo
que en la máquina destino no va a estar. Borrar `publish/` antes de cada corrida.

### Armar el paquete

Sobre la carpeta publicada:

- **Borrar `pki/`.** Los certificados se generan solos en el primer arranque.
  Mandar los propios arrastra el nombre de host de la máquina de desarrollo.
- **Borrar los `.pdb`.** Además de ser ruido, sus stack traces exponen las rutas
  absolutas del disco de desarrollo.
- **Agregar** el `.opcsim.xml` del escenario de demo, el CSV de aliases y un
  `LEEME.txt` con requisitos, puesta en marcha y qué se debería ver.

El paquete de la POC SQL -> UA para PI System (V2-28) se arma con
`tools/Build-PocPackage.ps1`, que automatiza estos mismos pasos para ese caso.

`config/demo-10.opcsim.xml` está versionado aunque sea un archivo generado por
el configurador de Matrikon: son 2 KB y evita tener que abrirlo cada vez que se
arma un paquete. Se regenera importando `config/aliases.example.csv` con
`File → Import Aliases` y guardando con `File → Save As...`. Al importar sobre
una configuración ya abierta los aliases se suman a los que había, así que
conviene partir de una configuración vacía —si no, el escenario de demo termina
guardado junto con los 8.000 de la prueba de carga.

### Qué se verificó y qué no

La prueba no es que el publish termine sin error: es **descomprimir el paquete en
una carpeta cualquiera fuera del repo y que arranque**. Ese recorrido está
verificado, y fue el que expuso el problema de rutas de la decisión 21 — el
gateway resolvía la PKI subiendo hasta el archivo de solución, que en una máquina
sin el repo no existe.

Lo que **no** está verificado en limpio son los dos primeros pasos del LEEME:
instalar el simulador y cargar el escenario en un Windows donde Matrikon nunca
estuvo. La máquina de desarrollo ya lo tenía instalado y con los aliases
importados, así que esa parte del procedimiento está escrita pero no probada.

### Firewall

Al primer arranque Windows pide permiso de red. **Se puede cancelar**: el
servidor UA y la página de diagnóstico escuchan en `localhost`, y ese tráfico no
lo filtra el firewall. Conviene decirlo en el LEEME para que no se lea como que
algo falló.

Cancelar no es neutro: Windows crea una regla de bloqueo explícita, igual que
aceptar crea una de permiso. Se listan y se borran así (el borrado necesita
PowerShell como administrador):

```powershell
Get-NetFirewallApplicationFilter |
  Where-Object { $_.Program -like "*Gateway.Host.exe*" } |
  Get-NetFirewallRule |
  Select-Object DisplayName, Direction, Action, Profile
```


## Anomalía conocida: errores de certificado repetidos en consola

**Síntoma.** La consola del gateway muestra, cada ~5 segundos y de forma
indefinida, un bloque de error como este:

[ERR] Could not verify security on OpenSecureChannel request.
The receiver's certificate thumbprint is not valid.
[80120000] (BadCertificateInvalid) 'The receiver's certificate thumbprint is not valid.'
[ERR] ChannelId N: ForceChannelFault due to Could not verify security on OpenSecureChannel request..


El `ChannelId` se incrementa en cada repetición. Suele venir acompañado de
`SERVER - Service Fault Occurred. Reason=BadMessageNotAvailable`.

**Qué NO significa.** No indica un problema con el certificado propio del
gateway, ni con la PKI, ni con la configuración de seguridad. Tampoco lo
resuelve tener `auto-aceptar` habilitado: esa opción controla si el gateway
confía en el certificado *del cliente*, y acá el rechazo va por otro lado.

**Causa.** En el mensaje `OpenSecureChannel`, el cliente incluye el thumbprint
del certificado *del servidor* que tiene guardado de una sesión anterior —es su
forma de decir "el receptor de este mensaje debería ser este de acá". El gateway
compara ese thumbprint contra el certificado que está usando y, si no coincide,
corta el canal. Es decir: **hay un cliente UA con una identidad vieja del
servidor cacheada**, típicamente de una instalación previa o de otro servidor que
alguna vez ocupó el mismo endpoint. El cliente reintenta solo, en loop, y el
gateway lo rechaza correctamente cada vez.

**Cómo verificarlo.** Con el gateway levantado, ver qué procesos y qué máquinas
tienen conexiones al puerto UA:

```powershell
Get-NetTCPConnection -LocalPort 4840 -ErrorAction SilentlyContinue |
  Select-Object State, LocalAddress, RemoteAddress, OwningProcess
```

Para ver también las conexiones que ya cerraron (estados `Time Wait`), que es
donde suele quedar el rastro del cliente que reintenta, conviene
[TCPView](https://learn.microsoft.com/sysinternals/downloads/tcpview) filtrando
por `4840`: muestra proceso, dirección remota y timestamps en una sola vista.

Dos cosas a mirar en esa salida:

- **Si la dirección remota no es `127.0.0.1`**, el cliente está en otra máquina de
  la red — lo que desde la Fase 7 solo es posible si alguien cambió el bind a
  propósito, porque el default es loopback (ver "A qué interfaz se expone el
  gateway", más abajo).
- **Cuántos certificados propios tiene el gateway.** Debe haber exactamente uno:

```powershell
  Get-ChildItem -Recurse .\pki\own\certs | Select-Object Name, LastWriteTime
```

  Si hay dos o más, el problema es otro: el gateway regeneró su certificado —por
  ejemplo al correrse desde una ruta distinta, ya que la PKI es relativa al
  directorio de trabajo— y entonces el thumbprint desactualizado lo tienen
  todos los clientes legítimos.

**Resolución.** Con un solo certificado propio, no hay nada que corregir del lado
del gateway: está rechazando a un cliente que se identifica mal, que es el
comportamiento esperado. Las salidas son borrar el certificado cacheado del store
de confianza de ese cliente, o apagarlo si nadie lo usa. Las conexiones huérfanas
se limpian solas por timeout y los errores cesan.

**Resuelto en la Fase 7.** Este ruido sigue en la consola —el stack lo emite y no
lo silenciamos—, pero ya no es la única forma de enterarse: los intentos de
conexión rechazados se cuentan y se agrupan por motivo, tanto en la página de
diagnóstico como en los nodos UA de `Gateway.Counters`. Un rechazo correcto es un
evento contable, no un error, y ahora tiene un número que lo dice.

Como referencia de escala: dos rechazos de un mismo cliente produjeron doce
líneas entre `WRN` y `ERR` en la consola, y una sola línea en la página
(`BadCertificateUntrusted: 2 — último 18:38:46`).

## Habilitar el cliente de carga (`tools/UaLoadClient`)

El cliente sintético de las pruebas de carga tiene **su propia PKI**, separada de
la del gateway. La emite solo: en el primer arranque crea un certificado
autofirmado de 2048 bits en `%LocalApplicationData%\UaLoadClient\pki\own`. Está
fuera de `bin/` a propósito, para que sobreviva a un `dotnet clean` — si viviera
en el directorio de salida, cada limpieza generaría una identidad nueva y habría
que rehacer la confianza sin que nada avisara por qué dejó de conectar.

Que cada lado tenga su certificado no alcanza: además tienen que confiar el uno
en el otro. Eso lo hace un script:

```powershell
.\tools\UaLoadClient\trust-setup.ps1
```

Copia el certificado **público** (`.der`) de cada lado al `trusted\certs\` del
otro. Nunca toca los `.pfx`, que llevan la clave privada y no salen de
`own\private\`. Es idempotente: correrlo de más no rompe nada.

**Cuándo hay que correrlo:**

- La primera vez, después de que el gateway y el cliente hayan arrancado al
  menos una vez cada uno (antes no existen los certificados que hay que copiar).
- Cada vez que se re-emita un certificado de cualquiera de los dos lados. Un
  certificado nuevo tiene otro thumbprint, y el que estaba confiado deja de
  servir.

**Cómo se ve que falta correrlo:** el cliente aborta con
`BadSecurityChecksFailed` en el `OpenSecureChannel`, y el gateway registra en el
log el motivo real —`BadCertificateUntrusted`— y deposita el certificado en
`pki\rejected\certs\`. El mensaje del cliente es genérico; **el log del gateway
es el que dice qué pasó**.

El cliente corre con `AutoAcceptUntrustedCertificates` en `false` y
`useSecurity: true`, así que negocia `SignAndEncrypt` contra el mismo endpoint
que enfrenta cualquier cliente real. Es deliberado: con auto-accept la prueba de
carga mediría el rendimiento pero no ejercitaría la validación de confianza, que
es justo lo que la Fase 7 activó. No hace falta encender `EnableUnsecureEndpoint`
para correr las pruebas.

## A qué interfaz se expone el gateway

Por default el servidor UA escucha **solo en loopback** (`127.0.0.1`). Es una
decisión, no el default del stack: el stack ata a todas las interfaces, y un
gateway que expone un sistema legado sin autenticación de usuario no debería
aparecer en la red por accidente. Abrirlo es un acto explícito.

Se verifica con el socket, no con la configuración:

```powershell
Get-NetTCPConnection -LocalPort 4840 | Select-Object LocalAddress, LocalPort, RemoteAddress, State
```

Medido en la Fase 7, con el gateway en pie:

LocalAddress LocalPort RemoteAddress State

127.0.0.1 4840 0.0.0.0 Listen


El dato del bind está en `LocalAddress`. El `0.0.0.0` de `RemoteAddress` **no**
significa que acepte conexiones de cualquier origen: un socket en escucha no
tiene contraparte todavía, y Windows rellena ese campo con ceros. Confundir las
dos columnas lleva a creer que el gateway está abierto cuando no lo está.

**Al cambiar el bind hay que reemitir el certificado del servidor.** El nombre de
host viaja en el campo SAN del certificado, y el cliente lo compara contra la URL
por la que se conectó. El SAN lleva **un solo nombre**: el host del `EndpointUrl`
vigente cuando se emitió el certificado, que el stack no recalcula después
(V2-28). Emitido con `127.0.0.1` dice solo `IP=127.0.0.1`; emitido con el hostname,
solo `DNS=<hostname>`. Un certificado emitido para una dirección y usado desde
otra produce `BadCertificateHostNameInvalid`, un rechazo que parece un problema de
red y no lo es. El procedimiento es borrar (o renombrar) la carpeta `pki/own`
completa y dejar que el gateway emita uno nuevo en el próximo arranque, ya con la
dirección nueva. Cambia el thumbprint, así que los clientes tienen que volver a
confiar en él (ver "Al regenerar el certificado del gateway", más abajo).


## Volver atrás

El gateway no tiene estado persistente: no historiza, no escribe en el servidor
DA, y su configuración son dos archivos. Eso hace que el rollback sea reinstalar
la versión anterior, sin migración ni limpieza de datos.

1. Frenar el proceso.
2. Restaurar el paquete publicado anterior, o `git checkout` del tag previo y
   recompilar.
3. Restaurar el `tags.csv` y el `appsettings.json` que acompañaban a esa versión.
   **Son parte del rollback**: un CSV nuevo contra un binario viejo puede fallar
   por campos que la versión anterior no conoce.
4. Levantar y verificar contra las dos superficies del arranque.

El único artefacto que sobrevive a un rollback es la PKI (`pki/`). Se conserva a
propósito: borrarla obligaría a volver a confiar cada cliente. Si se vuelve a una
versión con otro bind, aplica la reemisión del certificado descrita arriba.

## Timestamps y notificaciones vistos desde el cliente (PI System)

### `ServerTimestamp` igual al `SourceTimestamp` en una lectura

Al leer un tag con el servicio **Read** (en UaExpert, el panel *Attributes*), el
`ServerTimestamp` sale idéntico al `SourceTimestamp`, al milisegundo. Pasa con
cualquier tag, DA o SQL; se notó primero en SQL porque sus valores cambian poco y
la coincidencia salta a la vista (B6.1). **No lo hace el gateway.** El gateway
solo escribe `node.Timestamp` (el `SourceTimestamp` que trae la fuente) y nunca
toca el `ServerTimestamp`. Lo completa el stack UA (OPC Foundation .NET Standard
1.5.378.156), y de forma distinta según el servicio:

- **Read.** `CustomNodeManager2.Read`, para el atributo `Value`, copia el
  `SourceTimestamp` en el `ServerTimestamp` (y si no hubiera `SourceTimestamp`,
  pone la hora actual en los dos). Verificado leyendo el código del stack.
- **Suscripción.** `MonitoredNode2.QueueValue` estampa el `ServerTimestamp` con la
  hora del muestreo. Es lo que se midió en la Fase 2 (unos cientos de ms después
  del `SourceTimestamp`) y lo que recibe un cliente que se suscribe.

El principio "el `SourceTimestamp` no se pisa" se cumple en los dos casos. Si un
cliente necesitara un `ServerTimestamp` propio también en el Read, habría que
sobrescribir `Read` en `GatewayNodeManager`; hoy no hay un consumidor que lo pida.

### La suscripción no notifica cambios de solo timestamp

El trigger por defecto de un `MonitoredItem` es `StatusValue`: el servidor notifica
cuando cambia el valor o el `StatusCode`, no cuando cambia solo el
`SourceTimestamp` (B6.3). El gateway republica todos los nodos en cada ciclo de
`UpdateIntervalMs`, pero el filtro del stack descarta esas muestras si valor y
calidad son iguales a los anteriores.

Qué ve un cliente tipo PI System con la configuración por defecto:

- **Un tag que se refresca con el mismo valor no genera eventos.** En SQL es lo
  normal: `PLANTA_02_ESTADO_BOMBA_01` en 1, o `PRESION_SALIDA` clavada en 9 por un
  operador, tienen `TS` nuevo en cada escritura de la aplicación de origen y en PI
  no aparece nada después del primer valor. El archivo de PI muestra el último
  valor como vigente (con la hora de ese evento), que es correcto, pero no hay
  forma de distinguir "se sigue confirmando" de "nadie lo actualiza" (el caso b de
  P14, que el gateway tampoco distingue).
- **Los cambios de calidad sí llegan.** La marca de V2-32 (vínculo SQL caído →
  `Uncertain`) y la vuelta a `Good` cambian el `StatusCode`, así que generan
  notificación aunque el valor no cambie.

Qué configurar si hace falta ver cada refresco: un `DataChangeFilter` con
`Trigger = StatusValueTimestamp` en los `MonitoredItem`, **sin deadband**: si el
filtro trae deadband, el stack (`MonitoredItem.ValueChanged`) vuelve el trigger a
`StatusValue` en silencio. Lo pide el **cliente**, no se configura en el gateway: el
stack lo soporta. Antes de prometerlo hay que
verificar si el conector de PI que se use permite elegir el trigger; si no lo
permite, la alternativa es leer por polling (servicio Read) en vez de suscribirse.
Dos costos a tener en cuenta: con `StatusValueTimestamp`, cada escritura de la
aplicación de origen es un evento en PI aunque el valor no cambie (más volumen,
aunque la compresión de PI lo absorbe), y en un tag DA el `SourceTimestamp` avanza
en cada lectura del servidor DA, así que el trigger convierte cada ciclo en un
evento.

## Ruido conocido — no perseguir

Errores que aparecen en la consola durante operación normal y **no** indican una
falla:

| Mensaje | Cuándo | Qué es |
|---|---|---|
| `ERR` de dominio al conectar | cada conexión exitosa | El servidor validando su propio certificado contra la URL del cliente. Se descarta por thumbprint y no llega a los contadores. Causa exacta no confirmada. |
| `BadServerHalted` | arranque | Un cliente pidiendo sesión antes de que el server termine de levantar. |
| `Oops! MonitoredItems queued but no notifications available.` | esporádico con clientes suscriptos | Falsa alarma del stack. Se emite en `Subscription.InnerPublish()` (`Subscription.cs:1007`, tag `1.5.378.156`) cuando un `PublishRequest` termina sin notificaciones. Pese al texto, la condición no verifica que haya items encolados: alcanza con que la lista de publicación quede vacía porque otro request la vació primero. `SubscriptionManager` lo comenta como *false alarm or race condition*, re-encola el request y lo loguea en `Trace` (`SubscriptionManager.cs:1113`). El mismo evento sale como `Error` en un lado y `Trace` en el otro; el nivel alto es el incorrecto. No se pierden datos ni requests. Solo aparece con clientes que mantienen varias suscripciones sobre una misma sesión: 15 min y 324.000 notificaciones con `UaLoadClient` no lo produjeron ni una vez. **Se filtra en `Program.cs`** por mensaje y no por categoría, para no perder errores reales del servidor UA. |
| `BadMessageNotAvailable`, a veces precedido de `BadSessionIdInvalid` | reconexión de un cliente, o reinicio del gateway con UaExpert abierto | El cliente pide `Republish` de secuencias de una sesión anterior que el servidor ya no tiene (`Subscription.Republish()`, `Subscription.cs:1237`). Sale como `WRN` y no como `INF` porque el `switch` de `EndpointBase.cs:496` no lo incluye en su lista de desconexiones esperadas. |
| 404 de favicon | página de diagnóstico abierta | El navegador pidiendo un ícono que no existe. |

Ninguno de estos suma a los contadores de rechazo: solo cuentan los StatusCode de
identidad. Un número creciendo ahí sí es alguien insistiendo mal configurado.

## El log decia "domain not listed" en cada conexion (resuelto)

Hasta el 24/08/2026, cada conexion de un cliente por IP dejaba estas dos
lineas en el log del servidor:

    The domain 'opc.tcp://127.0.0.1:4840/GatewayDaUa' is not listed in the server certificate.
    Server - Client connects with an endpointUrl which does not match Server hostnames.

No bloqueaban nada, pero eran reales: el certificado del gateway no declaraba
el dominio contra el que el stack lo comparaba. Corregido en `Program.cs`.

**Que pasaba.** Las dos lineas son el mismo chequeo, no dos: `CreateSession`
valida el certificado del propio servidor contra la URL que mando el cliente
y, si falla con `BadCertificateHostNameInvalid`, loguea las dos seguidas. Y
esa comparacion tiene dos comportamientos que la intuicion no anticipa,
ambos verificados contra el fuente del tag `1.5.378.156`:

1. `CertificateValidator.FindDomain` **descarta la IP** cuando el cliente
   entra por loopback: reconoce `127.0.0.1` como tal y la sustituye por
   `Utils.GetHostName()` y el FQDN antes de comparar. El SAN con
   `IP=127.0.0.1` nunca llega a mirarse. El dominio que hace falta es el
   nombre de la maquina.
2. `X509Utils.GetDomainsFromCertificate` **concatena todos los `DC=` del
   subject con puntos en una sola cadena**. El subject declaraba
   `DC=127.0.0.1, DC=localhost` —que el stack reescribe a
   `DC=127.0.0.1, DC=laptop-0jprbimi` al arrancar— y de ahi salia el unico
   dominio `127.0.0.1.LAPTOP-0JPRBIMI`, que no matchea nada. El hostname
   correcto ya estaba en el certificado; estaba pegado a la IP.

**La correccion:** un solo `DC=localhost` en el `SubjectName`. El stack lo
reescribe a `DC={hostname}` en `SecurityConfiguration.Validate()`, asi que no
hardcodea el nombre de la maquina. No se toco el SAN, ni el bind, ni las
`BaseAddresses`.

**Como comprobarlo.** Subir `Opc.Ua` a nivel `Information` en el logger y
arrancar: el stack imprime la lista contra la que valida.

    Certificate Domain names:
     LAPTOP-0JPRBIMI
     127.0.0.1

Dos entradas, que salen de lugares distintos. La primera es el `DC=` del
subject (siempre el nombre de la maquina). La segunda es el **unico** nombre
del SAN, que es el host del `EndpointUrl` con el que se emitio el certificado
(V2-28): `127.0.0.1` en el certificado de loopback, que es el de este ejemplo.
El SAN no trae el hostname y la IP a la vez. Comprobado el 02/10/2026 sobre
los certificados publicos de `pki/own/certs`: el de loopback tiene subject
`DC=laptop-0jprbimi` y SAN `IP=127.0.0.1` (mas la URI de la aplicacion); uno
emitido con el hostname en la URL tenia SAN `DNS=laptop-0jprbimi` y nada mas.
En ese segundo caso la lista del log no se volvio a capturar. Si alguna vez
vuelve a aparecer una sola entrada con puntos en el medio, es esta misma
recaida.

**El error de metodo que lo mantuvo abierto.** La hipotesis anotada era que
la validacion comparaba contra los hostnames derivados del `applicationUri`.
Era falsa, y llevo a un intento de arreglo —agregar la IP al SAN— que no
podia funcionar porque atacaba el campo equivocado. Se sostuvo porque el
experimento que la habria refutado (conectar por hostname) era imposible sin
tocar el bind de loopback. Lo que la resolvio no fue idear un experimento
mejor: fue leer el fuente de la version exacta que corre el gateway. Misma
familia que el bug de `FILETIME`.

**Al regenerar el certificado del gateway** (borrar `pki/own/` y arrancar)
cambia el thumbprint, y hay que volver a confiar el nuevo: correr
`tools/UaLoadClient/trust-setup.ps1` y re-aceptarlo en UaExpert. El script
copia pero no limpia, asi que el certificado viejo queda huerfano en el
almacen de confiados del cliente y conviene borrarlo a mano.