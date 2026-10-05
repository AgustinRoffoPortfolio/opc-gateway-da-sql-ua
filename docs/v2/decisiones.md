# Decisiones de diseño — v2

Registro de decisiones de la fuente SQL. Continúa el criterio de `../decisiones.md`:
**los números son identificadores estables, no se renumeran ni se reordenan**, aunque
una decisión quede superada. Cuando eso pasa, la entrada vieja se marca y apunta a la
que la reemplaza.

Los identificadores llevan prefijo `V2-` para que no se confundan con los del registro
de la v1, que se referencian sin prefijo.

---

## V2-1. El motor no se elige: viene dado

SQL Server no salió de una comparación técnica. Es un requisito (R1): la tabla existe,
está en producción, y otra aplicación la escribe. El gateway se adapta a ella.

Esto descarta de entrada la comparación con PostgreSQL que hubiera correspondido en un
proyecto nuevo, y descarta también cualquier discusión sobre el esquema. La tabla no se
diseña: se replica tal cual está (P1).

Lo que sí queda como trabajo propio es todo lo que pasa del lado del gateway: cómo se
lee, cada cuánto, qué se hace con una fila que no cambia, y cómo se traduce al modelo
OPC UA sin inventar información que el dato no tiene.

## V2-2. Cliente: `Microsoft.Data.SqlClient` 7.0.2

El driver ADO.NET oficial de Microsoft para SQL Server. Tres cosas lo justifican y las
tres se verificaron antes de adoptarlo:

- **Licencia MIT.** No aprieta la licencia heredada, que en este repo la fija la
  dependencia más restrictiva.
- **Funciona en x86.** El host del gateway compila en 32 bits por el COM interop de OPC
  DA, y este driver lleva una parte nativa en Windows. Verificado con un probe
  compilado en x86: el proceso carga la biblioteca de `runtimes/win-x86/native`,
  confirmado por los módulos cargados del proceso y no deducido del tipo de error.
- **Es el camino soportado.** `System.Data.SqlClient` está deprecado y las
  características nuevas solo llegan a este paquete.

Se fijó 7.0.2 y no la última publicada, para trabajar sobre una versión con rodaje.

*Aclaración del 02/10/2026, revisando licencias para el README.* El paquete es MIT,
pero la parte nativa que se verificó en x86 viene en una dependencia aparte,
`Microsoft.Data.SqlClient.SNI.runtime` 6.0.2, que no es MIT: va bajo los términos de
licencia de software de Microsoft para código redistribuible (se puede distribuir
dentro de una aplicación, no suelta). No es copyleft y no está en el repositorio,
así que no cambia la licencia del repo; sí aplica a quien redistribuya el paquete
compilado. Las otras 21 transitivas que lista hoy `project.assets.json` de
`Gateway.Sql` son MIT.

**Nota de peso:** arrastra 17 dependencias transitivas, varias de identidad de Azure que
este proyecto no usa. No es un problema de funcionamiento, pero es un argumento más para
que el driver SQL viva en su propio proyecto: así ese árbol queda contenido detrás del
borde y no se filtra al resto de la solución.

**Pendiente:** si se suma Dapper o se usa ADO.NET directo. Se decide al diseñar el
driver, y la carga de la prueba la tiene Dapper: con una sola consulta de cuatro columnas,
una dependencia más tiene que justificarse.

## V2-3. La base de desarrollo corre en un contenedor

SQL Server 2019 Developer Edition, imagen oficial `mcr.microsoft.com/mssql/server:2019-latest`,
en un contenedor con volumen persistente.

Por qué contenedor y no SQL Server Express instalado en Windows:

- **Un comando lo levanta.** Quien clone el repo reproduce el entorno sin instalador ni
  instructivo. Es la diferencia entre una línea en el README y una página.
- **La edición se parece al destino.** La imagen corre Developer, que en funcionalidad
  equivale a Enterprise. Express es la edición recortada, y el servidor real es 2019 o
  2025 completo (P8). Desarrollar contra Developer acerca el entorno de desarrollo al de
  destino.
- **Se borra sin dejar rastro.** No queda un servicio de Windows corriendo después del
  proyecto.

Por qué la etiqueta `2019` y no `2025`: el entorno de destino puede ser cualquiera de las
dos (P8), y desarrollar contra la más vieja evita apoyarse sin querer en algo que exista
solo en la nueva. Lo que corre en 2019 corre en 2025; al revés no está garantizado.

El costo es 2 GB de RAM mientras el contenedor está arriba. En una máquina de gama media
eso obliga a no tener otros motores de base corriendo en paralelo.

## V2-4. El cifrado de la conexión es un parámetro, no una constante

El driver exige cifrado por defecto (`Encrypt=True`) y valida quién emitió el certificado
del servidor. Contra un servidor que se autofirmó el suyo —el contenedor local, y
probablemente también el de TEST— esa validación falla y la conexión no se abre.

Se resuelve con `TrustServerCertificate`, que mantiene el tráfico cifrado pero omite la
validación del emisor. La decisión no es *usarlo*, sino **dónde vive el valor**: en la
configuración (R6), nunca fijo en el código.

El motivo es que la respuesta correcta cambia según el servidor. En una base local el
riesgo es nulo. Contra un servidor real, omitir la validación protege de que alguien
escuche el tráfico, pero no de que alguien se haga pasar por el servidor. Ahí la decisión
correcta puede ser distinta, y tiene que poder tomarse editando un JSON y no
recompilando (P10).

Los dos comportamientos se verificaron contra el contenedor: con el valor por defecto la
conexión falla durante el inicio de sesión por el certificado autofirmado, y con la
validación del emisor omitida abre y el servidor responde.

### V2-5 — Columna de origen en el CSV de tags

**Decisión.** El CSV pasa a 12 columnas y se lee por nombre de cabecera, no por posición:

```csv
TAG_NAME_OPC_UA;SOURCE;SOURCE_TAG;DATA_TYPE;MULTIPLICADOR;OFFSET;EU;SCAN_RATE_MS;DEADBAND;ACCESS_LEVEL;DESCRIPTION;ENABLED
```

- `SOURCE_TAG` reemplaza a `TAG_NAME_OPC_DA`. Es el identificador del tag **en su fuente**, y `SOURCE` dice cómo interpretarlo: un ItemID para `OPCDA`, el valor de la columna `TAG` de la tabla para `SQL`.
- Valores aceptados de `SOURCE`: `OPCDA`, `OPC_DA` y `SQL`, sin distinguir mayúsculas y con `Trim()`. Los CSV del repositorio se escriben con `OPCDA`.
- `SOURCE` vacío o desconocido es error de carga. No hay default a OPC DA.
- En `TagDefinition`: `OpcDaName` pasa a `SourceTag`, y se suma `Source` (enum `TagSource { OpcDa, Sql }`) **sin valor por defecto**.

**Por qué una sola columna de nombre de origen y no una por fuente.** R2 plantea el nombre del lado SQL como el análogo del nombre del lado DA: es el mismo concepto. Con una columna por fuente (`TAG_NAME_OPC_DA` y `TAG_NAME_SQL`) el CSV admite filas contradictorias —las dos llenas, o la de la fuente equivocada— y cada fuente futura agrega una columna más. Con `SOURCE` + `SOURCE_TAG` el esquema no cambia.

**Por qué lectura por cabecera.** `CsvTagLoader` leía por índice (`fields[0]`–`fields[10]`) y salteaba la primera fila sin mirarla: la cabecera era decorativa. Como el CSV cambia de forma en esta versión, leer por posición convierte un error de edición en un gateway que arranca cargando campos cruzados en silencio. Leyendo por nombre se puede además detectar la cabecera de la v1 y decir en el error qué columna renombrar. El costo es tocar un archivo de la v1 que hoy funciona y tiene tests.

**Por qué `SOURCE` obligatorio y sin default.** Un default a OPC DA repite el pecado que la Fase 0 encontró en `TagsCsvPath`: el gateway arranca contento con un tag mal clasificado, y desde OPC UA ese tag se ve igual que una fuente caída. Que falle la carga es más barato que diagnosticarlo en `UaExpert`.

**Costo medido.** Seis apariciones de `new TagDefinition(` u `OpcDaName` en cuatro archivos: `CsvTagLoader.cs`, `TagCache.cs`, `TagDefinition.cs` y `TagCacheTests.cs`.

**Detalle de implementación.** `SOURCE` no puede usar el `ParseEnum<TEnum>` genérico: `Enum.TryParse` con `ignoreCase` resuelve `OPCDA` contra el miembro `OpcDa`, pero no `OPC_DA`, que R2 acepta. Necesita normalizar el guion bajo antes de resolver.

**Implementado** en `f4f21bd` (Fase 4): `CsvHeader.cs` valida la cabecera, `CsvTagLoader` lee por nombre, y `tags.example.csv`, `demo-500.tags.csv` y `Generate-LoadTestTags.ps1` pasaron a 12 columnas, todas con `SOURCE=OPCDA`. Lo verificado son los tests: todavía nadie levantó el gateway con estos CSV contra Matrikon, y ningún CSV del repositorio declara una fila `SQL`.

**Pendientes cerrados.** Los dos que este bloque dejaba abiertos ya estaban resueltos en la Fase 1 y la nota había quedado vieja. `DATA_TYPE` en una fila SQL lo cierran V2-14 (un analógico se declara `Float`) y V2-15 (un booleano llega como número), y qué conjunto de tipos se acepta lo cierra V2-24. `SCAN_RATE_MS` y `DEADBAND` los cierra V2-22: quedan en su default, el gateway los ignora y el validador avisa sin rechazar la fila.

**Flojedades conocidas de la implementación**, anotadas en la revisión del código y no corregidas porque ninguna cambia el comportamiento con una cabecera bien escrita:

- Tres de los cuatro errores de cabecera (columna faltante, desconocida y repetida) no tienen test. El único caso cubierto es el de la cabecera de la v1.
- En `SourceAusente_EsErrorDeCarga`, el `Assert.Contains("SOURCE", ...)` también pasaría si el mensaje hablara de `SOURCE_TAG`: una cadena contiene a la otra.
- `ParseSource` normaliza con `Replace("_", "")`, que saca todos los guiones bajos y no solo el de `OPC_DA`, así que acepta `S_Q_L`. Se deja: arreglarlo bien cuesta más que el problema.
- El mensaje de error de `ParseSource` lista los valores aceptados a mano, no con `Enum.GetNames` como hace `ParseEnum`. Si el enum suma una fuente, el mensaje miente. El motivo de la copia es que `OPC_DA` no sale de `GetNames`.

---

### V2-6 — Parámetros de la fuente SQL en el JSON

**Decisión.** Sección `Sql` en `appsettings.json`, con los parámetros de R6 como claves sueltas:

```json
"Sql": {
  "Host": "127.0.0.1",
  "Port": 1433,
  "Database": "PLANT_DB",
  "Schema": "dbo",
  "Table": "CURRENT_VALUES",
  "User": "",
  "Password": "",
  "PollingIntervalSeconds": 30,
  "ReconnectDelaySeconds": 15,
  "CommandTimeoutSeconds": 10,
  "Encrypt": true,
  "TrustServerCertificate": false,
  "Columns": {
    "TagName": "TAG",
    "Timestamp": "TS",
    "Value": "V",
    "Quality": "Q"
  }
}
```

**Por qué partes sueltas y no una connection string.** Lo pide R6, y además habilita V2-7: si la cadena fuera un único string, sacar la password del repositorio obligaría a sacar con ella host, base y todo lo demás, y se perdería la configuración versionada y legible. Con partes, el override local lleva dos claves. La cadena se arma en ejecución con `SqlConnectionStringBuilder`, que escapa los valores: una password con `;` o comillas no rompe la cadena, la reinterpreta.

**`CommandTimeoutSeconds` es un agregado sobre R6.** El default de `Microsoft.Data.SqlClient` es 30 s, más que el polling mínimo de R4 (20 s), así que una consulta lenta encimaría ciclos. Sin timeout propio no se puede sostener el invariante 8 (una fuente caída no frena a la otra). Valor inicial 10 s, con margen sobrado para una tabla de 10.000 filas.

**Segundos y no milisegundos**, contra el patrón `...Ms` de la v1: lo piden R4 y R5, y un `30000` invita a que se escape un cero en un parámetro que nadie revisa dos veces.

**Sin clave `Enabled`.** Crearía un estado contradictorio: `Enabled: false` con tags `SQL` en el CSV dejaría esos tags en `Bad` sin que nada lo explique. Manda el CSV: si ningún tag tiene `SOURCE=SQL`, el driver no arranca.

**Cifrado (aplica V2-4).** Los valores versionados son los seguros: `Encrypt: true`, `TrustServerCertificate: false`. El contenedor local usa certificado autofirmado y necesita `TrustServerCertificate: true`, que va en el override local junto con las credenciales. No agrega fricción, porque sin override el gateway no conecta de todos modos.

