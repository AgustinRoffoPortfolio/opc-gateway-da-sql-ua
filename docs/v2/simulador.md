# Simulador de `CURR_DATA`

Replica en local la tabla que en producción escribe otra aplicación, para poder
desarrollar y probar el driver SQL sin depender del servidor real (P10).

No es parte del gateway: es andamiaje de desarrollo. El gateway **nunca** crea ni
escribe esta tabla, solo la lee.

---

## Qué hay

| Ruta | Qué es |
|---|---|
| `compose.yml` (raíz del repo) | SQL Server 2019 Developer en contenedor |
| `tools/SqlSimulator/schema/01-create-curr-data.sql` | Esquema de la tabla, fiel al `CREATE` real (P1) |
| `tools/SqlSimulator/config/tags.simulator.jsonc` | Catálogo de tags simulados |
| `tools/SqlSimulator/Program.cs` | El simulador |

---

## Cómo se levanta

### 1. La base

```powershell
docker compose up -d
```

La password del usuario `sa` sale de un archivo `.env` en la raíz, que git ignora.
Ver `.env.example`.

El puerto se publica solo en `127.0.0.1`, no en todas las interfaces: la base no
queda expuesta a la red local. Es el mismo criterio que los endpoints en loopback
del gateway.

### 2. El esquema

Una sola vez, o cada vez que se borre el volumen. El script es idempotente:
correrlo de nuevo no pisa datos.

```powershell
docker cp .\tools\SqlSimulator\schema\01-create-curr-data.sql gateway-sql:/tmp/01-create-curr-data.sql
docker exec -it gateway-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -i /tmp/01-create-curr-data.sql
```

### 3. El simulador

La cadena de conexión sale de una variable de entorno, no del código ni de un
archivo versionado:

```powershell
$pw = Read-Host "Password de sa" -AsSecureString
$env:SQLSIM_CONNSTR = "Server=127.0.0.1,1433;Database=SCADA_HST;User ID=sa;Password=$([System.Net.NetworkCredential]::new('', $pw).Password);Encrypt=True;TrustServerCertificate=True;Connect Timeout=5"
$pw = $null
dotnet run --project tools\SqlSimulator
```

La variable vive solo en esa ventana de PowerShell. Al abrir una nueva hay que
definirla otra vez.

Si la base todavía no acepta conexiones (recién levantada tarda unos 15 s), el
simulador reintenta cada `reconnectMs` hasta lograrlo. Lo mismo si se cae con el
simulador andando: loguea `Se perdio la conexion`, espera y reconecta.

Se puede correr en segundo plano con la salida redirigida a un archivo. Sin
consola interactiva las teclas `1` `2` `3` y `Esc` no están disponibles: se
termina matando el proceso.

**`Server=127.0.0.1` y no `localhost`.** En Windows, `localhost` resuelve primero
a `::1` (loopback de IPv6), pero Docker publica el puerto en `127.0.0.1` (IPv4).
El cliente de SQL se queda esperando en la dirección equivocada hasta agotar el
timeout, con un mensaje de error que habla de red y no de resolución de nombres.

**`TrustServerCertificate=True`.** El driver moderno cifra por defecto y la base
local usa un certificado autofirmado que el cliente no puede validar. Es válido
para desarrollo; contra el servidor real puede tener que ir en `False`, y por eso
el parámetro va a la configuración y no al código.

---

## Qué hace

Cada tag del catálogo pertenece a un **grupo de scan** con su propio período,
que es como la aplicación de origen refresca los datos reales (P7). El loop
revisa qué grupos vencieron y actualiza solo esas filas.

El valor sale de un **modelo determinista**, nunca de un número al azar:

| Modelo | Qué hace | Parámetros |
|---|---|---|
| `Sine` | Oscila alrededor de un valor base | `baseValue`, `amplitude`, `periodSeconds` |
| `Ramp` | Crece a ritmo constante desde el arranque | `baseValue`, `ratePerSecond` |
| `Steady` | Se queda quieto | `baseValue` |

