# Decisiones de diseño

Registro de decisiones del gateway. **Los números son identificadores estables:
no se renumeran ni se reordenan**, aunque una decisión quede superada. Cuando eso
pasa, la entrada vieja se marca y apunta a la que la reemplaza.

---

## 1. La interfaz de la fuente de datos se ensancha

> **Superada por la decisión 7.** El diagnóstico que sigue se sostiene entero;
> lo que no se sostuvo fue la solución. Se conserva porque el camino desde este
> razonamiento hasta el de la 7 es parte del resultado.

El proyecto anterior (`oilfield-scada`) define la fuente de valores así:

```csharp
void Step(double dtSeconds);
bool TryGetValue(string tagName, out double value);
```

Alcanzaba para Modbus, donde la calidad es binaria: contestó o no contestó. Para
OPC DA no alcanza, por tres razones distintas:

- **`out double` no soporta `String`.** El CSV declara `Double`, `Boolean`,
  `Int32` y `String`. El tipo del valor tiene que abrirse, no solo sumarle campos.
- **Falta la calidad y el `SourceTimestamp`.** OPC DA entrega valor, calidad y
  timestamp de origen por cada item, y los tres tienen que llegar intactos al
  cliente UA.
- **El `bool` de retorno se vuelve redundante.** Con un `StatusCode` en la
  respuesta, "no conozco ese tag" deja de ser `false` y pasa a ser un status
  no-bueno. Un tag desconocido y uno con calidad mala serían indistinguibles, y
  en un gateway no pueden serlo.

La conclusión de entonces fue una firma del tipo
`(valor, StatusCode, SourceTimestamp)`. Ver decisión 7 para por qué eso no fue lo
que se implementó.

## 2. El `SourceTimestamp` nunca se pisa

El `ServerTimestamp` lo pone el gateway. El `SourceTimestamp` es el que vino del
DA y no se sobrescribe en ningún punto del recorrido.

El node manager heredado hacía `node.Timestamp = DateTime.UtcNow` en cada ciclo
de publicación. Eso es correcto para un simulador y **corrupción de datos** para
un gateway: si un tag no cambia hace diez minutos, su timestamp tiene que tener
diez minutos de antigüedad. Un timestamp que se refresca solo hace que un
historiador aguas abajo registre diez minutos de datos que nunca existieron, y
ese problema aparece meses después y es carísimo de rastrear.

La verificación es visual: en el árbol de prueba, un solo tag cambia de valor
periódicamente y su `SourceTimestamp` avanza; los demás quedan clavados en la
hora de arranque mientras su `ServerTimestamp` sigue actualizándose.

> **Actualización 05/10/2026 — esto se cumple en suscripción, no en Read.** En un
> Read el stack iguala el `ServerTimestamp` al `SourceTimestamp`, así que los dos
> salen clavados. El `SourceTimestamp` del nodo sigue sin pisarse. Ver V2-36 en
> [`v2/decisiones.md`](v2/decisiones.md).

## 3. La jerarquía se deriva del nombre del tag

El punto en `TAG_NAME_OPC_UA` separa jerarquía: `PLANTA_01.MEDICION.PRESION_ENTRADA`
produce dos carpetas anidadas y una variable. **No existe un campo `NODE_PATH`**
en el CSV, deliberadamente: sería una segunda fuente de verdad para lo mismo, y
dos fuentes de verdad divergen.

Se usa el punto y no la barra porque en el proyecto anterior la `/` en los
nombres rompió el ruteo de un endpoint HTTP aguas abajo, que terminó necesitando
el tag por query string.

Carpetas intermedias compartidas se crean una sola vez, indexadas por su ruta
acumulada. Dos tags que comparten prefijo cuelgan de la misma carpeta, y dos
carpetas homónimas en ramas distintas no colisionan porque el `NodeId` es la
ruta completa, no el segmento.

## 4. `BaseDataVariableState` antes que `AnalogItemState`

El proyecto anterior usa `AnalogItemState`, que exige `EURange` y
`EngineeringUnits`. El CSV trae `EU` desde la Fase 3, pero todavía no se expone
como propiedad del nodo, y además no existe `AnalogItemState` para `String`.

Se arranca con `BaseDataVariableState`, que acepta cualquier `DataType`, y se
sube a `AnalogItemState` más adelante para los tags que declaren unidad de
ingeniería. Subir es fácil; arrancar con un tipo que no admite uno de los cuatro
tipos de dato no.

## 5. El `PlatformTarget x86` va solo en el host

El driver de OPC DA obliga a un proceso de 32 bits. El `PlatformTarget` manda en
el ejecutable: si el host es x86, todas las bibliotecas que cargue corren en ese
proceso aunque estén compiladas como `AnyCPU`.

Por eso el x86 está en `Gateway.Host` y nada más. Ponerlo en los cinco proyectos
ataría las manos sin beneficio y complicaría una eventual partición en dos
procesos.

**Consecuencia a vigilar:** un proceso de 32 bits tiene un límite práctico de
memoria útil cercano a los 2 GB. No molesta con cientos de tags; puede apretar
en las pruebas de carga con decenas de miles.

## 6. La ubicación de la PKI se fija en el primer arranque

El stack OPC UA genera su propio certificado la primera vez que corre. **Cada
mudanza de la carpeta PKI regenera el certificado y rompe toda la confianza ya
establecida con los clientes.**