**Validación al arranque, en dos niveles.** `Host`, `User` o `Password` vacíos: falla con mensaje explícito, que además nombra el override local de V2-7, porque en una máquina nueva ese es el primer error que aparece y sin la pista parece un olvido de configuración. Los intervalos se validan contra dos rangos distintos:

- **Rango duro de sanidad** —mayor que cero y hasta una hora—: es error y el gateway no arranca. Un cero no es "fuera de lo esperado", es un loop que no puede funcionar, y un valor desmedido es un cero de más en un parámetro que nadie revisa dos veces.
- **Rango esperado de R4 y R5**: es advertencia y el gateway arranca igual, porque los requisitos dan rangos de operación esperados y no límites del sistema.

**Por qué dos niveles y no el rango de los requisitos como límite duro.** Con R4 duro, un polling de 5 s no arrancaría, y ese es exactamente el valor que hace falta para demostrar el gateway en el video y para medir tiempos de detección y recuperación en la Fase 5 sin esperar medio minuto por prueba. También ataría el gateway a rechazar una configuración que mi padre podría pedir mañana por un número que elegimos nosotros y no él. El costo de la advertencia —que un log que nadie lee es lo mismo que nada— se acota con el rango duro, que sí atrapa lo absurdo.

**Dos validaciones más, no previstas acá y agregadas al escribir el código**, las dos por ser errores silenciosos:

- Advertencia si `CommandTimeoutSeconds` es mayor o igual que `PollingIntervalSeconds`: una consulta lenta encimaría ciclos, que es el invariante 8.
- Advertencia si el cifrado está relajado (`TrustServerCertificate: true`) o apagado (`Encrypt: false`). Es la configuración esperada contra la base local con certificado autofirmado, y sería un agujero callado contra un servidor real.

**Forma de la validación.** Es una función pura que devuelve dos listas de texto ya redactado, sin logger y sin excepciones: el host tira los errores y loguea las advertencias. Se testea sin base y sin logger, igual que `TagQuality.FromDaCode` en V2-19.

**Pendiente, cerrado por V2-9.** El armado de `[Database].[Schema].[Table]` se resolvió con `SqlIdentifier`: se valida la forma del identificador y se escapa al concatenar. El validador rechaza al arrancar cualquiera de las tres claves que no sea un identificador válido.

---

### V2-7 — Override local de credenciales (resuelve el choque C1)

**Decisión.** Los valores reales de `Sql:User` y `Sql:Password` no se versionan. En `appsettings.json` quedan vacíos y se pisan con **user-secrets** en desarrollo, y con **variables de entorno** en TEST.

- `Gateway.Host.csproj` lleva un `UserSecretsId` generado en este repositorio, nunca reutilizado de la v1. El identificador no es un secreto: solo indica en qué carpeta buscar.
- `Program.cs` registra la fuente después del JSON. El sistema de configuración de .NET superpone fuentes clave por clave: la última registrada gana.
- En TEST (P10) las mismas claves van como `Sql__User` y `Sql__Password`, con doble guion bajo. Mismo mecanismo, cero cambios de código.

**Por qué user-secrets y no un `appsettings.local.json` ignorado.** El archivo ignorado depende de que el `.gitignore` esté bien y de que nadie lo fuerce con `git add -f`. User-secrets guarda en `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`, fuera del árbol del repositorio: no está ignorado, no está. Es la diferencia entre no cometer el error y confiar en la red que lo atrapa.

**Riesgo conocido.** El helper `AddUserSecrets` de .NET carga solo en entorno `Development`. Si el gateway corre sin `DOTNET_ENVIRONMENT`, el default es `Production` y los secretos no se leen. La validación de V2-6 hace que eso falle con un mensaje claro en vez de arrancar callado, pero igual conviene registrar la fuente sin condicionarla al entorno. A verificar contra el `Program.cs` real.

**Cómo se resuelve el choque C1.** El requisito pide tratar usuario y password como parámetros de configuración más, y lo son: tienen su clave en el JSON igual que el puerto. Los estándares del portfolio prohíben versionar credenciales, y no se versiona ninguna. Ninguna de las dos partes cede. Si más adelante se pide el valor escrito en el JSON del repositorio, gana el requisito y esta decisión queda anotada como revertida.

**Fuera de alcance.** El tratamiento definitivo (cifrado de la configuración, autenticación de Windows, gestor de secretos) está explícitamente pospuesto.

---

### V2-8 — Dónde vive el driver SQL y qué referencia

**Decisión.** Proyecto nuevo `src/Gateway.Sql`, espejo estructural de `Gateway.Da`: referencia de proyecto a `Gateway.Core` y paquete `Microsoft.Data.SqlClient`, nada más. No referencia a `Gateway.Da` ni al revés. `Gateway.Host` sigue sin referenciar el paquete SQL: solo referencia el proyecto.

**Por qué un proyecto propio y no dentro de `Gateway.Core` o de `Gateway.Host`.** Es el principio 1, y lo que lo hace real es el grafo de referencias, no la disciplina al escribir. `Gateway.Core` lo referencian todos: meter el cliente SQL ahí lo pondría al alcance de `Gateway.Ua` y `Gateway.Web`, que no tienen nada que hacer con él, y el borde dejaría de existir. `Gateway.Host` es todavía peor, porque es exactamente el proyecto que no debe ver un `SqlDataReader`. Con un proyecto aparte, escribir `SqlConnection` fuera del driver no compila.

**Verificación del borde.** La forma de comprobarlo no es leer código sino intentar romperlo: una referencia a un tipo de `Microsoft.Data.SqlClient` desde `Gateway.Host` tiene que fallar el build. Se prueba una vez en la Fase 3 y se anota en `verificacion.md`.

**`TargetFramework` `net10.0`, sin el sufijo `-windows`.** `Gateway.Da` apunta a `net10.0-windows` porque COM solo existe en Windows. Nada del driver SQL toca COM, así que no hereda esa restricción. Dejarlo en `net10.0` documenta en el propio `.csproj` que esta fuente no depende de la plataforma, y deja marcado cuál es el proyecto que ata el gateway a Windows: el DA, no este. `Gateway.Host` sigue siendo el único con `PlatformTarget x86` (principio 7); el driver SQL no fija plataforma y se compila para la del host.

**Costo real, medido al crear el proyecto en la Fase 3.** Esta decisión decía "costo cero" y no lo era. Un proyecto sin sufijo no puede referenciar a uno con sufijo, y `Gateway.Core` era `net10.0-windows`, así que `Gateway.Sql` no podía referenciarlo. Se resolvió bajando `Gateway.Core` a `net10.0` en vez de subir `Gateway.Sql` a `-windows`: `Core` son tipos puros sin ninguna dependencia externa, el sufijo era arrastre de la v1 (donde todo el árbol era Windows porque el borde DA lo obligaba), y subir `Sql` habría contradicho el porqué de arriba, borrando la señal de cuál es el proyecto que ata a Windows. La compatibilidad va en una sola dirección: un proyecto `-windows` sí puede referenciar uno neutral, así que `Gateway.Da`, `Gateway.Ua`, `Gateway.Web`, `Gateway.Host` y `Gateway.Tests` no se tocaron. Verificado con `dotnet build` de la solución y 79 tests en verde: ningún archivo de `Core` usaba una API exclusiva de Windows. Costo: una línea del `.csproj`.

**Qué expone.** `SqlTagSource`, clase pasiva con la misma forma que `OpcDaTagSource`: `Connect()`, `IsConnected`, `ReadAll()` devolviendo `IReadOnlyDictionary<string, TagSample>`, e `IDisposable`. Sin hilos, sin temporizadores, sin política de reintentos adentro. El porqué está en V2-10.

**Pendiente, cerrado.** Es `SqlOptions`, dentro de `Gateway.Sql`, como se preveía por simetría con `DaOptions`. Con `SqlColumnOptions` anidada para los nombres de columna de R6, que en el JSON son un objeto y el binder necesita un tipo que mapear.

---

### V2-9 — Nombre de tabla configurable sin abrir la puerta a inyección

**Decisión.** `Database`, `Schema` y `Table` se validan al arrancar contra una lista blanca —letra o guion bajo inicial, después letras, dígitos o guion bajo, hasta 128 caracteres— y se arman como `[Database].[Schema].[Table]` duplicando cualquier `]` interno. Si alguno no valida, el gateway falla al arrancar con el nombre de la clave y el valor recibido. No se corrige ni se sanitiza: se rechaza.

**Por qué no alcanza con parámetros.** Un parámetro de ADO.NET (`@algo`) ocupa el lugar de un *valor*, no de un identificador. `SELECT * FROM @tabla` no es SQL válido. El nombre de la tabla se arma pegando texto por definición, y pegar texto es el mecanismo de la inyección. Como no se puede evitar la concatenación, lo que se controla es qué se concatena.

**Por qué igual se valida, si el atacante sería quien edita el JSON.** Quien edita `appsettings.json` ya tiene la máquina; no es un anónimo de internet, y por ese lado el riesgo real es bajo. La validación se sostiene por otras dos razones. Una operativa: convierte un typo en un error claro al arrancar, en vez de en un error de sintaxis de SQL treinta segundos más tarde y a través del log del driver. Otra defendible en entrevista: la diferencia entre "es seguro porque nadie malicioso toca el JSON" y "es seguro porque no acepta nada que no sea un identificador". Solo la segunda es una propiedad del código.

**Por qué no `QUOTENAME`.** Delegarle el escape a SQL Server obliga a una consulta extra solo para construir la consulta, y pone la validación del otro lado de la red cuando puede estar acá, antes de abrir la conexión. Los corchetes con `]` duplicado son la misma regla de `QUOTENAME`, aplicada localmente.

**Consecuencia sobre R6.** El requisito pide que la tabla sea configurable, y lo sigue siendo: cualquier identificador legítimo de SQL Server pasa. Lo que queda afuera son nombres con espacios, puntos o caracteres raros, que en esta tabla no existen. Si alguna vez aparece uno, se amplía la lista blanca como decisión nueva, no aflojando la validación en el momento.

**Cierra el pendiente de V2-6** sobre cómo armar `[PLANT_DB].[dbo].[CURRENT_VALUES]` desde tres claves sueltas.

---

### V2-10 — Aislamiento del loop SQL (invariante 8)

**Decisión.** El driver SQL es pasivo y el hilo lo aporta el host, igual que con DA: un `Thread` dedicado, `IsBackground = true`, `Name = "SQL polling"`, sin `SetApartmentState`. La política —cada cuánto consultar, cuándo reconectar, qué hacer con un tag ausente— vive en un `SqlAcquisitionService` dentro de `Gateway.Host`, espejo de `DaAcquisitionService`. La cache es lo único compartido entre los tres hilos: adquisición DA, adquisición SQL y publicación UA.

**Qué se descubrió mirando el código de la v1.** `OpcDaTagSource` no tiene loop, ni hilo, ni `Task`: es sincrónico y pasivo. El `Thread` está en `Program.cs` y el `while` con espera bloqueante, en `DaAcquisitionService`. O sea que el invariante 8 ya estaba implementado para una fuente antes de llamarse así, y el comentario del propio `Program.cs` lo dice: el apartamento MTA que exige COM, y que una lectura DA lenta no frene la publicación UA. Sumar SQL no inventa un patrón: agrega un tercer hilo al que ya existe.

**Por qué un hilo bloqueante y no `async`/`await`.** Es ir a contramano de `Microsoft.Data.SqlClient`, que expone todo en versión `Async`, y conviene decirlo en voz alta. El argumento clásico a favor de `async` es no desperdiciar un hilo del pool esperando E/S, y vale cuando hay miles de operaciones concurrentes. Acá hay tres hilos en total y uno durmiendo treinta segundos no le saca lugar a nadie. Lo que se gana a cambio es un único modelo de concurrencia para las dos fuentes: un solo patrón que explicar, que mantener y que depurar, en vez de dos conviviendo. Si el driver SQL creciera a varias consultas concurrentes, la decisión se revisa.

**Por qué no un hilo compartido que alterne DA y SQL.** Viola el invariante 8 por construcción: una consulta SQL lenta frenaría la lectura DA. Queda descartado.

**El hilo propio no alcanza solo.** Protege a DA de que SQL se demore, pero no protege al hilo SQL de quedar colgado para siempre en una consulta que nunca vuelve. Eso lo cierra el `CommandTimeoutSeconds` de V2-6: con timeout, una consulta trabada termina en excepción, entra al camino de reconexión de R5 y el loop sigue vivo. Sin timeout, el hilo SQL se cuelga callado y los tags SQL se congelan sin que nada lo reporte.

**Qué queda para la Fase 3.** La degradación por antigüedad de la v1 vive en `DaAcquisitionService` y es global (3 ciclos, 3000 ms). Si `SqlAcquisitionService` es un servicio aparte, no la hereda por accidente, que es justo lo que se quiere según lo anotado en la sección C: para los tags SQL la calidad sale de `Q` (P6), no de la antigüedad. Falta escribir esa decisión con su porqué y ver qué reporta el `LinkState` de la fuente SQL.