El seno se ancla a los segundos desde medianoche, así la fase no salta al
reiniciar el simulador. La rampa se ancla al arranque del proceso: si acumulara
desde medianoche, un totalizador arrancaría en un número arbitrario según la hora.

`PLANTA_02_ESTADO_BOMBA_01` está en `Steady` con valor `1`: es el caso del
booleano que llega como 0 o 1 en la columna `real` (P2).

### Teclas

| Tecla | Qué hace |
|---|---|
| `1` `2` `3` | Cicla el estado del grupo: sano → campo perdido → falla de comunicación → sano |
| `Esc` | Termina |

### Condición propia del tag

Aparte del estado del grupo, un tag puede declarar en el catálogo una condición
que le es propia, con el campo `condition`:

| `condition` | Qué escribe |
|---|---|
| *(ausente)* | Normal: `Q = 192` y `V` del modelo |
| `LocalOverride` | `Q = 216` y `V` clavado en `overrideValue` |
| `Uncertain` | `Q = 64`, `V` sigue el modelo |
| `NullValue` | `V = NULL` |
| `NullQuality` | `Q = NULL` |

**El estado del grupo tiene precedencia sobre la condición del tag.** Si el campo
de un grupo se cortó, sus tags reportan esa falla y su condición propia no se
aplica: si no hay comunicación, no hay nada que reportar sobre el tag. Es una
simplificación del andamiaje, no un comportamiento observado en la tabla real.

**Los dos casos `Null` no existen en producción.** El relevamiento de R7 no
encontró ninguna fila con `Q` nula, y las cinco calidades observadas suman el
total de la tabla. Son casos de borde que el esquema permite y que el mapeo del
driver contempla, así que hay que poder provocarlos; van declarados a mano en dos
tags dedicados (`PRUEBA_NULO_VALOR`, `PRUEBA_NULO_CALIDAD`) justamente para que
se vea que son forzados y no un estado natural de la planta.

**El simulador no reproduce las proporciones de la tabla real.** Con diez tags no
hay forma honesta de representar un 0,04 %. El objetivo es que los cinco códigos
sean *alcanzables* a voluntad, que es lo que necesita el driver para probar su
mapeo. La proporción observada sirve para otra cosa: dimensionar qué fracción de
la tabla está en `Bad` en cualquier momento dado.

---

## Pérdida de campo (P6)

Al cortar un grupo —en cualquiera de sus dos estados de falla— sus filas quedan
así:

- **`V` congelado** en el último valor bueno.
- **`Q` en 20** (`BadLastKnown`) o **en 24** (`BadCommFailure`), según el estado.
- **`TS` sigue avanzando.**

Lo último es lo importante y es a propósito. La aplicación de origen sigue viva y
escribiendo la fila; lo que se cayó es el campo. Un tag degradado puede tener el
timestamp **más fresco** de toda la tabla, así que la falla se detecta por la
calidad y no por antigüedad. Es el escenario que justifica no aplicarles a los
tags SQL la degradación por antigüedad que la v1 usa para los tags DA.

---

## Cómo se verifica

Con el simulador corriendo, desde otra ventana:

```powershell
docker exec -it gateway-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -d SCADA_HST -Q "SET NOCOUNT ON; SELECT LEFT(TAG,38) AS TAG, CONVERT(varchar(12), TS, 114) AS TS, V, Q FROM dbo.CURR_DATA WITH (NOLOCK) ORDER BY TAG"
```

Qué mirar:

- El `TS` de cada grupo avanza a su propio ritmo. Los tags del grupo lento quedan
  visiblemente atrás de los del rápido.
- Los decimales salen con **punto**. Los valores viajan como `SqlParameter`
  tipados, nunca concatenados en el texto del SQL, así que la coma decimal de
  es-AR no tiene por dónde colarse.
