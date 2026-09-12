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

**Consecuencia inmediata.** Los CSV de `config/` todavía no tienen la columna `SOURCE`. Entre esta decisión y la Fase 4, el gateway no levanta.

**Pendientes.** Qué valor lleva `DATA_TYPE` en una fila SQL (bloque de mapeo de datos). Qué hace el validador con `SCAN_RATE_MS` y `DEADBAND` en filas SQL, que son parámetros del grupo OPC DA y no aplican al polling de R4 (bloque de cache y ritmos).

---

### V2-6 — Parámetros de la fuente SQL en el JSON

**Decisión.** Sección `Sql` en `appsettings.json`, con los parámetros de R6 como claves sueltas:

```json
"Sql": {
  "Host": "127.0.0.1",
  "Port": 1433,
  "Database": "SCADA_HST",
  "Schema": "dbo",
  "Table": "CURR_DATA",
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

**Validación al arranque.** `Host`, `User` o `Password` vacíos: falla con mensaje explícito. Intervalos fuera de los rangos de R4 y R5: advertencia en el log, no error, porque los requisitos dan rangos esperados y no límites.

**Pendiente.** Cómo se arma `[Database].[Schema].[Table]` desde tres claves de configuración sin que un valor mal escrito termine siendo SQL inyectado (bloque de arquitectura del driver).

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

**`TargetFramework` `net10.0`, sin el sufijo `-windows`.** `Gateway.Da` apunta a `net10.0-windows` porque COM solo existe en Windows. Nada del driver SQL toca COM, así que no hereda esa restricción. Dejarlo en `net10.0` documenta en el propio `.csproj` que esta fuente no depende de la plataforma, y deja marcado cuál es el proyecto que ata el gateway a Windows: el DA, no este. Costo cero. `Gateway.Host` sigue siendo el único con `PlatformTarget x86` (principio 7); el driver SQL no fija plataforma y se compila para la del host.

**Qué expone.** `SqlTagSource`, clase pasiva con la misma forma que `OpcDaTagSource`: `Connect()`, `IsConnected`, `ReadAll()` devolviendo `IReadOnlyDictionary<string, TagSample>`, e `IDisposable`. Sin hilos, sin temporizadores, sin política de reintentos adentro. El porqué está en V2-10.

**Pendiente.** El nombre del tipo de opciones (`SqlOptions`, espejo de `DaOptions`) y en qué proyecto vive: `DaOptions` está dentro de `Gateway.Da`, así que por simetría va dentro de `Gateway.Sql`. Se confirma al crear el proyecto en la Fase 3.

---

### V2-9 — Nombre de tabla configurable sin abrir la puerta a inyección

**Decisión.** `Database`, `Schema` y `Table` se validan al arrancar contra una lista blanca —letra o guion bajo inicial, después letras, dígitos o guion bajo, hasta 128 caracteres— y se arman como `[Database].[Schema].[Table]` duplicando cualquier `]` interno. Si alguno no valida, el gateway falla al arrancar con el nombre de la clave y el valor recibido. No se corrige ni se sanitiza: se rechaza.

**Por qué no alcanza con parámetros.** Un parámetro de ADO.NET (`@algo`) ocupa el lugar de un *valor*, no de un identificador. `SELECT * FROM @tabla` no es SQL válido. El nombre de la tabla se arma pegando texto por definición, y pegar texto es el mecanismo de la inyección. Como no se puede evitar la concatenación, lo que se controla es qué se concatena.

**Por qué igual se valida, si el atacante sería quien edita el JSON.** Quien edita `appsettings.json` ya tiene la máquina; no es un anónimo de internet, y por ese lado el riesgo real es bajo. La validación se sostiene por otras dos razones. Una operativa: convierte un typo en un error claro al arrancar, en vez de en un error de sintaxis de SQL treinta segundos más tarde y a través del log del driver. Otra defendible en entrevista: la diferencia entre "es seguro porque nadie malicioso toca el JSON" y "es seguro porque no acepta nada que no sea un identificador". Solo la segunda es una propiedad del código.

**Por qué no `QUOTENAME`.** Delegarle el escape a SQL Server obliga a una consulta extra solo para construir la consulta, y pone la validación del otro lado de la red cuando puede estar acá, antes de abrir la conexión. Los corchetes con `]` duplicado son la misma regla de `QUOTENAME`, aplicada localmente.

**Consecuencia sobre R6.** El requisito pide que la tabla sea configurable, y lo sigue siendo: cualquier identificador legítimo de SQL Server pasa. Lo que queda afuera son nombres con espacios, puntos o caracteres raros, que en esta tabla no existen. Si alguna vez aparece uno, se amplía la lista blanca como decisión nueva, no aflojando la validación en el momento.

**Cierra el pendiente de V2-6** sobre cómo armar `[SCADA_HST].[dbo].[CURR_DATA]` desde tres claves sueltas.

---

### V2-10 — Aislamiento del loop SQL (invariante 8)

**Decisión.** El driver SQL es pasivo y el hilo lo aporta el host, igual que con DA: un `Thread` dedicado, `IsBackground = true`, `Name = "SQL polling"`, sin `SetApartmentState`. La política —cada cuánto consultar, cuándo reconectar, qué hacer con un tag ausente— vive en un `SqlAcquisitionService` dentro de `Gateway.Host`, espejo de `DaAcquisitionService`. La cache es lo único compartido entre los tres hilos: adquisición DA, adquisición SQL y publicación UA.

**Qué se descubrió mirando el código de la v1.** `OpcDaTagSource` no tiene loop, ni hilo, ni `Task`: es sincrónico y pasivo. El `Thread` está en `Program.cs` y el `while` con espera bloqueante, en `DaAcquisitionService`. O sea que el invariante 8 ya estaba implementado para una fuente antes de llamarse así, y el comentario del propio `Program.cs` lo dice: el apartamento MTA que exige COM, y que una lectura DA lenta no frene la publicación UA. Sumar SQL no inventa un patrón: agrega un tercer hilo al que ya existe.

**Por qué un hilo bloqueante y no `async`/`await`.** Es ir a contramano de `Microsoft.Data.SqlClient`, que expone todo en versión `Async`, y conviene decirlo en voz alta. El argumento clásico a favor de `async` es no desperdiciar un hilo del pool esperando E/S, y vale cuando hay miles de operaciones concurrentes. Acá hay tres hilos en total y uno durmiendo treinta segundos no le saca lugar a nadie. Lo que se gana a cambio es un único modelo de concurrencia para las dos fuentes: un solo patrón que explicar, que mantener y que depurar, en vez de dos conviviendo. Si el driver SQL creciera a varias consultas concurrentes, la decisión se revisa.

**Por qué no un hilo compartido que alterne DA y SQL.** Viola el invariante 8 por construcción: una consulta SQL lenta frenaría la lectura DA. Queda descartado.

**El hilo propio no alcanza solo.** Protege a DA de que SQL se demore, pero no protege al hilo SQL de quedar colgado para siempre en una consulta que nunca vuelve. Eso lo cierra el `CommandTimeoutSeconds` de V2-6: con timeout, una consulta trabada termina en excepción, entra al camino de reconexión de R5 y el loop sigue vivo. Sin timeout, el hilo SQL se cuelga callado y los tags SQL se congelan sin que nada lo reporte.

**Qué queda para la Fase 3.** La degradación por antigüedad de la v1 vive en `DaAcquisitionService` y es global (3 ciclos, 3000 ms). Si `SqlAcquisitionService` es un servicio aparte, no la hereda por accidente, que es justo lo que se quiere según lo anotado en la sección C: para los tags SQL la calidad sale de `Q` (P6), no de la antigüedad. Falta escribir esa decisión con su porqué y ver qué reporta el `LinkState` de la fuente SQL.