---

### V2-11 — La degradación por antigüedad pasa a ser por tag

**Decisión.** El umbral de antigüedad deja de ser único de la cache y pasa a viajar por tag. `TagDefinition` suma `TimeSpan? StaleAfter = null` al final del record, donde `null` significa **no degradar**. `TagCache` guarda ese valor por tag y `Degrade` lo compara contra él; si es `null`, devuelve el estado intacto. El constructor conserva la firma actual: el `TimeSpan` que ya recibe pasa a ser el default para los tags que no traen el suyo. Quien decide el valor es el cargador del CSV: una fila con `SOURCE=OPCDA` hereda el default, una fila con `SOURCE=SQL` va en `null`.

**El problema, dicho con precisión.** La degradación no mide que el valor no cambie: mide que el gateway no reciba noticias. Con OPC DA se lee la cache entera cada 1000 ms y llegan todos los tags, cambien o no, así que una presión clavada durante media hora se refresca 1800 veces y nunca degrada. Lo que dispara la degradación es que el servidor DA deje de contestar. Un tag SQL, en cambio, recibe noticias cada 20 a 60 s por diseño (R4), no por falla. Con el umbral actual de 3000 ms, un tag SQL leído cada 30 s pasaría el 90 % del tiempo en `Uncertain` sin que nada esté mal.

**Por qué no se sube el umbral global.** Fue la primera salida que se evaluó, y es la que más cuesta. Subirlo a 90 s se lo aplica también a los tags DA: el gateway tardaría un minuto y medio en avisar que perdió el servidor DA, publicando mientras tanto valores viejos marcados como `Good`. Un `Uncertain` de más hace mirar; un `Good` mentiroso hace decidir sobre un dato que no existe. Y además no arregla SQL: con umbral 30 s y polling 30 s, cualquier demora de la consulta o jitter del hilo cruza el borde y aparece un `Uncertain` intermitente, lo que empuja a subirlo otra vez hasta que la degradación ya no detecta nada. El fondo es que 3 s y 30 s no son el mismo umbral mal calibrado sino dos ritmos legítimamente distintos conviviendo en la misma cache; un solo número no le sirve a los dos, se elija el que se elija.

**Por qué para SQL es `null` y no un umbral largo.** Porque para esa fuente la antigüedad no aporta información: la aporta la propia tabla. Cuando la aplicación de origen pierde el campo, marca `Q` como calidad mala del tipo *Last Known Value* (P6). Eso es un hecho reportado por el origen, no una inferencia del gateway. Poner además un umbral propio sería inventar una segunda señal, peor fundada, que podría contradecir a la primera. Y como cada tag se refresca al ritmo de su grupo de scan (P7), no existe un número único que sirva para todos los tags SQL.

**Por qué el umbral no se deriva de `ScanRateMs`.** Tienta, porque `TagDefinition` ya tiene ese campo. Pero en una fila SQL el ritmo no lo elige el gateway: lo elige la aplicación de origen, y el CSV no tiene forma de conocerlo. Derivar de ahí sería tratar un dato declarativo como si fuera medido. Qué hacen `ScanRateMs` y `Deadband` en una fila SQL queda como pregunta aparte.

**Por qué un campo en `TagDefinition` y no una función de política.** La alternativa era que `TagCache` recibiera una función que, dada una definición, devolviera su umbral. Es más flexible, pero mete una abstracción para un caso con dos valores posibles y obliga a leer otro archivo para entender qué hace la cache. El campo deja el umbral visible al lado del resto de lo que define al tag, con el mismo patrón de default que ya usaron los campos agregados en la Fase 3 de la v1.

**Costo medido.** Dos llamadas al constructor de `TagCache` en todo el repositorio: `Program.cs:260` y `TagCacheTests.cs:136`. Al conservar la firma, ninguna de las dos se toca. El cambio se concentra en `TagDefinition` (un campo), `TagCache` (guardar el valor por tag) y `Degrade` (la línea de comparación).

**Lo que este cambio significa para el portfolio.** Es la primera vez que la v2 modifica `Gateway.Core`. El reporte de cierre de la v1 afirma que la abstracción que sobrevivió fue el tipo de dato compartido y no una interfaz común. Esa afirmación aguanta para `TagSample` y `TagState`, que no cambian. No aguanta para la política de degradación, que estaba cableada a un solo ritmo de adquisición porque solo había uno. Es un resultado del proyecto y va al reporte de cierre, no una corrección de un error de la v1: con una sola fuente, un umbral único era la decisión correcta.

**Qué se publica entre un polling y el siguiente.** Queda resuelto por construcción y se anota acá porque es la contracara de esta decisión. `TagCache.Get` devuelve el `TagState` guardado y la publicación UA lo lee cada 1000 ms sin recalcular nada. Un tag SQL se republica con su valor y su `SourceTimestamp` original, que no se mueve (principio 2). El cliente ve un dato de hace veinte segundos y puede juzgarlo por su timestamp, sin que el gateway le baje la calidad.

**Pendiente.** Si un tag SQL nunca recibe su primera muestra, queda en `WaitingForInitialData` para siempre, porque sin umbral no hay transición a `NotConnected`. Hay que definir si ese caso lo cubre el tag ausente de P9 (`Bad` más aviso en el log, decidido en el driver) o si necesita tratamiento en la cache.

---

### V2-12 — La cache se indexa por el par (origen, tag de origen)

**Decisión.** La clave del índice de definiciones deja de ser el nombre de origen a secas y pasa a ser el par (origen, `SOURCE_TAG`). `Update` recibe, además del lote de muestras, de qué fuente viene, y cruza solo contra las definiciones de esa fuente. Los miembros que exponen el nombre de origen se renombran: `DaNames` pasa a pedir la fuente y devolver los nombres de esa fuente; `GetDaName` pasa a devolver el nombre de origen sin importar cuál sea. Se mantiene un único índice con clave compuesta, no dos diccionarios ni dos caches.

**El problema.** Hoy `_definitionsByDaName` agrupa por `OpcDaName` y `Update` cruza las muestras contra esa clave. Con dos fuentes eso rompe de dos maneras distintas. La primera es una colisión: los dos orígenes vienen del mismo mundo y es razonable que un tag DA y una fila de `CURRENT_VALUES` se llamen igual, y en ese caso las muestras de una fuente actualizarían los nodos UA de la otra sin que nada lo reporte. La segunda apareció al medir el costo del cambio y es peor, porque no depende de que haya nombres repetidos: `DaAcquisitionService` le pide a la cache `DaNames` para dar de alta los items contra el servidor DA. Si esa lista incluye los nombres SQL, el servidor DA los rechaza y quedan reintentándose cada `ItemRetryIntervalMs` para siempre, ensuciando el log y castigando al servidor legado con altas que nunca van a funcionar.

**Por qué clave compuesta y no dos diccionarios ni dos caches.** Dos diccionarios adentro de la cache duplican estructura y obligan a tocar cada lugar que recorre el índice, sin ganar nada sobre la clave compuesta. Dos instancias de `TagCache`, una por fuente, es la alternativa más invasiva: rompe que la cache sea la frontera única entre adquisición y publicación, y obliga a `Program.cs`, al node manager y a la página de diagnóstico a preguntarle a dos objetos en vez de a uno. La clave compuesta deja la estructura como está, concentra el cambio en un archivo y no cierra la puerta a una tercera fuente, aunque eso esté fuera de alcance.

**Lo que no cambia y conviene dejar anotado.** La relación uno a muchos se conserva: un mismo tag de origen puede seguir alimentando varios nodos UA con transformaciones distintas. Y el `continue` de `Update`, que descarta en silencio una muestra cuya clave no está en el índice, resulta ser exactamente el filtrado que pide R3: el driver SQL puede entregar la tabla entera y la cache se queda con los tags declarados en el CSV. No hay que escribir filtrado nuevo en el driver; la consulta sin `WHERE` y el filtrado del lado del gateway ya están cubiertos por código de la v1.

**Relación con el invariante 8.** El aislamiento entre fuentes no termina en el hilo dedicado de V2-10. Sin esta decisión, dos fuentes comparten el mismo espacio de claves y una puede escribir sobre los tags de la otra, que es una forma de interferencia que ningún hilo separado evita. El invariante se sostiene en dos capas: el hilo, para que una fuente lenta no frene a la otra; el índice por fuente, para que una fuente no escriba sobre los datos de la otra.

**Costo medido.** Once apariciones de los miembros afectados: nueve dentro de `TagCache.cs`, una en `TagDiagnosticsQuery.cs:71` y una en `DaAcquisitionService.cs:214`. La de diagnóstico es un renombre directo, y de paso la columna pasa a tener sentido para las dos fuentes. La de adquisición es un cambio de comportamiento, no de nombre: pasa a pedir solo los nombres de origen DA.

**Segundo cambio en `Gateway.Core`.** Junto con V2-11, confirma qué aguantó y qué no del diseño de la v1 al sumar una fuente. `TagSample` y `TagState` no cambian: el tipo de dato compartido efectivamente alcanzó. Lo que no aguantó fue lo que asumía una sola fuente sin decirlo — un umbral de antigüedad único y un espacio de nombres de origen único. Las dos son suposiciones correctas mientras hay un solo driver, y las dos se rompen al agregar el segundo. Va al reporte de cierre.

**Pendiente, cerrado.** La clave es `record struct TagKey(TagSource Source, string SourceTag)`. `record struct` y no tupla porque los miembros se leen por nombre en todos los usos, y struct porque se construye una vez por muestra por ciclo —miles por ciclo con la tabla entera— y así no genera basura. Los miembros quedaron como `SourceTags(TagSource)` y `GetSourceKey(string)`.

---

### V2-13 — El estado del vínculo se reporta por fuente

**Decisión.** `DaLinkStatus` se renombra a `SourceLinkStatus` y suma la identificación de la fuente. El snapshot deja de recibir un único status de vínculo y pasa a recibir una colección, una entrada por fuente activa. `GatewayStatus` sigue existiendo como resumen, pero deja de hablar del vínculo: conserva arranque, uptime y contadores agregados, y pierde `LinkState`, `LastSuccessfulCycleUtc`, `SecondsSinceLastCycle`, `ReconnectAttempts` y `LastError`, que pasan a leerse por fuente. El node manager publica el estado del vínculo bajo un nodo por fuente en vez del único `Status.LinkState` actual.

**Por qué el enum no cambia.** `LinkState` —`Connected`, `Disconnected`, `Reconnecting`, `Stalled`— es genérico de cualquier vínculo con polling y le sirve a SQL tal cual. Lo específico de DA no es el tipo sino dos cosas alrededor: el cálculo de `Stalled`, que vive en `DaAcquisitionService` comparando contra el inicio del ciclo, y `Diagnose`, que traduce el estado a un diagnóstico con nombre propio (`DaServerStalled`, `DaLinkDown`). El enum se reusa; esos dos se replican por fuente.

**Por qué no un estado global que sea el peor de los dos.** Era la opción más barata y es la que rompe el invariante 8 en la capa de reporte. Con un único `LinkState`, el escenario que la Fase 5 tiene que demostrar —base caída con DA sano— se vería en UaExpert y en la página como un gateway degradado entero. El gateway estaría haciendo lo correcto y reportando lo contrario, que es peor que no reportar nada: un operador que ve el gateway caído no confía en los tags DA que sí están sanos. El invariante no se sostiene solo en el comportamiento, tiene que verse.

**Por qué una colección y no un `SqlLinkStatus` paralelo.** Casi todos los campos de `DaLinkStatus` son genéricos de cualquier polling: ciclos, fallos, reconexiones, conexiones, tiempos de ciclo, intervalo configurado, instante del último ciclo exitoso. Lo único atado a DA es el nombre del record. Un segundo record paralelo duplicaría una docena de campos idénticos, obligaría a la página de diagnóstico y al node manager a llevar dos caminos casi iguales, y haría que cada métrica nueva del diagnóstico haya que agregarla dos veces. Una sola forma de reportar una fuente cuesta un renombre y no vuelve a costar nada.

**`Stalled` se replica para SQL, con otro significado del que esta decisión le dio al escribirse.** El concepto aplica —la consulta vuelve, pero tarde—, y el umbral no puede ser el de DA porque los ritmos son distintos por diseño (R4). Lo que cambió al implementarlo es dónde cae la zona útil. Acá se había escrito que `Stalled` cubría la consulta que contesta *dentro* del timeout pero fuera del ritmo esperado, o sea una señal temprana. Quedó al revés: el umbral es el doble de `CommandTimeoutSeconds`, con piso de 20 s.