- Al cortar un grupo, sus filas pasan a `Q = 20` con `V` clavado y `TS` en
  movimiento. Apretando la tecla otra vez pasan a `Q = 24`, con el mismo `V` y el
  `TS` igual de fresco: lo único que cambia entre las dos fallas es el substatus.

### Corrida del 13/09/2026

Los cinco códigos y los dos `NULL`, con los tres grupos sanos:

| TAG | V | Q |
|---|---|---|
| `PLANTA_01_MEDICION_PRESION_ENTRADA` | 12,564428 | 192 |
| `PLANTA_01_MEDICION_PRESION_SALIDA` | 9,0 | 216 |
| `PLANTA_02_MEDICION_DENSIDAD` | 0,85467809 | 64 |
| `PRUEBA_NULO_VALOR` | `NULL` | 192 |
| `PRUEBA_NULO_CALIDAD` | 2,0 | `NULL` |

Los decimales llegaron con punto, no con coma: los valores viajan como
`SqlParameter` tipados y la cultura es-AR no tiene por dónde colarse.

Después de cortarle el campo al grupo RAPIDO dos veces, sus cinco tags —incluidos
los tres con condición propia— quedaron en `Q = 24`, y los otros dos grupos
siguieron en 192 y 64 sin enterarse. Tres cosas quedaron verificadas de una:

- **La precedencia.** El 216, el 64 y los dos `NULL` desaparecieron detrás del
  estado del grupo.
- **Qué se congela.** `PRESION_SALIDA` quedó en 9,0, el valor que había forzado
  el operador, y no en el del seno. Lo que se conserva es lo último que se
  escribió de verdad. `PRUEBA_NULO_VALOR` quedó en 1,0 y no en `NULL`, porque un
  nulo nunca entra al registro de último valor bueno: `BadCommFailure` ya dice
  que el dato no sirve, y una columna nula encima sería ruido.
- **El aislamiento entre grupos**, que es el invariante 8 visto del lado del
  dato, antes de que exista el driver que lo lee.

Y el `TS` de las cinco filas caídas quedó más nuevo que el de los grupos sanos,
que es V2-23 otra vez: la falla se detecta por calidad, nunca por antigüedad.

---

## Limitaciones conocidas

Están aceptadas a propósito, no son deuda a corregir.

- **Escribe fila por fila.** Un `MERGE` por tag y por ciclo. Con ocho tags es
  irrelevante; con las ~10.000 filas de la tabla real sería lento. El simulador
  no necesita escalar: lo que tiene que escalar es el driver que **lee**, y eso
  se prueba contra la tabla real o contra un catálogo más grande, no contra este
  loop de escritura.
- **Reconexión simple, con espera fija.** Una sola conexión abierta todo el
  tiempo; si no se puede abrir o se cae, el simulador lo loguea, espera
  `reconnectMs` (5 s por defecto, en el catálogo) y vuelve a intentar, sin
  límite de intentos ni espera creciente. El grupo que estaba escribiendo
  cuando se cortó pierde esa vuelta y se escribe en su próximo vencimiento; el
  modelo vive en memoria, así que los valores siguen la curva como si nada.
  Durante la espera las teclas no se atienden. La conexión va **sin pooling**,
  igual que la del gateway (V2-20): con pooling, después de un `Open` fallido
  SqlClient devuelve el mismo error cacheado durante un "blocking period" sin
  volver a intentar, y el simulador tardaba ~37 s más que el gateway en
  reconectar con la base ya viva. Alcanza para poder cortar la
  base a propósito (para medir la reconexión del gateway, R5) sin tener que
  relanzar el simulador.
- **El valor forzado de `LocalOverride` es fijo.** Un operador real puede cambiar
  el valor que dejó puesto; acá sale del catálogo y no se mueve. Alcanza para lo
  que el driver tiene que distinguir, que es el código de calidad.
- **`TS` en hora local**, sin conversión. Es lo que hace la aplicación de origen
  (P4); la conversión a UTC es responsabilidad del gateway.