Por eso la carpeta se resuelve por ruta absoluta anclada al archivo de solución,
subiendo desde la ubicación del ejecutable — nunca contra el working directory.
Si se resolviera contra el working directory, arrancar con `dotnet run` desde la
raíz y ejecutar el binario desde su carpeta de salida darían dos carpetas PKI
distintas, y el certificado se regeneraría al alternar entre las dos formas.

La única regeneración que se hace a propósito es al cambiar el host del
`EndpointUrl`: el SAN lleva un solo nombre, el de la URL con la que se emitió, y
no se recalcula. Se borra `pki/own` completa, el gateway emite uno nuevo al
arrancar y los clientes vuelven a confiar en él (v2, V2-28; procedimiento en
`operacion.md`).

---

## 7. El contrato se partió en dos, no se ensanchó

La idea original era ensanchar `ITagValueSource` para que devolviera
`(valor, StatusCode, SourceTimestamp)` en vez de solo un valor. Al implementarlo
apareció que con una cache en el medio hay dos preguntas distintas, no una más
grande: el driver DA responde *qué acabo de leer* (`TagSample`) y el node manager
pregunta *cuál es el último estado conocido* (`TagState`). Son ritmos distintos y
no tienen por qué compartir firma.

Lo que se diseñó entonces fue el tipo de dato compartido, no una interfaz común.
`TagSample` viaja del driver a la cache; `TagState` va de la cache al node
manager. El segundo puede devolver un valor viejo, el primero nunca.

Supera a la decisión 1.

## 8. El driver DA no tiene reloj propio

`OpcDaTagSource` expone `Connect()`, `AddItems()`, `ReadAll()` y `Dispose()`, y
nada más. El ciclo que llama a `ReadAll()` vive en el host.

El desacople de ritmos es trabajo de la cache y del host; si además el driver
tuviera su propio timer habría dos relojes y ninguno dueño. Un driver sin hilos
adentro también es mucho más simple de razonar cuando haya que agregar
reconexión (Fase 4).

La contrapartida es que la restricción de apartment COM (MTA) se le escapa al
driver y pasa al host. Se resuelve validando: `Connect()` verifica el apartment
y falla con un mensaje legible si no es MTA.

## 9. El hilo de adquisición DA es explícito y separado

El ciclo DA corre en un `Thread` propio, creado con
`SetApartmentState(ApartmentState.MTA)`, y no en el timer de publicación UA.

Dos razones. Primera: hasta acá el MTA se obtenía de rebote, porque un `Main`
async corre sobre el thread pool, que es MTA por defecto — funcionaba, pero por
accidente. Segunda: una lectura DA lenta no tiene por qué frenar la publicación
UA, y compartir hilo garantizaría lo contrario.

## 10. Ante una lectura mala se conserva el último valor

Cuando llega una muestra con calidad no utilizable, la cache mantiene el valor
anterior **y su `SourceTimestamp` original**, y le pega la calidad nueva. No se
descarta el valor ni se refresca su hora.

El criterio viene de cómo se usa el dato aguas abajo: un operador que ve un campo
vacío no puede distinguir un tag caído de un tag que nunca existió, mientras que
un valor con indicación de calidad mala le dice cuánto valía la medición hasta
hace un rato y que dejó de actualizarse. La propia especificación de OPC DA
reserva un subestado para esto (`BadLastKnown`), lo que indica que conservar era
el comportamiento asumido.

Lo que nunca se hace es conservar el valor y refrescarle el timestamp: ahí
desaparece toda evidencia de que está congelado.

El caso apareció en la primera corrida real. Leyendo con `Cache`, el servidor DA
devolvió los valores de la sesión anterior marcados `BadOutOfService`, con
timestamps de doce minutos antes de que el gateway arrancara. Sin la guarda de
`IsUsable`, esos números se habrían escalado y publicado con apariencia de dato
fresco.

## 11. Un item DA puede alimentar varios nodos UA

La relación entre `TAG_NAME_OPC_DA` y `TAG_NAME_OPC_UA` es uno a muchos: el mismo
punto del servidor legado puede exponerse más de una vez con transformaciones
distintas — la misma presión en bar y en kg/cm², por ejemplo.

Se descubrió porque la primera versión de la cache indexaba las definiciones por
nombre DA en un diccionario y el gateway no arrancaba con un CSV que repetía un
ItemID. La corrección fue agrupar: por cada nombre DA, la lista de definiciones
que lo consumen.

## 12. La traducción de calidad va con `switch` explícito

Los enums de calidad del SDK (`OpcDaQualityMaster`, `OpcDaQualityStatus`,
`OpcDaQualityLimit`) coinciden en nombre y valor numérico con los de
`Gateway.Core`, así que un cast habría funcionado. Se usa `switch` de todas
formas.

Un cast sigue compilando si el SDK renumera un enum en una versión futura, y
falla en silencio sobre el dato: el gateway publicaría calidades equivocadas sin
que nada avise. El `switch` falla ruidoso. El costo son quince líneas.

Los casos por defecto caen siempre en el lado conservador (`Bad`, `NotLimited`):
ante un valor desconocido, el gateway no puede afirmar que el dato está bien.

La tabla de correspondencia completa está en [calidad-da-ua.md](calidad-da-ua.md).