**Por qué se dio vuelta.** Una consulta que se pasa del timeout ya cae sola en `Reconnecting`, así que un umbral por debajo avisaría de algo que se resuelve solo dos segundos después. Y el estado del vínculo alimenta los nodos UA de diagnóstico y la página: ahí, cualquier cosa distinta de `Connected` es lo que manda a un operador a revisar. Una consulta de 8 s con timeout de 10 está funcionando y entregando datos; pintarla `Stalled` es la versión chica del error que esta misma decisión rechaza al descartar el estado global —reportar problema donde no lo hay entrena a desconfiar del indicador—. Lo que el umbral sí detecta es lo que el timeout no cubre: el mapeo de 10.000 filas o la transferencia del reader, el único tramo donde puede pasar lo mismo que en DA, colgado sin morir.

**Qué se pierde y por dónde se recupera.** Se pierde el aviso automático de que la base viene lenta y en algún momento va a pasar el timeout. Esa señal existe igual, en los tiempos de ciclo que el status ya reporta (último, promedio y máximo), que muestran la degradación sin mentir sobre el estado del vínculo. El costo real es que hay que mirarlos, y nadie los mira hasta que algo falla.

**Por qué no se deriva del intervalo de polling.** La ventana medida es la del ciclo: el instante de inicio se registra al empezar y se limpia al terminar, así que la espera entre ciclos no participa. Con 30 s de polling, cinco intervalos darían 150 s para algo que el timeout corta a los 10.

**Medido.** Con la base caída, la detección tarda el `CommandTimeout` (~10 s) y no el intervalo de polling. Los números completos están en `driver-sql.md`.

**Queda afuera un `Connect()` colgado.** Ahí el vínculo todavía no está marcado como conectado y el estado sale `Disconnected` o `Reconnecting`: no es falso, pero no distingue "colgado conectando" de "esperando para reintentar".

**Qué muestra la página de diagnóstico.** Una sección por fuente, con su estado de vínculo, su último ciclo exitoso, sus reintentos y su último error; y arriba, el resumen agregado que ya existe, sin estado de vínculo. Es lo que hace legible el escenario de la Fase 5 sin abrir UaExpert: una fuente en rojo y la otra en verde, al mismo tiempo, en la misma pantalla.

**Costo medido.** Ocho apariciones de `DaLinkStatus` y `GatewayStatus` en cuatro archivos. Solo dos construyen el record completo: `DaAcquisitionService.cs:78` y el helper `Link(...)` de `GatewaySnapshotTests.cs:21`, que concentra el renombre para todos los casos de prueba en un solo lugar. El cambio de forma real, de uno a colección, está en `GatewaySnapshot.cs:144`.

**Tercer cambio en `Gateway.Core`, y el de más superficie.** A diferencia de V2-11 y V2-12, este toca lo que el gateway le muestra al mundo: nodos UA de diagnóstico y la página web. Va al reporte de cierre junto con los otros dos, y es el que mejor ilustra el patrón común: lo que no aguantó el agregado de una segunda fuente no fueron los tipos de dato compartidos, sino las estructuras que decían "el" vínculo, "el" umbral, "el" nombre de origen, en singular, porque cuando se escribieron había uno solo.

**Pendiente.** Una fuente se identifica con `TagSource`, el mismo enum que usa la columna `SOURCE` del CSV, serializado como texto. Queda abierto si el node manager arma la rama de diagnóstico por fuente de forma dinámica o con las dos fuentes conocidas. Se cierra en la Fase 4, al integrar.

---

### V2-14 — `V` se publica como `Float`, tipo nuevo del enum

**Decisión.** `TagDataType` suma `Float`. Un tag SQL analógico lo declara en la columna `DATA_TYPE` del CSV y se publica como `Float` en UA. `TryScale` suma un caso que reusa `TryToDouble`, aplica `Multiplier` y `Offset` en `double` y castea a `float` al final. El valor queda disponible también para filas DA: nada obliga a usarlo, y los tags DA existentes siguen con el tipo que ya declaran.

**El hecho.** `V` es `real` en SQL Server: float de 4 bytes, unos 7 dígitos significativos (P1). `Double` en UA tiene unos 15. Convertir de uno a otro es exacto y no pierde nada del dato. Lo que se pierde es la información de cuántos de esos dígitos significan algo: un valor que en la tabla es 8009.57 publicado como `Double` se ve como 8009.570068359375. Ninguno de los dígitos de más existe en la medición; son la representación exacta de un `float`, mostrada con la precisión de un `double`.

**Por qué no publicar como `Double` y listo.** Era la opción de cero costo: el caso ya existe en el `switch` y `Double` no pierde información. Se descarta porque el gateway no le miente al cliente en ningún otro lado —no inventa `SourceTimestamp` (principio 2), publica `Uncertain` en vez de `Bad` ante una duda (principio 3), publica los nodos de diagnóstico en `Good` y pone la falla en el valor (principio 4)— y esto sería la única excepción. Un cliente que lee `Float` sabe cuántos dígitos tiene sentido mostrar; uno que lee `Double` no tiene de dónde deducirlo.

**Por qué no redondear a 7 dígitos y publicar `Double`.** Es la opción que parece prolija y es la que más distorsiona. Fabrica un número que no es ni el que está en la base ni el que corresponde al tipo, y el redondeo caería después de `Multiplier` y `Offset`, con lo cual el criterio de "7 dígitos significativos" ya no se refiere al dato de origen sino al resultado de una cuenta.

**Orden de las operaciones.** El escalado se hace en `double` y el cast a `float` va al final. Calcular en la precisión alta y bajar recién al publicar evita acumular error en la propia cuenta, que es un problema distinto del de la precisión del dato de origen.

**Costo.** Un valor más en el enum y un caso en `TryScale` que se apoya en el mismo `TryToDouble` y el mismo escalado que `Double` e `Int32`. El `switch` es código compartido con DA, así que el caso nuevo queda a la vista de las dos fuentes; es el costo asumido de tener la conversión en un solo lugar.

---

### V2-15 — Un `Boolean` puede llegar como número

**Decisión.** El caso `Boolean` de `TryScale` deja de exigir que el valor sea un `bool` de .NET y acepta también numérico: **0 es `false`, cualquier otro valor es `true`**. `Multiplier` y `Offset` no se aplican a un `Boolean`, y eso queda explícito en el código.

**El problema.** Hoy el caso `Boolean` devuelve `false` si el valor no es literalmente un `bool`. Desde DA funciona porque el servidor entrega un `VT_BOOL` que el SDK materializa como booleano. Desde SQL, `V` es `real` y un booleano llegaría como 0 o 1 en esa misma columna (P2), así que un tag SQL declarado `Boolean` nunca se actualizaría — y fallaría en silencio, porque `TryScale` devolviendo `false` no distingue un tipo mal declarado de un valor imposible.

**Por qué se arregla en el `switch` y no en el driver.** La alternativa era que el driver SQL convirtiera a `bool` antes de entregar la muestra. Se descarta porque el tipo lo declara la definición del tag en el CSV, no la fuente: el driver tendría que leer definiciones para saber qué convertir, y eso le suma al borde una responsabilidad que hoy no tiene. El driver entrega lo que la tabla contiene; interpretar qué significa es de la cache.

**Por qué no prohibir `Boolean` en filas SQL.** Era la opción más barata y hoy no rompería nada, porque mi padre confirmó que son todos analógicos (P2). Se descarta porque la restricción sería arbitraria: el día que aparezca un booleano en la tabla, el arreglo es exactamente este, y mientras tanto el validador del CSV estaría rechazando algo que el gateway puede manejar. La suposición de que un booleano llega materializado es heredada de tener una sola fuente cuyo SDK ya lo entregaba así; un gateway que traduce entre mundos tiene que aceptar la representación que cada mundo usa, y en el mundo del proceso un booleano es 0 y distinto de 0 desde siempre.

**Por qué "distinto de 0" y no "1 exactamente".** Es la convención universal y evita descartar un valor que llegue como 0,9999 por una conversión o un escalado intermedio. Tratar todo lo que no sea 0 ni 1 como inválido agregaría un modo de falla sin agregar información.

**Pendiente heredado, que esta decisión no crea ni resuelve.** `TryScale` devolviendo `false` no distingue "tipo mal declarado" de "valor imposible", y el tag simplemente no se actualiza. Es deuda de la v1; con dos fuentes se vuelve más fácil de tropezar, pero se deja como está para no ampliar el alcance.

---

### V2-16 — `V` o `Q` en `NULL` se publican como `Uncertain`

**Decisión.** Las dos columnas admiten nulo (P1) y las dos se publican como `Uncertain`, nunca como `Bad`, pero se tratan distinto en lo demás:

- **`V` en `NULL`:** no hay medición. Se conserva el último valor bueno y su `SourceTimestamp` —que no avanza aunque `TS` haya cambiado, porque no hay dato nuevo que fechar— y solo se degrada la calidad.
- **`Q` en `NULL`:** hay medición pero no hay código de calidad que mapear. El valor y el `SourceTimestamp` se actualizan normalmente; lo único que sale como `Uncertain` es la calidad.

**Por qué `Uncertain` y no `Bad`.** Es el principio 3, y acá aplica de manera casi literal: `Bad` no transporta valor y le borra al cliente el último dato bueno, mientras que un `NULL` es exactamente una duda y no una certeza de que el dato esté mal. Con `Uncertain`, el operador sigue viendo el último valor conocido y sabe que no lo tome como fresco, que es más información que una pantalla en blanco.

**Por qué el `SourceTimestamp` no avanza con `V` en `NULL`.** Avanzarlo afirmaría que hay una medición de ese instante, y no la hay. Es el mismo criterio con el que la v1 arranca los tags con `SourceTimestamp` en default en vez de en la hora actual: un tag sin dato no tiene momento de origen.

> **Actualización 05/10/2026.** Eso vale en el nodo, no en el cliente: el stack reemplaza el `SourceTimestamp` en default por la hora actual al responder (V2-36).

**Pendiente, que se suma al de V2-11.** Si `V` viene `NULL` de forma permanente, el tag queda indefinidamente mostrando un valor viejo en `Uncertain`, y como los tags SQL no degradan por antigüedad (V2-11), nada lo empeora nunca. Es el mismo hueco que el tag SQL que jamás recibe su primera muestra. Los dos casos se resuelven juntos, junto con la definición de qué cubre exactamente el tag ausente de P9.

**Hallazgo al implementar.** La decisión no tenía camino en el código. Una muestra con calidad utilizable y valor nulo pasaba el chequeo de `IsUsable`, caía en `TryScale` —que devuelve `false` ante un nulo— y terminaba publicada como `ConversionError`, que es `Bad`: justo lo contrario de conservar el último valor bueno. Se agregó una rama en `TagCache.Apply`, entre el chequeo de calidad y el de escalado.

---

### V2-17 — El cruce de nombres respeta la semántica de cada fuente

**Decisión.** El índice de la cache compara los nombres de origen según la fuente: **insensible a mayúsculas para SQL** (`OrdinalIgnoreCase`) y **sensible para DA**, como hasta hoy. La parte de origen de la clave compuesta de V2-12 se compara siempre de forma exacta. Se implementa con un comparador propio para la clave compuesta, de unas pocas líneas, no con estructuras separadas.

**El choque.** SQL Server, con su collation habitual, no distingue mayúsculas: para la base, `TIC101.PV` y `tic101.pv` son el mismo nombre, y como `TAG` es clave primaria (P1), la tabla no puede contener las dos filas. Un `Dictionary<string, ...>` de .NET con el comparador por defecto sí las distingue. Si el CSV declara `TIC101.PV` y la tabla tiene `Tic101.Pv`, el `TryGetValue` de `Update` falla y el tag queda tratado como ausente — hoy en silencio, por el `continue` que descarta muestras no declaradas.

**Por qué no se extiende a DA.** OPC DA sí distingue mayúsculas en los `ItemID`, y hay servidores con items que difieren solo en eso. Un comparador insensible del lado DA podría colapsar dos items legítimamente distintos en uno, que es un error peor y más difícil de ver que el que se quería arreglar. No hay una respuesta única correcta acá: cada fuente tiene la semántica de su propio mundo y el gateway respeta la de cada una. Que esto se pueda hacer sin contorsiones es consecuencia directa de V2-12, porque la clave ya lleva el origen adentro.

**Por qué no dejarlo sensible y documentarlo.** Era la opción de costo cero. Se descarta porque convierte un problema de mayúsculas en un tag que no funciona sin explicar por qué: el operador ve un tag en `Bad` y no tiene forma de deducir que la causa es la capitalización de una letra en un CSV.

**Detalle de implementación.** Un `Dictionary` tiene un único comparador para toda la clave, así que el comparador de la clave compuesta es el que decide: compara el origen de forma exacta y el nombre según la regla de ese origen. Es `TagKeyComparer`, un `IEqualityComparer<TagKey>` singleton, con el `GetHashCode` derivado del mismo comparador de strings que usa `Equals` para que dos claves SQL que difieren en mayúsculas caigan en el mismo bucket.

---

### V2-18 — `TS` se convierte de hora local a UTC

