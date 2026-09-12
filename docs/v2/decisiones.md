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