## 13. Se lee con `Cache`, no con `Device`

`OpcDaGroup.Read` acepta dos orígenes. `Device` fuerza una lectura contra el
dispositivo; `Cache` devuelve lo que el servidor DA ya tiene en su propia cache,
refrescada a su ritmo.

Se eligió `Cache` porque es el único modo que escala: con `Device`, el tráfico al
dispositivo crece con la cantidad de lecturas del gateway, y evitar exactamente
eso es la razón de ser de la cache propia
(ver [arquitectura.md](arquitectura.md#por-qué-la-cache-es-el-centro)). Un
gateway que le pega al equipo legado en cada ciclo traslada al dispositivo la
carga que debería absorber él.

El precio es latencia, acotada por el `UpdateRate` del grupo. Medido contra
Matrikon con `UpdateRate` de 1000 ms: el timestamp que devuelve `Cache` llega
~124 ms antes del instante de lectura, contra ~1 ms con `Device`. Frente al
intervalo de publicación UA (1000 ms), es despreciable.

Dos aclaraciones para no prometer de más:

- Matrikon estampa toda la tanda con el mismo instante en ambos modos, así que
  el timestamp por item que `Cache` podría dar en teoría acá no aparece. Otro
  servidor DA podría comportarse distinto.
- Una versión anterior de este archivo justificaba `Device` afirmando que
  `Cache` desincronizaba valor y timestamp por varios minutos. Medido después,
  es falso: el desfase se observaba idéntico en los dos modos, así que no era
  atribuible al origen de lectura. Esa parte del diagnóstico se sostuvo.
  Lo que no se sostuvo fue la causa que se le atribuyó después: hasta la Fase 6
  este documento afirmaba que el desfase era del simulador, que no refrescaría
  timestamps sin un cliente DA manteniéndolo despierto. **Es falso.** El desfase
  es un bug de conversión de `FILETIME` en el propio SDK, y por eso aparecía
  igual en los dos modos: ambos desembocan en el mismo conversor. Ver
  [bug-filetime-sdk.md](bug-filetime-sdk.md).

## 14. Una duda no se publica como `Bad`

Cuando el servidor DA rechaza el alta de un item, el rechazo tiene dos causas que
desde una sola respuesta no se distinguen: el ItemID no existe (permanente) o el
servidor todavía no terminó de levantar (transitorio). El gateway publicaba esa
ambigüedad como `NotConnected`, que es master `Bad`.

El problema apareció en la Fase 4, en la ventana de reconexión: un tag que venía
funcionando y era rechazado transitoriamente al reengancharse perdía en el cliente
UA el último valor conocido y su `SourceTimestamp`, y mostraba `Null` con una hora
fresca. Quedaba peor informado durante el reenganche que durante la caída, lo cual
es al revés de lo razonable.

La causa no está en la cache, que conservaba el valor previo correctamente, sino
en la especificación: **un `DataValue` con `StatusCode` de master `Bad` no
transporta valor**. El dato se declara inválido, así que el stack lo anula y
estampa la hora actual. Se verificó comparando, en el mismo instante, lo que la
cache entregaba al nodo contra lo que mostraba UaExpert: valor y timestamp buenos
de un lado, `Null` y hora fresca del otro. También explica por qué un tag que
nunca tuvo dato muestra un timestamp reciente en vez de 1601.

> **Actualización 05/10/2026 — la hora fresca no la causa el `Bad`.** La causa es
> que el stack reemplaza un `SourceTimestamp` en `MinValue` por la hora actual, con
> cualquier `StatusCode`: un tag sin dato muestra hora fresca también en
> `Uncertain`. Lo de que `Bad` no transporta valor sigue en pie. Ver V2-36 en
> [`v2/decisiones.md`](v2/decisiones.md).

La regla que resultó: **una muestra `NotConnected` no pisa un tag que ya tiene
valor.** Se conserva el estado previo sin refrescar `LastUpdateUtc`, de modo que
la antigüedad lo siga degradando hasta `LastUsableValue`, que es `Uncertain` y sí
transporta valor y timestamp. La duda se expresa entonces como incertidumbre, que
es lo que significa, y el cliente conserva el último dato bueno congelado.

Un rechazo confirmado en el reintento es otra cosa: llega como `ItemRejected` →
`BadConfigurationError` y ese sí pisa. Ahí ya no hay duda sino un error de
configuración, y perder el valor es correcto.

El alcance de la regla es deliberadamente estrecho. `NotConnected` no es una
respuesta del servidor DA sino un estado que fabrica el gateway; un `Bad` legítimo
que sí venga del DA (falla de dispositivo, fuera de servicio) se publica como
siempre. La cache es la única pieza que puede aplicar esta regla, porque es la
única que conoce el estado anterior.

---

## 15. Una sola foto alimenta los nodos UA y la página

El ciclo de publicación arma un `GatewaySnapshot` por vuelta y lo deposita en un
`SnapshotHolder`. Los nodos de diagnóstico y el endpoint HTTP leen **ese mismo
objeto**; ninguno arma el suyo.

El argumento de costo es el evidente —`Build` recorre la cache entera, así que un
F5 sobre 8.000 tags costaría un recorrido completo y diez clientes costarían
diez—, pero el que decide es el de correctitud: si cada vista armara su propia
foto, las dos discreparían justo cuando hay un problema y alguien las está
comparando. Un operador que ve 12 tags caídos en la página y 14 en el cliente UA
deja de creerle a las dos.

No hay lock porque el snapshot es inmutable: el lector ve la foto vieja entera o
la nueva entera, nunca una mezcla. La referencia es `volatile`, y no por
atomicidad —una referencia en x86 ya lo es— sino por visibilidad entre hilos.

El costo aceptado es que la página muestra datos de hasta un ciclo de antigüedad.
Se hace visible en la vista en vez de disimularlo: el encabezado muestra la hora
de la foto y los segundos transcurridos desde el último ciclo.

## 16. La tabla de tags no sale del snapshot

El endpoint de la tabla es el único que **no** lee la foto guardada: consulta la
cache en el momento, por la misma puerta que usa el node manager.

No es una inconsistencia con la decisión 15, es su límite. El snapshot contiene
agregados —cuántos tags en cada estado—, no la lista de tags, y no podría
contenerla: la tabla se filtra por búsqueda y por "solo degradados", parámetros
que el ciclo de publicación no conoce cuando arma la foto. Guardar todas las
listas posibles para todas las combinaciones de filtros sería absurdo.

La consulta vive en `Gateway.Core` y no en la capa web porque decidir qué cuenta
como degradado es la misma regla que aplica el snapshot; escrita en dos lugares
terminaría contradiciéndose, que es exactamente lo que la decisión 15 evita.

El valor se expone como **texto ya formateado en cultura invariante**, no como
número: según el CSV un tag puede ser `Double`, `Boolean` o `String`, y
serializarlo crudo daría un campo JSON que cambia de tipo según la fila. El formato es `"R"` y no `"G17"` — los dos hacen round-trip, pero `G17` fuerza 17
dígitos y mostraba `29973.389843749999` donde el valor real es `29973.38984375`.

## 17. Un tag mudo no es lo mismo que un tag con mala calidad

El diagnóstico se apoya en un contador de tags **mudos**, y mudo significa que no
se está refrescando, no que la calidad sea mala. Son cosas distintas: un tag que
llega `Uncertain` porque el sensor está fuera de rango está contestando
perfectamente, y contarlo como mudo diagnosticaría una caída donde solo hay ruido
de proceso.

Cuentan como mudos exactamente dos casos: los `Bad` —rechazado, no conectado, no
convierte— y el `UncertainLastUsableValue` que la propia cache fabrica por
antigüedad. Los `BadWaitingForInitialData` quedan afuera y en su propio contador,
porque al arrancar **todos** los tags están en ese estado y contarlos dispararía
un diagnóstico de falla en cada inicio.

Los mudos se parten después en dos buckets: los que nunca entregaron un dato y
los que entregaron antes y dejaron de hacerlo. Esa distinción es la que separa
"el ItemID no existe del otro lado" de "el servidor perdió sus items", que se
arreglan en lugares distintos.

El discriminante es barato y no requiere un campo nuevo: `ScaledValue` solo se
puebla con una muestra utilizable y ningún camino lo vuelve a `null`, así que un
`ScaledValue` no nulo significa que ese ItemID contestó alguna vez en la vida del
proceso. Eso es todo.

## 18. El diagnóstico es una opinión, y se trata como tal

Los contadores son mediciones; el `Diagnosis` que se deriva de ellos es una
heurística. Esa diferencia se sostiene en tres lugares:

- **Nunca se publica como nodo UA.** El address space es un contrato con los
  clientes; una opinión del gateway no va ahí. Los contadores sí se publican.
- **En la página se muestra siempre junto a los números que lo generaron.** El
  operador puede no estar de acuerdo con la conclusión y sacar la suya.
- **Cuando no hay una causa dominante, el gateway no elige.** Con los dos buckets
  parejos el resultado es `Indeterminate`, que dice "están pasando dos cosas a la
  vez" en vez de inventar una.

Los umbrales son 5% para no afirmar una causa global —dos tags mal tipeados en un
CSV de 8.000 no son un diagnóstico— y 90% para atribuirle la causa a un bucket.
No son magia ni están calibrados contra nada: son la formalización de "una amplia
mayoría", y están en constantes con nombre para que se discutan como lo que son.

El orden de evaluación importa y es el que se lee en `Diagnose`: primero el
vínculo, después la proporción. No tiene sentido preguntarse por el CSV cuando no
hay con quien hablar.

**El límite honesto, que se muestra en la propia página:** `LikelyCsvMismatch` no
puede distinguir entre "el ItemID está mal escrito en el CSV" y "el servidor DA
no tiene su configuración cargada". Desde adentro del gateway las dos causas son
indistinguibles si el tag nunca alcanzó a contestar durante la vida del proceso.
No es un bug ni algo a mejorar: es un límite de lo que se puede saber desde acá,
y por eso la vista nombra las dos causas en vez de una.

## 19. Los contadores del vínculo viven con la política de reconexión

El ciclo de adquisición dejó de ser un bucle suelto en el arranque y pasó a
`DaAcquisitionService`, que además de conectar, leer y volcar en la cache lleva
la cuenta de ciclos, fallos, conexiones y desconexiones.

Los contadores podrían haber vivido en `OpcDaTagSource`, que es quien toca COM.
No van ahí porque **el driver no decide cuándo reintentar ni cuánto esperar**: esa
política es de esta clase (decisión 8, el driver no tiene reloj propio). Contar
desde el driver dejaría afuera justo la mitad que importa para el diagnóstico —
cuántas veces se reconectó, cuánto lleva caído — porque el driver ni se entera de
que lo recrearon.

Los campos mutables se escriben desde el hilo DA y se leen desde el hilo que arma
el snapshot. Los `long` van con `Interlocked` y el resto `volatile`; los
`DateTime` se guardan como ticks porque `volatile` no admite structs de 8 bytes y
en un proceso x86 se pueden leer a medio escribir. El máximo usa
compare-and-swap: entre leer y escribir, otro hilo podría haber subido el valor y
lo estaríamos pisando.

Las duraciones se acumulan en **microsegundos** y no en milisegundos. Con 8.000
tags el ciclo ronda las decenas de ms, pero con diez tags da menos de 1 ms y un
promedio entero quedaría clavado en cero — que es un número que parece una
medición y no lo es.

## 20. Un vínculo vivo no prueba que el gateway esté leyendo

`LinkState` tiene un cuarto estado además de conectado, desconectado y
reconectando: `Stalled`. Es el caso del servidor DA colgado sin morir — COM no
falla, la conexión sigue abierta, la llamada simplemente no vuelve.

Sin ese estado la página mostraría "Conectado" con el hilo de adquisición
bloqueado, que es la peor mentira posible en un diagnóstico: dice que todo está
bien exactamente cuando nada lo está, y manda a buscar el problema a otro lado.

Se detecta comparando contra el instante en que arrancó el ciclo en curso, no
contra el último ciclo exitoso. El umbral es un múltiplo del intervalo
configurado y no un número fijo: con `UpdateRate` de 100 ms un segundo sin volver
ya es anormal, con 5000 ms es lo esperable.

**Lo que esto no hace: destrabar la llamada.** `_group.Read()` es síncrona y no
tiene timeout, así que `Stalled` reporta el cuelgue pero no lo cura. Un timeout
sobre COM sigue siendo deuda. La decisión acá es más modesta y vale igual: no
afirmar una salud que no se sabe.

## 21. La carpeta base de datos no es la raíz del repo

La decisión 6 ancló las rutas de configuración y PKI al archivo de solución,
subiendo desde la carpeta del ejecutable. Resolvía bien el problema de entonces:
cinco proyectos corriendo cada uno desde su propio `bin/`, contra una única
configuración compartida, sin depender del working directory.

Al empaquetar el gateway para distribuirlo apareció el límite. En una máquina sin
el repo no hay `Gateway.slnx` en ninguna carpeta padre, así que la búsqueda subía
hasta la raíz del disco y tiraba excepción **en el arranque** — el paquete no
llegaba a levantar.

Lo que el proceso necesita no es la raíz del repo: es la carpeta base donde viven
`config/` y la PKI. En desarrollo las dos coinciden, y por eso la distinción no
se veía. Corriendo desde un paquete publicado no hay nada arriba, y la carpeta
base correcta es la del ejecutable: el paquete es autocontenido. El método pasó a
llamarse `ResolveDataRoot()` y, cuando no encuentra el marcador, devuelve
`AppContext.BaseDirectory` en vez de fallar.

El fallback es silencioso a propósito. Podría avisar, pero el único caso en que
oculta algo es correr desde el repo con el `.slnx` renombrado — raro, y el
síntoma (una PKI nueva creada en `bin/`) es visible. A cambio, un paquete
distribuido arranca sin configuración previa, que es exactamente el escenario que
esto tiene que soportar.

`Resolve()` no se tocó: ya arranca desde la carpeta del ejecutable, así que con
el CSV en `config/` al lado del `.exe` lo encuentra en la primera iteración.

## 22. Los ItemIDs del CSV de ejemplo nunca existieron

El CSV de ejemplo mapeaba siete de sus diez tags contra ItemIDs con forma
`Simulacion.Grupo1.Item3`. Al empaquetar el gateway apareció que el servidor DA
los rechazaba —siete de diez tags fuera de servicio, semáforo ámbar— y la primera
hipótesis fue la anomalía ya documentada en `operacion.md`: el simulador
relanzado por COM vuelve con la configuración por defecto y pierde los aliases
importados.

No era eso. **Esos ItemIDs nunca existieron.** Matrikon rechaza puntos en el
nombre de un alias, porque en OPC DA el punto es el separador de jerarquía del
ItemID y un alias con puntos sería ambiguo para el servidor. Está escrito en
`pruebas-carga.md` desde la Fase 6, donde los 8.000 aliases se generan con
guiones bajos y punto inicial justamente por eso. El CSV de ejemplo es anterior y
nunca se alineó: sus nombres describían cómo se querían llamar los items, no cómo
podían llamarse.

Pasó desapercibido porque los tres tags que sí funcionaban apuntaban a items
nativos (`Random.Real8`, `Random.Real4`), que existen siempre. Con tres de diez
andando, la demo se veía viva y el resto se leía como un problema de
configuración del simulador.

La corrección es que ahora hay **dos archivos y no uno**: `config/tags.example.csv`
mapea UA↔DA para el gateway, y `config/aliases.example.csv` crea esos ItemIDs del
lado del simulador. Los diez tags pasan por alias, incluidos los tres que
funcionaban directo: mantener dos convenciones conviviendo en un archivo que se
lee para aprender a configurar confunde más de lo que ahorra.

**Lo que esto expone sobre el método.** El error sobrevivió desde la Fase 2 porque
la verificación de "listo" fue mirar que los valores cambiaran en UaExpert, y eso
lo cumplían los tres tags nativos. Un CSV de ejemplo es configuración, y la
configuración de ejemplo también se verifica: que un tag esté declarado no prueba
que exista del otro lado. El gateway, mientras tanto, se portó bien todo el
tiempo —rechazó los items, lo reportó por tag y siguió sirviendo el resto—, que
es exactamente lo que la Fase 3 pedía.

## 23. Los nodos de diagnóstico se publican siempre con calidad `Good`

Las tres carpetas `Gateway.Status`, `Gateway.Counters` y `Gateway.Performance`
publican sus variables con `StatusCode` `Good` en todos los casos, incluso cuando
lo que reportan es una falla. `SetDiagnostic` lo hace incondicionalmente: no hay
camino por el que un nodo de diagnóstico salga con otra calidad.

La razón es la misma física de la entrada 14, aplicada al caso simétrico. Ahí el
problema era que un tag rechazado transitoriamente perdía su último valor bueno;
acá sería peor: **el nodo que dice "el vínculo DA está caído" se vaciaría
justamente por decirlo.** Un `LinkState` publicado como `Bad` llega al cliente
como `Null`, y el operador que abre el árbol durante una caída encuentra vacío el
único nodo que iba a explicársela. La falla va en el contenido del valor, nunca en
la calidad del nodo que la reporta.

**La alternativa que se descartó** era reflejar el estado del vínculo en el
`StatusCode` de estos nodos, que a primera vista es más idiomático en UA: un
cliente que ya monitorea calidad se enteraría sin tener que interpretar un string.
Se descartó porque compra esa comodidad al precio de que el dato desaparezca
cuando más se necesita, y porque confunde dos cosas distintas: la calidad de un
nodo describe si *ese* dato es confiable, no si el sistema está sano.

El contraste con los nodos de tag no es una inconsistencia, es el mismo criterio
dando resultados opuestos. Un nodo de tag representa un dato que se originó en el
servidor DA, y el gateway puede no tenerlo o tenerlo dudoso; por eso ahí un
`StatusCode` no-`Good` es correcto y necesario, incluido el que se publica
mientras se espera la primera lectura. Un nodo de diagnóstico representa un dato
que se origina en el gateway mismo, y el gateway siempre conoce su propio estado:
aunque el DA esté caído, el número de reconexiones es un dato bueno. El corolario
está en el mismo método: en estos nodos el `SourceTimestamp` es la hora del
gateway, y está bien por la misma razón.

**El límite honesto:** un cliente UA que solo mire `StatusCode` nunca va a ver
alarma en estas carpetas. Para saber si el gateway está sano hay que leer
`LinkState`, `SecondsSinceLastCycle` o los contadores por calidad, o mirar los
nodos de tag, que sí degradan. Es una consecuencia buscada y no un descuido, y
tiene peso ahora que el gateway se distribuye empaquetado: quien lo reciba va a
conectarle un cliente que no escribimos nosotros, y conviene que esté documentado
en vez de que lo descubra durante una caída.


## 24. La auditoría de conexiones se engancha en dos lados

Contar intentos de conexión rechazados parecía un contador y resultó ser dos. El
certificado del cliente se valida al abrir el canal seguro, **antes de que exista
sesión**; el token de usuario recién al activarla. Son dos caminos distintos del
stack y no hay un punto único que vea los dos.

Por eso la auditoría se alimenta desde dos enganches: el evento
`CertificateValidation` del validador, y un override de `ActivateSessionAsync` en
`UaServer`. Los dos escriben sobre un único `UaAuditCounters`, que es el que
después leen las dos vistas.

La evidencia de que la partición era necesaria salió de la verificación: un
rechazo real por certificado untrusted quedó registrado con
`rejectedByCertificate: 1` y **`sessionsCreated: 0`**. Un diseño que contara todo
desde los eventos del `SessionManager` habría visto ese rechazo como si no
hubiera pasado nada.

**El detalle de método que casi lo arruina:** el primer override se escribió
contra `ActivateSession`, el método síncrono, que compila y nunca se llama —el
stack usa la variante async y marca la sincrónica como obsoleta. El contador
habría quedado en cero para siempre sin fallar jamás. Lo agarró un warning
`CS0618` que era perfectamente ignorable, y es la misma familia de error que el
bug de `FILETIME`: algo que anda y miente.

## 25. El certificado del propio servidor se descarta por thumbprint

> **Actualización 24/08/2026 — la causa descrita acá ya no ocurre.** El
> `BadCertificateHostNameInvalid` sobre el certificado propio era un bug del
> `SubjectName`, corregido: ver "El log decía domain not listed en cada
> conexión" en `operacion.md`. El filtro se conserva, pero hoy no filtra nada en
> la operación normal. Lo que sigue describe el estado en que se tomó la
> decisión.

El evento `CertificateValidation` no dispara solo por certificados de clientes.
Cuando un cliente conecta, el stack valida también **el certificado del propio
gateway** contra la URL que ese cliente mandó, y con el bind en `127.0.0.1` esa
validación fallaba con `BadCertificateHostNameInvalid` sin impedir la conexión.

Medido en la primera corrida de verificación: una conexión exitosa de UaExpert
produjo `rejectedByCertificate: 1`, con el subject `CN=OpcGatewayDaUa, C=AR,
O=Portfolio` — el nuestro. El contador estaba reportando un intento rechazado por
cada cliente que entraba sin problemas. Un contador de seguridad que suma cuando
todo sale bien no es un contador impreciso: es uno que hay que aprender a
ignorar, y a partir de ahí no sirve para nada.

El filtro compara el thumbprint del certificado en evaluación contra el del
certificado propio, leído después de
`CheckApplicationInstanceCertificatesAsync` porque antes de esa línea puede no
existir todavía. Se descarta por thumbprint y no por subject: el subject es texto
que otro certificado podría repetir, y el thumbprint es el hash del certificado
entero.

**El hallazgo colateral valió más que el filtro.** El `ERR` de dominio que
aparecía en cada conexión venía anotado como cabo suelto sin explicación, y esto
identificó *quién* validaba a *quién*: el servidor a sí mismo, contra la URL del
cliente. Quedaba sin saberse por qué `127.0.0.1` no matcheaba, y esa segunda
mitad recién se cerró dos días después leyendo el fuente del stack — ver el
blockquote de arriba.

**Por qué se conserva un filtro que hoy no filtra nada.** Corregido el
`SubjectName`, el certificado propio ya no falla la validación y no llega al
evento. Pero el filtro no depende de *esa* causa: dice que el certificado del
gateway no es un intento de conexión ajeno, y eso es cierto sea cual sea el
motivo por el que el stack decida validárselo. Sacarlo ahorraría cuatro líneas y
volvería a exponer el contador a cualquier autovalidación futura, incluida una
que aparezca por un cambio de configuración y no por un bug.

## 26. Solo se cuenta lo que es un rechazo

`ActivateSessionAsync` no falla únicamente por identidad rechazada. Por ahí salen
también fallas del ciclo de vida del servidor, y en la verificación se midieron
dos: `BadServerHalted` durante el arranque, y `BadSessionIdInvalid` cuando un
cliente reintenta con la sesión de una corrida anterior.

El primer diseño los mandaba a una categoría `Other`. Es honesto —algo falló al
activar una sesión— pero produce un número que no significa nada
operativamente: un contador de intentos rechazados que sube cada vez que se
reinicia el gateway con UaExpert abierto pierde lo único que tenía para decir,
que es cuántas veces alguien no pudo entrar.

Así que no se cuentan. Solo suman al contador de token los `StatusCode` que
significan identidad rechazada (`BadIdentityTokenInvalid`,
`BadIdentityTokenRejected`, `BadUserAccessDenied`, `BadUserSignatureInvalid`,
`BadIdentityChangeNotSupported`); el resto se relanza sin registrar. Es el mismo
criterio de la entrada sobre `Diagnosis`: no publicar como medición algo que es
ruido.

**El valor `RejectionCategory.Other` se conserva en el enum** aunque hoy nadie lo
escriba, para que un rechazo real que no sea ni certificado ni token tenga dónde
ir sin tocar el tipo.

**El límite honesto:** esos eventos dejan de ser visibles como número. Siguen en
el log del stack, pero quien quiera rastrearlos tiene que ir a leerlo.

## 27. El desglose por motivo no va al address space

La página de diagnóstico expone `rejectionsByReason`, un diccionario de
`StatusCode` a cantidad. Los nodos UA no: publican los agregados
(`UaRejectedByCertificate`, `UaRejectedByToken`, `UaRejectedTotal`) y el último
motivo como texto, y nada más.

Un diccionario de tamaño variable en el address space significaría crear nodos en
runtime, a medida que aparecen motivos nuevos. **El árbol UA es un contrato:** un
cliente que navegó el espacio de nombres al conectarse asume que lo que vio sigue
ahí. En JSON, en cambio, un objeto que crece no le rompe nada a nadie.

Los nodos nuevos van bajo `Gateway.Counters`, junto a los contadores del lado DA,
y no en una carpeta propia. Quien abre el diagnóstico todavía no sabe de qué lado
está el problema; separarlos lo obligaría a adivinar antes de mirar.


## 28. El cliente OPC DA es un repaquetado de terceros, no el paquete oficial

El SDK elegido en la Fase 0 es **`TitaniumAS.Opc.Client.NetCore` 1.0.2.1**, MIT.
No es el paquete oficial del proyecto: es un repaquetado publicado por un tercero
en septiembre de 2018, y era la única vía para consumir esa librería desde .NET
moderno.

El paquete oficial, `TitaniumAS.Opc.Client`, targetea `net40`. Desde
`net10.0-windows` solo se consumiría con `NU1701` —la advertencia de estar usando
un paquete de .NET Framework en .NET moderno—, que es una apuesta a que el
runtime resuelva en ejecución lo que el compilador ya avisó que no puede
garantizar. En un proyecto cuyo riesgo principal era justamente el interop COM, no
era el lugar para arrancar debiendo una advertencia. **No se escribió código
contra él**: se descartó por análisis, y eso es todo lo que la Fase 0 hizo al
respecto.

**La alternativa que se descartó** fue Technosoftware OPC DA/AE/HDA Client
Solution .NET, que targetea `net6`/`net7`/`net8` y se habría consumido sin fricción
—el framework de una dependencia es piso, no techo—. Quedó anotada como plan B
por si el interop COM se rompía, y nunca se ejerció. Su licencia para quien no
compra una comercial es GPL 3.0, así que usarla habría arrastrado el repositorio
entero a GPL. Ver decisión 29.

**Qué se verificó antes de confiar en un repaquetado.** Que el proyecto original
es MIT y su código es público; que el paquete llevaba ocho años en NuGet sin
reportes de abuso ni CVEs; que no trae binarios nativos ni hace red por su cuenta.
Para una PoC que corre en una máquina de desarrollo, alcanza. Si esto fuera a una
planta real, la respuesta correcta sería compilar desde el fuente y firmarlo uno
mismo.

**El precio se cobró, y no fue teórico.** Un repaquetado queda fuera del canal de
correcciones del proyecto original: el bug de `FILETIME` que costó cuatro fases
está arreglado en el repositorio oficial desde hace años y nunca llegó al paquete
que consume el gateway, porque nadie volvió a publicarlo. Se verificó que no
existe ningún paquete de NuGet con el fix. La corrección terminó siendo propia y
acotada al borde del driver. Detalle en
[`bug-filetime-sdk.md`](bug-filetime-sdk.md).

**Las dos restricciones que impone, y que se pagan en todo el proyecto:** el
proceso tiene que estar en apartment MTA y `Bootstrap.Initialize()` tiene que
correr antes que cualquier otra llamada COM. Ver decisiones 8 y 9.

## 29. La licencia del repositorio la eligió el SDK, no el autor

El repositorio es **MIT**, y lo es porque el SDK cliente DA lo es. La licencia
quedó determinada en la Fase 0, antes del primer commit, y por eso la Fase 0 fue
primero: el `LICENSE` va antes de que exista código, y no se podía escribir sin
saber qué librería iba a entrar.

Con el plan B de la decisión 28 el resultado habría sido otro. Technosoftware es
GPL 3.0 para quien no compra licencia comercial, y la GPL es viral: un repositorio
público que la enlaza se distribuye bajo GPL. No habría sido un error —es una
licencia legítima y se habría documentado como consecuencia deliberada—, pero
cambia qué puede hacer con esto quien lo lea.

Elegido el SDK permisivo, MIT es la opción que no agrega restricciones propias. La
alternativa real era una licencia más restrictiva que la de la dependencia, y
sobre un proyecto de portfolio que existe para ser leído y copiado no tenía
sentido: restringir a un lector algo que la librería subyacente le permite es
costo sin beneficio.

**Lo que esto deja como criterio general:** la licencia de un repositorio no se
elige al publicarlo, se hereda de la dependencia más restrictiva que se haya
aceptado. Conviene mirarla cuando se elige la librería, que es cuando todavía hay
alternativas, y no cuando ya hay tres mil líneas escritas encima.

## 30. Los dos endpoints escuchan solo en loopback

El servidor OPC UA (`4840`) y la página de diagnóstico (`8080`) hacen bind en
`127.0.0.1`, no en `0.0.0.0`. El gateway solo acepta clientes de la misma máquina.

Es coherente con la restricción que ya tenía el proyecto del otro lado: el
servidor DA se consume por COM local, sin DCOM remoto. Un gateway que no puede
alcanzar un servidor DA remoto pero que se expone a toda la red ofrece superficie
sin ofrecer alcance. Y la PoC no gana nada con la exposición: todo lo que hay que
demostrar se demuestra con UaExpert corriendo al lado.

**Se comprobó contra el socket, no contra la configuración.** Que el
`appsettings.json` diga `127.0.0.1` prueba que se pidió el bind correcto, no que
haya ocurrido: un default del stack, una `BaseAddress` extra o una URL reescrita
producen un socket distinto del que dice el archivo, sin ningún error. La
verificación fue mirar la tabla de conexiones TCP del sistema con el gateway
corriendo y confirmar que el `LocalAddress` de los dos listeners es `127.0.0.1`.
La trampa está anotada en `verificacion.md`: el `0.0.0.0` que aparece en
`RemoteAddress` de un listener es relleno de Windows para un socket sin
contraparte, y leerlo como un bind abierto —o al revés— es fácil en la dirección
peligrosa.

**El costo apareció enseguida y se pagó.** Con el bind cerrado no hay forma de
conectar por hostname, y ese era justamente el experimento que habría refutado la
hipótesis equivocada sobre el aviso de dominio del certificado. La decisión fue no
abrir el bind para poder correrlo. Se resolvió por otro camino —leer el fuente del
stack— y el caso está contado en `verificacion.md`, sección "Sobre el método".
Vale como precedente: una restricción de seguridad que se sostiene también cuando
molesta es una restricción; una que se levanta para depurar es una sugerencia.

**El límite honesto:** nada de esto se probó contra un cliente UA en otra máquina,
porque no puede probarse. Abrir el gateway a la red es un cambio de una línea en
la configuración, pero deja de ser el sistema que se verificó — habría que revisar
al menos el certificado, que hoy declara los dominios de esta máquina.