**Decisión.** El driver SQL convierte `TS` a UTC antes de armar la `TagSample`. La zona horaria es un parámetro de configuración (`Sql:TimeZone`), con la zona de la máquina local como default. El `SourceTimestamp` publicado es el resultado de esa conversión; el gateway sigue sin inventar nada (principio 2), solo cambia de referencia un instante que ya venía en el dato.

**Por qué convertir.** `TS` es hora local puesta por la aplicación de origen (P4) y el `SourceTimestamp` de OPC UA es UTC por definición. Publicarlo sin convertir correría los tags SQL respecto de los DA —en Argentina, tres horas— y un cliente que compare timestamps de las dos fuentes vería el dato SQL en el futuro o en el pasado sin explicación. Mi padre condicionó la conversión a que no implique carga de CPU, y no la implica: es una resta de offset sobre unas miles de filas cada 20 a 60 s, con la zona resuelta una vez al arrancar.

**Por qué parámetro y no la zona de la máquina a secas.** Con base simulada local las dos coinciden, pero el servidor real de TEST (P10) puede estar en otra máquina y el gateway correr en otra. Dejarlo como parámetro hace que ese caso se resuelva por configuración, que es el único punto de adecuación previsto. El default evita que haya que configurarlo para el caso normal.

**La clave es `Sql:TimeZone` y vacío es válido.** Vacío significa la zona de la máquina donde corre el gateway, que es el caso normal. No se versiona un ID concreto porque se resuelve contra la tabla de zonas de la máquina que corre, así que fijar uno en el JSON lo rompería en cualquier otra. Se valida al arrancar: un ID mal escrito explotaría recién en el primer ciclo de polling, lejos del arranque y con un mensaje que no nombra el JSON.

**Detalle que importa.** Un `datetime` de SQL Server llega con `DateTimeKind.Unspecified`. Usar `ToUniversalTime()` sobre eso asume la zona de la máquina en silencio, que es justamente lo que el parámetro existe para no hacer. La conversión tiene que ser explícita contra la zona configurada.

**Horario de verano.** Argentina no lo aplica desde 2009, pero el parámetro admite otras zonas y el caso hay que decidirlo igual. Una hora ambigua (la que se repite al atrasar) se resuelve con el comportamiento por defecto de .NET, que elige el horario estándar. Una hora inexistente (la que se saltea al adelantar) no puede convertirse: se publica la muestra como `Uncertain` y se loguea una vez, porque un timestamp imposible es exactamente una duda, no una certeza de error.

---

### V2-19 — Decodificar un código de calidad DA crudo, en `Gateway.Core`

**Decisión.** La descomposición de un código de calidad OPC DA en `TagQuality`
(master, substatus, limit) es `TagQuality.FromDaCode(int, out bool)`, pública, en
`src/Gateway.Core/TagQuality.cs`. El driver SQL la usa sobre `Q`. Antes de
decodificar se enmascaran los 8 bits de fabricante tomando el byte bajo.

**El hallazgo, corregido al implementar.** La decisión original decía que la
descomposición estaba en `OpcDaTagSource.Translate` y que había que moverla a
`Gateway.Core`. Es falso: `Translate` no descompone nada. Recibe `OpcDaQuality`
del SDK, que **ya viene desarmado** en `Master`, `Status` y `Limit`, y lo único
que hace es traducir enum del SDK a enum de `Gateway.Core`. La descomposición por
bits la hace el SDK. Así que en V2-19 no había lógica para mover: hubo que
**escribir** la decodificación desde un entero, que no existía en el repo.

**El fundamento se sostiene igual, y mejor.** Los enums de `TagQuality.cs` ya
tienen los valores numéricos de la especificación (`Good = 192`,
`BadLastKnown = 20`, `GoodLocalOverride = 216`), así que decodificar bits es
convertir a esos mismos enums. La tabla de significados no se duplica: hay una
sola, la del enum, y las dos fuentes llegan a ella por caminos distintos.

**Pendiente cerrado: `Gateway.Da` no delega.** La decisión original dejaba
abierto que, si `OpcDaQuality` exponía el código crudo, `Gateway.Da` llamara a la
misma función para dejar una sola implementación. No conviene aunque lo exponga:
sería tirar el desarmado que el SDK ya hizo para rehacerlo a mano. `Gateway.Da`
queda como está, y no hay duplicación real que eliminar.

**El `smallint` negativo, redimensionado.** `calidad-observada.md` mostró que en
la tabla real no hay valores negativos, así que el enmascarado ya no se justifica
por un problema observado: se hace porque la spec (Parte 8 A.3.2.3) indica
descartar siempre los bits de fabricante, y sale gratis. Queda como red por si
alguna vez cambia el driver de origen — el bit 15 volvería negativo al `smallint`
y cualquier comparación contra 192 fallaría en silencio. Hay un test que lo cubre.

**Códigos no previstos.** Si el substatus no corresponde a ningún valor conocido
se conserva el master —que es la información que decide si el dato sirve— y el
substatus cae al valor base de ese master. Descartar la muestra entera por un
substatus raro sería perder un dato que el master ya califica bien.

**El aviso no vive en `Core`.** La decisión original pedía loguear una vez por
código desconocido. `Gateway.Core` no tiene logger, y "una vez por código"
exigiría estado estático en un tipo que hoy es una función pura. En su lugar la
función devuelve `out bool unknownSubstatus` y **decide quien llama**, que sí
tiene logger. Es la misma forma que usa el resto de la API de .NET para esto.

**`QualityMaster.Error` se conserva.** El master `Error` (128) está reservado por
la spec y no tiene ningún `QualitySubstatus` correspondiente, así que la regla
anterior no tiene a dónde caer. Se publica con `Master = Error` y
`Substatus = Bad`, en vez de aplanarlo a `Bad`. El motivo es que ese camino ya
existe: `Translate` puede producir `QualityMaster.Error` desde el lado DA hoy.
Aplanarlo solo del lado SQL haría que la misma anomalía se viera distinta según
la fuente.

**Lo que esto confirma.** `QualitySubstatus` ya contenía `BadLastKnown = 20`, el
valor que mi padre mencionó para la pérdida de campo (P6), y los otros cuatro
códigos que `calidad-observada.md` encontró en la tabla. El mapeo de la v1 sirve
tal cual, sin agregar valores.

**Evidencia.** `tests/Gateway.Tests/TagQualityTests.cs`, 14 tests: los cinco
códigos reales de la tabla, todo substatus del enum decodificable desde su propio
código, los dos bits de limit, el código negativo con bits de fabricante, tres
substatus no previstos y el caso `Error`.

---

### V2-20 — Cómo se cumple R5 sobre ADO.NET

**Decisión.** El driver mantiene una única instancia de conexión abierta durante toda su vida, que es la lectura literal de R5. Ante cualquier falla de la consulta: se descarta la conexión, se espera `ReconnectDelaySeconds` y se crea una nueva. El pooling se deshabilita explícitamente en la cadena de conexión.

**El matiz que había que resolver.** En ADO.NET lo idiomático es abrir y cerrar por consulta y dejar que el pool mantenga viva la conexión física. Observable desde afuera, las dos formas se parecen mucho. Se elige la conexión única porque acá hay exactamente un consumidor —el hilo dedicado de V2-10—, y el beneficio del pool es repartir conexiones físicas entre consumidores concurrentes que no existen. Sin ese beneficio, la conexión única cumple el requisito al pie de la letra y deja el modelo sin ambigüedad.

**Por qué deshabilitar el pooling.** Si el pooling queda activo, una conexión que falló puede volver al pool y entregarse otra vez, y `LinkState` pasaría a reportar sobre un objeto que no controla el estado real. Con pooling desactivado, el ciclo de vida de la conexión física coincide con lo que el driver cree que está pasando, y eso es lo que hace honesto el diagnóstico de V2-13. El costo es un handshake completo en cada reconexión, que ocurre solo cuando ya hubo una falla.

**Detección.** Una conexión no se entera de que se cayó la red hasta que falla una operación. Eso no es una limitación de esta decisión sino de cualquiera: "reconectar cuando falla una consulta" es el mecanismo real de detección, y con polling de 20 a 60 s el peor caso de detección es un ciclo. Se contrasta con los números medidos en la Fase 5.

**No hay choque con R5.** El requisito pide conexión única, persistente y con reconexión automática espaciada, y eso es exactamente lo que se implementa.

---

### V2-21 — Tag SQL sin dato: los tres casos

> **Enmendada por V2-30** en un solo punto: el aviso de log del tag ausente ya no
> sale "una vez por sesión" sino una vez por cambio del conjunto. La calidad con
> la que se publica cada caso no cambió.

**Decisión.** Se cierran juntos los pendientes acumulados de V2-11 y V2-16, porque son tres situaciones distintas que se venían confundiendo:

- **Tag declarado en el CSV que no aparece en el resultado de la consulta.** Es un error de configuración: el tag existe de este lado y no del otro. Se publica `Bad` con substatus de error de configuración, que es la misma forma que ya usa `ItemRejected` para un `ItemID` que el servidor DA rechaza, y se loguea **una vez por tag y por sesión**, no en cada ciclo: a 20 segundos de polling, avisar siempre inunda el log y esconde lo que importa. Esto es lo que P9 pide.
- **Tag presente con `V` en `NULL` de forma permanente.** Queda en `Uncertain` indefinidamente, y está bien que quede así. La fila existe, el origen la está escribiendo, y lo único que falta es el valor. `Uncertain` con el último valor bueno es la descripción exacta de esa situación, y como los tags SQL no degradan por antigüedad (V2-11), nada la empeora con el tiempo. No hace falta un mecanismo nuevo.
- **Tag que nunca recibió su primera muestra.** Deja de ser un caso propio. Si el tag no está en la tabla, cae en el primero. Si está, la primera consulta ya lo trae y sale de `WaitingForInitialData` en el primer ciclo. El estado eterno que preocupaba en V2-11 no puede ocurrir.

**Por qué `Bad` acá y `Uncertain` allá, sin contradecir el principio 3.** Un tag que no existe en la tabla no tiene ningún valor bueno anterior que `Bad` pueda borrar, así que el argumento del principio no aplica; y la causa —configuración— manda a revisar el CSV en lugar de la red, que es información útil. Un tag que existe y momentáneamente no trae valor sí tiene historia que preservar, y ahí `Uncertain` es lo correcto.

**Pendiente.** El primer caso lo implementa el host, no el driver: hay que cruzar las filas recibidas contra `SourceTags(TagSource.Sql)`. El mapeo no conoce las definiciones, por diseño, y la cache solo toca lo que llega, así que hoy un tag ausente queda en `WaitingForInitialData` —`Bad`, pero con otra causa y sin aviso—. Va con el loop de polling.

---

### V2-22 — `SCAN_RATE_MS` y `DEADBAND` en filas SQL

**Decisión.** En una fila con `SOURCE=SQL` los dos quedan en su default y el gateway los ignora como parámetros de adquisición. El validador del CSV avisa si vienen distintos de cero, sin rechazar la fila.

**Por qué.** El ritmo de una fila SQL no lo elige el gateway: lo elige la aplicación de origen, con intervalos distintos por grupo de scan (P7), y el gateway lo único que controla es cada cuánto consulta la tabla entera (R4). Declarar un `SCAN_RATE_MS` por tag afirmaría un control que no existe. Ya quedó descartado en V2-11 derivar de ahí el umbral de antigüedad, por la misma razón.

**Aviso y no error.** Un valor heredado de copiar una fila DA es un descuido, no una configuración inválida, y rechazar la fila dejaría un tag fuera de servicio por algo que no afecta el comportamiento. El aviso alcanza para que se corrija.

**Pendiente cerrado (Fase 3, paso 5).** `DEADBAND` no tiene efecto en ningún camino de la v1: ni en publicación ni en adquisición. En todo el repo la propiedad `Deadband` aparece dos veces y las dos son declarativas — `CsvTagLoader` la parsea de la columna 8 y `TagDefinition` la declara con default `0`. Ningún componente la lee. Ya estaba dicho en `configuracion-tags.md`, que la clasifica como "Solo viaja" y avisa de no asumir que un tag con `DEADBAND=0.5` esté filtrando algo; lo que faltaba era verificarlo contra el código en vez de confiar en el doc.

**Qué implica para SQL.** Que la alternativa "actúa sobre la publicación, así que aplica igual a las dos fuentes" no existe, y la decisión de arriba queda como está: el driver SQL no hereda ni replica nada, porque no hay nada que heredar. La columna es una intención legible en las dos fuentes por igual, y el aviso del validador sigue teniendo sentido: marca un valor copiado por descuido, no un filtro que se perdió.

---

### V2-23 — Una fuente SQL caída no se detecta por antigüedad

**Decisión.** Cuando la aplicación de origen pierde el campo, el gateway se entera
por `Q` y solo por `Q`. El `TS` de una fila degradada sigue avanzando, así que la
antigüedad del dato no dice nada sobre su validez. El simulador reproduce ese
comportamiento a propósito: al cortar un grupo, congela `V`, pone `Q` en 20 y deja
que `TS` siga refrescándose.

**Por qué es así.** La aplicación que escribe `CURRENT_VALUES` sigue viva y sigue
pisando la fila; lo que se cayó es el campo, un nivel más abajo. Congelar también
el `TS` sería simular que la aplicación murió, que es otra falla distinta y que se
detecta de otra manera. P6 es explícito: la pérdida de campo se marca en la calidad.

**La evidencia.** Verificado en la Fase 2 con el simulador corriendo. Con el grupo
RAPIDO cortado, sus tres tags quedaron en `Q = 20` con el `TS` **más nuevo de toda
la tabla** (16:43:00 contra 16:42:55 de los tags sanos). Un tag con la calidad rota
y el timestamp más fresco que los sanos: cualquier lógica que infiriera la falla
por antigüedad lo habría dado por bueno.

**Qué implica.** Refuerza V2-11. Haber vuelto el umbral de antigüedad configurable
por tag es lo que permite que los tags SQL no lo usen, y esta decisión dice por qué
ahí no aplicarlo no es una simplificación conveniente sino la única lectura correcta
del dato.
Un umbral de antigüedad aplicado a esta fuente no detectaría la falla real y, peor,
degradaría tags sanos que solo esperan su próximo ciclo de scan.

**Lo que no cubre.** Que la aplicación de origen muera y deje de escribir la tabla
entera es un escenario distinto, con `TS` congelado y `Q` en el último valor bueno.
No está decidido cómo se detecta ni si hace falta. Se evalúa en la Fase 5, donde
"base caída" ya es uno de los escenarios.

---

### V2-24 — Qué `DATA_TYPE` acepta una fila SQL

**Decisión.** En una fila con `SOURCE=SQL` se aceptan `Float`, `Boolean` e `Int32`. `String` es **error de carga**, igual que un `SOURCE` vacío: la carga falla y el gateway no arranca. En filas DA no cambia nada.

**Por qué `String` no puede existir en una fila SQL.** `V` es `real` (P1) y mi padre confirmó que no hay tags de texto (P2). De esa columna no sale una cadena nunca, así que un tag SQL declarado `String` no se actualizaría jamás.

**Por qué error y no aviso, a diferencia de V2-22.** La asimetría es deliberada y el criterio es si el valor sobrante afecta el comportamiento. Un `SCAN_RATE_MS` heredado de copiar una fila DA no afecta nada: el tag funciona igual y el aviso alcanza. Un `String` en una fila SQL deja el tag muerto, y muerto en silencio: `TryScale` devuelve `false`, el tag no se actualiza y desde UA se ve igual que una fuente caída. Es exactamente la deuda heredada que V2-15 deja anotada —`TryScale` no distingue "tipo mal declarado" de "valor imposible"— y el mismo razonamiento de V2-5 para `SOURCE` obligatorio: que falle la carga es más barato que diagnosticarlo en `UaExpert`.

**Por qué se acepta `Int32` y no solo `Float`.** El caso ya existe en `TryScale` y `real` transporta enteros chicos sin problema, así que un contador declarado `Int32` funciona. Restringir a `Float` sería arbitrario, como lo hubiera sido prohibir `Boolean` en V2-15. El límite real es la precisión del `real` de origen, no el enum: más allá de unos 7 dígitos significativos un entero pierde exactitud, y eso ya es una propiedad del dato de origen y no algo que el gateway pueda arreglar declarando otro tipo.

**Dónde se implementa.** En la validación del CSV, junto al resto de las reglas de carga, no en el driver: el driver entrega lo que la tabla contiene y quién interpreta el tipo declarado es la cache (V2-15). El código va en la tanda delegable de la Fase 4, con el validador.

**Costo.** Una regla más en la validación, que cruza dos columnas (`SOURCE` y `DATA_TYPE`) en vez de validar cada una por separado. Es el primer cruce entre columnas del CSV; hasta ahora cada una se validaba sola.

---

### V2-25 — Un `DATA_TYPE` que el gateway no sabe publicar no tira el gateway abajo

**El hallazgo.** `GatewayNodeManager.CreateVariable` traduce `TagDataType` a un tipo de dato de OPC UA con un `switch` que termina en `default => throw new InvalidOperationException(...)`. Ese `throw` no está atrapado en ningún lado: pasa por `AddTag`, por el recorrido de todos los tags al construir el address space, y por `application.StartAsync(server)` en `Program.cs`, sin un solo `try/catch` en el medio. Hoy es inalcanzable —el enum tiene exactamente cinco valores y los cinco están en el `switch`—, pero es inalcanzable por coincidencia, no por diseño: nada impide que el día de mañana alguien sume un valor al enum (como pasó con `Float` en V2-14) sin acordarse de tocar este `switch`, y ese día una sola fila del CSV tira abajo el arranque completo del gateway, DA incluido.

**Decisión.** Antes de que una fila llegue a `TagDefinition`, `TagValidator` la cruza contra `PublishableTypes`: la lista explícita de los `TagDataType` que el `switch` de `CreateVariable` sabe convertir hoy, mantenida a mano en `Gateway.Core`. Si el tipo declarado no está en esa lista, la fila queda fuera de servicio —error de carga con número de línea y motivo, la misma vía que cualquier otro dato inválido del CSV— y el resto del archivo se sigue cargando. En planta es peor un gateway que muere entero que uno que publica 20 de 21.

**Por qué en `Gateway.Core` y no arreglando el `switch` de `Gateway.Ua`.** No se toca el node manager ni los tipos que publica: son código ya verificado y fuera del alcance de esta tanda. La validación tiene que cortar la fila *antes* de que llegue ahí, así que el único lugar posible es más arriba en la cadena, donde ya vive el resto de las reglas de `DATA_TYPE` (V2-24).

**Por qué una lista aparte y no reusar el enum entero.** Reusar `Enum.GetValues<TagDataType>()` sería más corto y hoy daría el mismo resultado, pero es exactamente lo que no hay que hacer: esa lista sería siempre igual al enum por construcción, nunca podría detectar que el enum creció y el `switch` no. La lista tiene que poder desincronizarse del enum para que sirva de red.

**El costo real de esta red: sigue habiendo dos lugares para mantener sincronizados.** Si alguien agrega un valor al enum y lo suma acá pero no al `switch` de `CreateVariable`, el resultado ahora es una fila rechazada en la carga en vez de un crash — una degradación aceptable. Si lo suma al `switch` pero no acá, el resultado es una fila que sigue sin publicarse aunque el gateway ya sabría hacerlo — más conservador de lo necesario, pero no un error nuevo. Ninguna de las dos direcciones de desincronización empeora lo que hay hoy; las dos son preferibles a no tener la lista.

**No hay forma de probarlo de punta a punta con un CSV real.** `CsvTagLoader.ParseEnum` ya rechaza con `Enum.IsDefined` cualquier valor que no sea un miembro del enum, así que ningún archivo real puede llegar con un `DATA_TYPE` que `PublishableTypes` no reconozca: hoy coincide 1 a 1 con el enum entero. `TagValidator.IsPublishableType` es pública justamente para poder probar la red con un valor forzado fuera de rango (`(TagDataType)99`), sin esperar a que el enum crezca de verdad.

---

### V2-26 — El override local de credenciales pasa de user-secrets a `appsettings.Local.json`

**Decisión.** `Program.cs` deja de registrar `AddUserSecrets<Program>()` y en su lugar agrega `AddJsonFile("appsettings.Local.json", optional: true)`, en el mismo lugar de la cadena: después de `appsettings.json`, para pisar sus claves vacías, y antes de `AddEnvironmentVariables()`, que sigue ganando por último. El archivo es opcional, vive junto al ejecutable (se resuelve contra `AppContext.BaseDirectory`, igual que `appsettings.json`) y ya estaba en `.gitignore` de antes. Se agrega `appsettings.Local.example.json`, versionado y sin valores reales, como plantilla de qué copiar. `Gateway.Host.csproj` pierde el paquete `Microsoft.Extensions.Configuration.UserSecrets` y el `UserSecretsId`.

**Por qué se revierte V2-7 en este punto.** V2-7 ya dejaba anotado el riesgo, sin verificarlo: *"el helper `AddUserSecrets` de .NET carga solo en entorno `Development`. Si el gateway corre sin `DOTNET_ENVIRONMENT`, el default es `Production` y los secretos no se leen"*. Un ejecutable publicado (`dotnet publish`, la forma en que este gateway corre en planta y en TEST) no trae `DOTNET_ENVIRONMENT=Development` a menos que alguien lo fije a mano, así que en el caso real el mecanismo elegido en V2-7 nunca leía nada: `SqlOptionsValidator` fallaba igual, pero con un mensaje que apunta a un archivo de secretos que jamás se consulta. `appsettings.Local.json` no depende de ninguna variable de entorno para activarse: si el archivo está, se lee.

**Por qué no las dos cosas a la vez.** Sostener `AddUserSecrets` además de `AddJsonFile` no agrega nada: en cualquier escenario donde el JSON local funciona, sumar user-secrets es una fuente más para razonar sobre qué pisa a qué, y en el escenario donde el JSON local no alcanzaría (que no existe: el archivo se lee siempre que esté, sin condición de entorno) user-secrets tampoco lo resolvía. Dos mecanismos para el mismo problema es más difícil de explicar que uno, y el que se descarta es el que ya estaba probado inservible en el caso real.

**El argumento original de V2-7 en contra del archivo ignorado, revisado.** V2-7 decía: *"el archivo ignorado depende de que el `.gitignore` esté bien y de que nadie lo fuerce con `git add -f`"*. Sigue siendo cierto y no se resuelve acá: es un riesgo real pero menor al de un mecanismo que no se activa nunca fuera de `Development`. `appsettings.Local.json` ya estaba en `.gitignore` desde antes de esta decisión, lo que sugiere que el archivo ignorado era el plan original y user-secrets fue el desvío.

**Qué no cambia.** El resto de V2-6, V2-7 y V2-9 sigue de pie: los parámetros van sueltos y no como connection string, la password nunca se versiona, `SqlConnectionStringBuilder` arma la cadena, y `Database`/`Schema`/`Table`/columnas se validan como identificadores. Lo único que cambia es *por dónde* llegan `Sql:User` y `Sql:Password` en desarrollo local; en TEST seguía siendo variables de entorno (`Sql__User`, `Sql__Password`) y sigue siéndolo.

**Costo.** Un archivo nuevo versionado (`appsettings.Local.example.json`), una línea de `Program.cs`, un paquete y una propiedad menos en el `.csproj`, y los mensajes de `SqlOptionsValidator` que nombraban "user-secrets" reescritos para nombrar el archivo nuevo.

**El archivo local nunca viaja en el paquete publicado.** `appsettings.Local.json` se copia al directorio de salida de `dotnet build`/`dotnet run` (`CopyToOutputDirectory`), porque ahí es donde el gateway lo busca en desarrollo, pero lleva `CopyToPublishDirectory=Never`: si el desarrollador que corre `dotnet publish` tiene el archivo con credenciales reales en su máquina, esas credenciales no salen en el paquete. `appsettings.Local.example.json`, sin valores reales, sí viaja al paquete publicado. Verificado con un `appsettings.Local.json` de prueba y `dotnet publish`: el paquete resultante tiene `appsettings.json` y `appsettings.Local.example.json`, no `appsettings.Local.json`.

---

### V2-27 — La fuente SQL solo se exige y solo arranca si el CSV declara tags SQL

**El problema.** `Program.cs` creaba `SqlAcquisitionService` y arrancaba su hilo sin condición, sin importar si el CSV declaraba algún tag `SOURCE=SQL`, y `SqlOptionsValidator` —escrito y probado desde V2-6— nunca se llamaba desde el host: una configuración `Sql:*` vacía o inválida no se detectaba al arrancar, se descubría recién cuando `SqlTagSource.Connect()` fallaba en el primer ciclo, y de ahí en más el hilo reintentaba cada `Sql:ReconnectDelaySeconds` para siempre contra parámetros que nunca iban a funcionar.

**Decisión.** `SqlSourceActivation.Decide(hasSqlTags, options)`, función pura en `Gateway.Sql`, cruza si el CSV declaró algún tag `SOURCE=SQL` (ya cargado y validado por `TagValidator`) contra el resultado de `SqlOptionsValidator.Validate`. `Program.cs` la llama antes de construir nada:

- **Sin tags SQL en el CSV:** no se valida `Sql:*` ni se loguea error alguno. La fuente no se activa. Una planta que solo usa OPC DA no tiene que configurar una base que no usa.
- **Con tags SQL y configuración válida:** se crea `SqlAcquisitionService` y arranca su hilo, como hasta ahora. Las advertencias de `SqlOptionsValidator` (cifrado relajado, rangos fuera de R4/R5) se loguean igual que antes.
- **Con tags SQL y configuración inválida:** cada error se loguea (`Log.Error`, con el mensaje ya redactado por el validador) más una línea que dice explícitamente que la fuente queda inactiva. Ni `SqlAcquisitionService` ni su hilo se crean. Los tags declarados `SOURCE=SQL` quedan sin actualizar —el mismo `Bad` que ya reporta cualquier tag que nunca recibió su primera muestra (V2-11)— y el resto del gateway, DA incluido, sigue funcionando: es el invariante 8 aplicado también al arranque, no solo al ciclo de polling.

**Por qué no crear el hilo igual y dejar que reintente.** Es lo que pasaba antes de esta decisión y funciona, en el sentido de que no rompe nada: el hilo SQL aislado (V2-10) ya evita que una conexión que nunca prospera frene a DA. Pero una configuración vacía o con un identificador inválido no es una base momentáneamente caída —R5 es para eso, y ahí sí conviene reintentar—, es un error de tipeo que no se va a arreglar solo. Reintentar para siempre contra eso ensucia el log cada `ReconnectDelaySeconds` sin parar y le pide al operador que distinga a ojo, mirando la frecuencia de los mensajes, cuál de los dos casos está pasando. Fallar una vez al arrancar, con el motivo, es más barato de diagnosticar.

**Por qué la fuente inactiva no aparece en el snapshot ni en los nodos de diagnóstico UA.** `GatewaySnapshot.Sources` ya estaba documentado como "una entrada por fuente activa, en el orden en que las pasó el host" (V2-13), y tanto `GatewaySnapshot.Build` como `GatewayNodeManager` recorren esa lista con `foreach`, sin asumir que haya exactamente DA y SQL. No incluir a SQL en la lista cuando está inactiva es usar la dinámica que ya existía, no agregar una nueva: no hace falta tocar el node manager ni la página de diagnóstico para que el gateway arranque igual sin la fuente SQL.

**No cierra qué debería ver un operador para una fuente inactiva por configuración, a diferencia de una fuente que nunca se declaró.** Hoy las dos se ven igual: ninguna sección en el diagnóstico. Distinguirlas —"no hay tags SQL" contra "hay tags SQL pero la configuración está mal"— solo queda en el log de arranque. Se deja así porque tocar qué muestra la página de diagnóstico está fuera del alcance de esta tanda.

**Costo.** Un archivo nuevo (`SqlSourceActivation.cs`, con su record de resultado) y el `sqlAcquisition`/`sqlThread` de `Program.cs` pasan de no anulables a `SqlAcquisitionService?`/`Thread?`, con los `?.`/chequeos correspondientes en el snapshot y en el apagado.

---

### V2-29 — El driver DA solo arranca si el CSV declara tags DA

**El problema.** `Program.cs` creaba `DaAcquisitionService` y arrancaba su hilo sin condición, sin importar si el CSV declaraba algún tag `SOURCE=OPC_DA`. En una instalación con tags exclusivamente SQL —el caso que V2-27 ya soporta del lado SQL— el driver DA igual se conectaba contra el ProgID configurado (`Matrikon.OPC.Simulation.1` en desarrollo), reintentando para siempre en una máquina que puede ni tener un servidor DA instalado.

**Decisión.** Mismo criterio que V2-27, aplicado a DA: `hasDaTags = tagLoadResult.Tags.Any(t => t.Source == TagSource.OpcDa)` se calcula junto a `hasSqlTags`, antes de crear nada.

- **Sin tags DA en el CSV:** no se crea `DaAcquisitionService` ni su hilo. Se loguea una línea Information, simétrica a la de SQL: "No hay tags de origen OPC DA en el CSV; la fuente DA no se activa."
- **Con tags DA:** todo se comporta exactamente como antes — se crea el servicio, el hilo nace en MTA (exigencia de COM) y arranca de inmediato.
- **Con cero tags de cualquier fuente:** las dos ramas caen en "no se activa" y el gateway arranca sin ningún driver de adquisición corriendo, solo el servidor UA, vacío. Un CSV sin filas es válido para el validador de tags, así que el host tiene que tolerar ese caso.

**Por qué no hay, del lado DA, un equivalente a `SqlSourceActivation`/`SqlOptionsValidator`.** SQL necesita validar `Sql:*` (host, credenciales, identificadores) antes de intentar conectar porque una configuración con un identificador inválido no es un problema de red, es un error de tipeo que nunca se va a resolver solo reintentando (V2-27). DA no tiene ese escalón: el ProgID y el intervalo de `Da:*` no se validan hoy contra nada antes de conectar, así que la única pregunta que hace falta responder es si el CSV declaró tags DA, no si `Da:*` es válido.

**Por qué simetría con SQL y no una regla nueva.** Mismo argumento que V2-27: un driver reintentando para siempre contra algo que el CSV nunca pidió es ruido para quien opera, no una señal de un problema real. La única razón por la que DA nacía siempre era que, antes de V2-5, todos los tags eran DA por definición; con la fuente SQL ya integrada (V2-24) esa suposición ya no vale para ningún lado.

**Por qué no hace falta tocar `GatewaySnapshot`, el node manager ni la página de diagnóstico.** Igual que en V2-27: `Sources` es una lista de "una entrada por fuente activa, en el orden en que las pasó el host" y tanto `GatewaySnapshot.Build` como `GatewayNodeManager.PublishDiagnostics` recorren esa lista con `foreach`, identificando cada entrada por `Link.Source` y nunca por posición. `AddDiagnosticNodes` ya arma la rama de cada fuente solo `if (_cache.SourceTags(source).Any())`, y la página web (`diagnostics.html`) arma cada tarjeta a partir de `link.source`, nunca del índice del array. Sacar la entrada DA cuando no hay tags DA usa la misma dinámica que ya sostenía a SQL, no agrega una nueva.

**Costo.** `acquisition`/`daThread` en `Program.cs` pasan de no anulables a `DaAcquisitionService?`/`Thread?`, con los chequeos correspondientes al armar `sourceLinks` en el timer y en el `Join` del apagado. `daShutdown` se sigue creando y cancelando siempre, aunque no haya hilo: cancelar un `CancellationTokenSource` sin nadie escuchando no hace nada, así que no hace falta un chequeo extra ahí.

---

### V2-28 — Exponer el endpoint UA a la red para la POC de PI System

**El problema.** La POC de PI System (`docs/LEEME-POC.txt`) necesita que un cliente OPC UA en otra máquina llegue al gateway, y desde la Fase 7 el default es loopback (`127.0.0.1`, ver "A qué interfaz se expone el gateway" en `operacion.md`): un cliente remoto no puede conectar contra eso. Medido hoy, con `Ua:EndpointUrl = opc.tcp://<hostname>:4840/...`: `Get-NetTCPConnection` muestra el listener en `0.0.0.0` y `::` (todas las interfaces), y el gateway lo señala en consola con `WRN "EXPUESTO A LA RED"`.

Medido también hoy, el SDK arma el SAN del certificado del servidor con el host de la URL **al emitirlo**, no lo recalcula después: un certificado emitido con `127.0.0.1` lleva SAN solo `IP=127.0.0.1`, uno emitido con el hostname lleva SAN solo `DNS=<hostname>`, y si la URL cambia después de la emisión el certificado viejo queda desalineado. Conectar con UaExpert por hostname contra un certificado emitido antes con `127.0.0.1` dio `BadCertificateHostNameInvalid`. Renombrar/borrar `pki/own` y rearrancar lo resolvió: UaExpert conectó por hostname con Sign & Encrypt y leyó los tags SQL. Es la misma reemisión que ya describe `operacion.md` para un cambio de bind, aplicada acá por primera vez fuera de loopback.

El primer arranque con el endpoint expuesto también dispara el cartel de permiso de red de Windows, a diferencia del paquete de desarrollo (loopback), donde ese cartel se puede cancelar sin perder funcionalidad.

**Decisión.** Para esta POC se expone únicamente el endpoint UA, y sigue siendo configurable (`Ua:EndpointUrl`, mismo mecanismo de siempre): el default en el JSON versionado del repositorio sigue siendo loopback, la página de diagnóstico sigue sirviendo solo en loopback, y el endpoint UA sigue exigiendo Sign & Encrypt — no se habilita `EnableUnsecureEndpoint`. `AutoAcceptUntrustedCertificates` sigue en `false` por default también en código (`UaOptions.cs`), no solo en el JSON: si la sección `Ua` llegara incompleta en algún paquete con un JSON editado a mano, el servidor no queda en modo permisivo.

**Por qué no tocar el default versionado.** Exponer a la red es, según la Fase 7, una decisión explícita y no un default heredado. Esta POC la toma para su propio `appsettings.Local.json` (fuera de control de versiones), no para `appsettings.json` del repositorio: cualquier otra instalación del gateway sigue arrancando en loopback salvo que alguien lo pida a propósito, igual que antes.

**Costo.** Rompe, a propósito y solo para esta POC, el principio 6 (endpoints solo en loopback). Mitigado por tres cosas: el gateway sigue siendo de solo lectura, la confianza de certificados sigue siendo explícita de los dos lados (nada de `AutoAccept`), y el entorno es TEST, no productivo. Queda un riesgo abierto: el SAN del certificado depende del orden en que se completan los pasos (nombre de máquina antes del primer arranque), y hoy eso se mitiga solo con documentación (`LEEME-POC.txt`, sección "ORDEN CRITICO" y "SI YA ARRANCO CON OTRO NOMBRE"). Pasarle al SDK una lista explícita de nombres alternativos para el SAN, en vez de depender del orden de arranque, queda como mejora futura no implementada.

Aparte: lo que `operacion.md` documenta bajo "Certificate Domain names" (dos entradas, hostname y `127.0.0.1`, en `SubjectName`) no coincide con lo medido hoy para este caso — acá el SAN sale con una sola entrada, la del host de la URL vigente al emitir. No se corrige esa sección: queda anotado acá como discrepancia a revisar. *Revisado el 02/10/2026:* no era una contradicción sino una redacción ambigua. De las dos entradas del log, la primera sale del `DC=` del subject y solo la segunda del SAN, que tiene un único nombre; `operacion.md` se corrigió para decirlo así, con los dos certificados medidos (loopback: SAN `IP=127.0.0.1`; hostname: SAN `DNS=<hostname>`).

Probando el paquete zip se detectó que este último no trae `pki/` (se borra al armarlo, ver `tools/Build-PocPackage.ps1`), así que `pki/trusted/certs` no existe en la máquina destino hasta que el operador necesita mover ahí el certificado del cliente; el gateway ahora crea esa carpeta al arrancar (`Program.cs`) para que ese paso no falle. Aparte, el CSV de ejemplo de esta POC usaba el prefijo `SQL.` en `TAG_NAME_OPC_UA`, el mismo nombre que la carpeta de diagnóstico de la fuente SQL en el address space (`Gateway.Sql`/"Sql"); se resolvió cambiando el prefijo del CSV a `TAGS.`.

---

### V2-30 — Los avisos SQL salen por cambio y solo de tags declarados

**Decisión.** Los avisos de tag ausente y de filas anómalas los decide
`SqlWarningTracker`, una clase pura que vive lo mismo que el servicio. Se avisa la
primera vez, cada vez que cambia el conjunto (nombre + tipo de anomalía) y una sola
vez cuando vuelve a quedar vacío. El estado sobrevive a la reconexión. Las anomalías
cuentan solo tags declarados en el CSV; el total de la tabla queda en `Debug`. Cada
aviso nombra hasta 10 tags ("y N mas").

**Por qué.** "Una vez por sesión" (V2-21) repetía el aviso en cada reconexión, y la
reconexión ya tiene sus propios avisos. Contar toda la tabla hacía que un `NULL` en
un tag que nadie pide disparara un WRN: con las ~10.000 filas de la tabla real sería
ruido permanente. Verificado el 30/09 con un corte de base: tras reconectar no se
repitió ningún aviso (`bb60070`).

### V2-31 — La fuente inactiva se ve, y la tarjeta cuenta caídas

**Decisión.** El snapshot suma `InactiveSources`. Una fuente con tags declarados y
configuración inválida se muestra en la página como "Inactiva" con su motivo, en vez
de desaparecer. Una fuente sin tags sigue sin mostrarse, porque no se usa. La tarjeta
muestra `Disconnections` como "Caídas" en lugar de `Connections`.

**Por qué.** Que la fuente desapareciera le ocultaba al operador justo el caso que
tiene que corregir. El dato viaja en el snapshot y no en un endpoint aparte, para
mantener una sola foto para la página y los nodos UA; entra como parámetro opcional,
y el node manager no lo lee. `Connections` cuenta la primera conexión, así que
"Reconexiones 1" al arrancar sugería una caída que no existió (`f64eda3`).

---

### V2-32 — Con el vínculo SQL caído, los tags SQL bajan a `Uncertain`

**Decisión.** Cada vez que un intento del driver SQL falla (el `catch` de
`SqlAcquisitionService.Run`), la cache marca todos los tags SQL. Ese `catch` atrapa
cualquier excepción de la sesión, no solo la caída de `ReadRows`, del mapeo o de un
reintento de `Connect`: también la escritura en la cache o la evaluación de avisos.
Hoy cualquiera de ellas ya se trata como caída (cuenta en `ReadFailures`, reconecta y
se loguea como corte del vínculo), así que la marca la acompaña y no agrega un
criterio nuevo. En la marca, los tags SQL que están en `Good`, con o sin
límite y también `GoodLocalOverride`, pasan a `Uncertain` *last usable value*,
conservando valor, `SourceTimestamp` y `LastUpdateUtc`. Los que ya estaban en
`Uncertain` o `Bad` (por `Q`, por `NULL`, por ausentes o esperando el primer dato) no
cambian: la marca solo degrada, nunca mejora. El primer ciclo exitoso después de
reconectar pisa todo con la calidad que traiga `Q`, como siempre. Los nodos de
diagnóstico de `Sql` siguen en `Good` (principio 4).

**Por qué.** Es el principio 3 aplicado a un hecho que el gateway conoce: perdió la
base y nadie está refrescando esos valores. Publicarlos en `Good` le decía al cliente
"dato bueno" sin serlo; `Uncertain` conserva el valor (que `Bad` borraría) y avisa la
duda. No es una inferencia por antigüedad, así que no contradice V2-11 ni V2-23: la
señal es la falla del vínculo, no el tiempo. El `SourceTimestamp` no se toca
(principio 2): sigue diciendo cuándo se midió. `LastUpdateUtc` tampoco, porque la marca
no es una muestra.

**Por qué en cada intento fallido y no una vez por caída.** La marca es idempotente:
después de la primera no queda ningún `Good` en SQL y nadie más escribe esos tags, así
que repetirla es inocuo. Condicionarla al flag que decide el aviso "Se corto el vinculo"
la haría fallar en una segunda caída, porque ese flag no se reinicia al reconectar
(bug preexistente, compartido con DA, fuera de esta decisión).

**Por qué en el momento de la detección.** La detección ya existe y la marca se hace ahí
mismo. El costo es la demora: la duda se publica recién al detectar la caída, hasta
40 s después del corte (polling 30 + timeout 10, medido ~32 s en la Fase 5). Se acepta:
bajar ese tiempo exige un polling más corto, que choca con R4.

**Asimetría con DA.** DA llega al mismo estado (`Uncertain` *last usable value*) por
otro camino: la degradación por antigüedad al leer (V2-11), sin marcar nada. SQL no
puede usar ese camino porque su antigüedad está desactivada (V2-23), así que
`Gateway.Core` suma una operación nueva de la cache para marcar una fuente caída.

**La fuente inactiva por config (B10).** Si hay tags SQL pero la config no pasó la
validación, la carpeta `Sql` de diagnóstico en UA publica una vez al arrancar
`LinkState = Disconnected` y `LastError = "Configuracion invalida"`, en `Good`
(principio 4), con los campos que ya existen y sin agregar un valor al enum. Los demás
nodos de esa rama (contadores, tiempos) quedan sin valor: publicar 0 o "nunca"
inventaría datos de una fuente que no corrió.

**Lo que no cubre.** La aplicación de origen muerta con la base viva (caso b de B9,
pregunta P14), y un ciclo colgado sin excepción, que no marca nada hasta que vence
`CommandTimeout`.

---

### V2-33 — Una fila con `TS` nulo o un tipo inesperado aborta el ciclo SQL

**Decisión.** `SqlTagSource.ReadRows` se queda como está: saltea solo las filas con
`TAG` nulo o en blanco. Un `TS` en `NULL`, o una columna con un tipo distinto del
esperado (`TAG` no texto, `TS` no `datetime`, `V` no `real`, `Q` no `smallint`), hace
tirar al reader; la excepción propaga, el ciclo entero se pierde y el host lo trata
como cualquier otra caída: reconecta cada `ReconnectDelaySeconds`, loguea el motivo y
marca los tags SQL (V2-32). No hay captura por fila.

**Por qué.** El esquema real (P1) declara `TAG` y `TS` como `NOT NULL` y fija los
cuatro tipos: ninguno de esos casos puede ocurrir contra la tabla de producción, y el
relevamiento de R7 no encontró nada que los contradiga. El salteo de `TAG` vacío ya es
defensa de más. Capturar por fila agregaría código y un contador nuevo para casos que
la base no permite, y los tipos inesperados no son un problema de una fila sino de la
configuración: si `Sql:Columns` (R6) apunta a otra columna, falla **todas** las filas,
y saltearlas una por una publicaría la tabla entera como ausente con un aviso
engañoso ("la consulta no trajo el tag") en vez del error real del tipo. Que el ciclo
falle deja el motivo exacto en `LastError` y en el log, que es donde hay que mirar.

**Costo.** Si alguna vez la tabla llegara a tener una fila así (un cambio de esquema
en origen, o `Sql:Columns` mal configurado), una sola fila deja **todos** los tags SQL
sin refrescar: quedan en `Uncertain` *last usable value* (V2-32), el diagnóstico de
`Sql` en `Reconnecting` y el log repite "Sigue caido el vinculo" cada 15 s, aunque la
base esté sana. Se diagnostica por el mensaje de la excepción, no por el nombre del
estado. Si se diera contra el servidor real, la salida es saltear la fila y contarla
como anomalía, igual que un `V` o un `Q` nulos (V2-16), y esta decisión se revisa.

---

### V2-34 — Los nombres de base y tabla se reemplazan por genéricos solo hacia adelante

**Decisión.** La base y la tabla de origen pasan a llamarse `PLANT_DB` y `CURRENT_VALUES` en todo el repositorio: código, valores por defecto de `SqlOptions`, `appsettings.json`, tests, script del simulador y documentación. El esquema `dbo` y las columnas `TAG`, `TS`, `V` y `Q` no cambian. El historial de git no se reescribe. Los nombres reales quedan solo en la configuración local de cada instalación (P13).

**Por qué.** El propio dueño del dato considera que el riesgo es bajo: los nombres son genéricos y no exponen nada, y el cambio es una preferencia, no una exigencia (P13). Reescribir el historial rompería los hashes de commit que los documentos citan como evidencia (`verificacion.md`, esta misma página), y esas referencias valen más que ocultar dos identificadores de bajo riesgo.

**Costo.** Los nombres reales siguen visibles en el historial de commits anteriores a este cambio. Además, el paquete de la POC ya no trae la tabla real en `appsettings.json`: quien lo despliegue contra TEST tiene que fijar `Sql:Table` (y `Sql:Database`) en su `appsettings.Local.json`.

---

### V2-35 — `Q` en `NULL` se publica como `Uncertain` sin substatus

**Decisión.** Una fila con `Q` en `NULL` se publica como `Uncertain` sin substatus (`QualitySubstatus.Uncertain`, `0x40000000` en UA), con la calidad `TagQuality.QualityNull`, y ya no como `Uncertain` *last usable value*. `V` en `NULL` queda como está: sigue en `UncertainLastUsableValue`, conservando el último valor y su `SourceTimestamp` (V2-16). El resto de V2-16 no cambia: las dos columnas nulas siguen siendo `Uncertain` y nunca `Bad`.

**Por qué.** V2-16 dice que con `Q` en `NULL` hay medición, y que el valor y el `SourceTimestamp` se actualizan normalmente. *Last usable value* le afirma al cliente UA lo contrario: que el valor que ve es el último útil y ya no se refresca. Además, el snapshot de diagnóstico cuenta como mudo a todo tag en ese substatus (`GatewaySnapshot.cs:267`), así que un tag que entregaba dato en cada ciclo aparecía en la pestaña Operador como uno que "dejó de responder" (con `demo-mixto`, `PRUEBA.NULO_CALIDAD`). La señal de mudo del snapshot es correcta para los demás productores de ese substatus —la degradación por antigüedad de DA (V2-11), la marca de fuente caída (V2-32) y el código 68 que mande la propia fuente— y no se toca: el error estaba en usar ese substatus para un caso que no lo es.

**Pendiente: hora inexistente.** Un `TS` que cae en la hora que se saltea al adelantar el reloj tiene el mismo defecto: `MapTimestamp` degrada con `Downgrade`, que da `UncertainLastUsableValue` aunque el valor sea fresco. Queda fuera porque `Downgrade` es compartido con `V` en `NULL`, donde ese substatus sí es correcto, y porque con la zona por defecto (Argentina, sin horario de verano) el caso no ocurre.

**Pendiente: `V` en `NULL` permanente sin valor previo.** Un tag con `V` en `NULL` desde el arranque nunca tuvo valor, así que el snapshot lo cuenta como mudo que nunca respondió (`NeverAnswered`). Junto a un tag ausente de la tabla, el veredicto pasa a `LikelyCsvMismatch` y manda a revisar el CSV aunque la fila exista y el origen la esté escribiendo (V2-21). Resolverlo exige una categoría nueva en el snapshot. Queda fuera por alcance, porque el proyecto está en la fase de cierre, y porque contra la tabla real es un caso improbable: según el relevamiento de R7, los dos casos `NULL` no existen en producción (`simulador.md`).

---

### V2-36 — Dos comportamientos del stack sobre timestamps no se corrigen

**Decisión.** El gateway sigue dejando `DateTime.MinValue` en el `Timestamp` del nodo cuando el tag no tiene dato (`TagCache` lo arranca en `default` y `GatewayNodeManager.Publish` lo copia tal cual): el principio 2 se cumple en el servidor. Lo que llega al cliente lo decide el stack OPC Foundation 1.5.378.156, que tiene dos comportamientos que no se corrigen:

- **(a) Reemplaza `MinValue` por `UtcNow` en el `SourceTimestamp`, en Read y en suscripción.** `BaseVariableState.ReadValueAttribute` hace `if (m_timestamp == DateTime.MinValue) sourceTimestamp = DateTime.UtcNow;`, y todo camino de lectura pasa por ahí vía `NodeState.ReadAttribute`. En Read, `CustomNodeManager2.Read` repite el chequeo después de leer. En suscripción, `CustomNodeManager2.ReadInitialValue` (valor inicial) y `MonitoredNode2.QueueValue` (cada cambio) arman el `DataValue` con `ServerTimestamp = DateTime.UtcNow` y después leen el nodo, así que los dos timestamps salen de dos `UtcNow` separados por microsegundos.
- **(b) En Read iguala el `ServerTimestamp` al `SourceTimestamp`.** `CustomNodeManager2.Read`, para el atributo Value, hace `ServerTimestamp = SourceTimestamp` después de leer, con cualquier tag y cualquier calidad. En suscripción no pasa: el `ServerTimestamp` es la hora del muestreo.

Las referencias son al código decompilado (ilspycmd) de `Opc.Ua.Types.dll` y `Opc.Ua.Server.dll`, build net10.0 del paquete. El paquete NuGet no trae fuentes ni `.pdb`, y no se cotejó contra el tag del repositorio de GitHub ni contra los otros frameworks.

**Evidencia.** UaExpert, 05/10/2026, contra el gateway con `demo-mixto`:

| Caso | Servicio | `SourceTimestamp` | `ServerTimestamp` |
|---|---|---|---|
| Tag DA `Good` | Suscripción (Data Access View) | 18:05:44.263 | 18:05:45.760 |
| Mismo tag | Read (panel Attributes) | 18:11:45.916 | 18:11:45.916 |
| `PRUEBA.NULO_VALOR`, `PRUEBA.TAG_AUSENTE` | Suscripción | igual al `ServerTimestamp` al milisegundo | — |

La primera fila es (b) en negativo: en suscripción los dos timestamps difieren. La segunda es (b): el mismo tag leído por Read los muestra idénticos. La tercera es (a): el nodo tiene `MinValue` y el cliente ve la hora del muestreo.

**Por qué no se corrige.** Las dos cosas están dentro del stack, no en el gateway. Corregirlas exige sobrescribir clases del stack (`ReadValueAttribute` en una variable propia, `Read` en el node manager) y sostener esas copias contra cada versión nueva del SDK. Un cambio en el comportamiento del stack UA es otro proyecto, no un ajuste de este.

**Costo.**

- Por el timestamp, un cliente no distingue "tag sin dato" de "medido recién". Sí lo distingue por el `StatusCode`, que en esos casos nunca es `Good`: `Bad` (`WaitingForInitialData` o tag ausente) o `Uncertain` (`V` en `NULL` sin valor previo). Un historiador que guarde el `SourceTimestamp` sin mirar la calidad registra una medición en un instante en que no la hubo.
- Un cliente que solo hace Read no ve el `ServerTimestamp` real: ve el `SourceTimestamp` repetido, y no puede medir la antigüedad del dato comparando los dos. Un cliente suscripto sí lo ve.
