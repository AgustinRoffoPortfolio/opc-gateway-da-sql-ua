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
| `1` `2` `3` | Corta o restablece el campo del grupo correspondiente |
| `Esc` | Termina |

---

## Pérdida de campo (P6)

Al cortar un grupo, sus filas quedan así:

- **`V` congelado** en el último valor bueno.
- **`Q` en 20** (`BadLastKnown`).
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
  movimiento.

---

## Limitaciones conocidas

Están aceptadas a propósito, no son deuda a corregir.

- **Escribe fila por fila.** Un `MERGE` por tag y por ciclo. Con ocho tags es
  irrelevante; con las ~10.000 filas de la tabla real sería lento. El simulador
  no necesita escalar: lo que tiene que escalar es el driver que **lee**, y eso
  se prueba contra la tabla real o contra un catálogo más grande, no contra este
  loop de escritura.
- **Una sola conexión abierta todo el tiempo**, sin reconexión. Si se cae la
  base, el simulador muere. La reconexión es requisito del gateway (R5), no de
  esta herramienta.
- **Tres de los cinco códigos de calidad reales no se generan todavía.** Están en
  el catálogo (`commFailureQuality`, `localOverrideQuality`, `uncertainQuality`)
  pero el loop solo usa 192 y 20. Ver `docs/v2/calidad-observada.md`.
- **`TS` en hora local**, sin conversión. Es lo que hace la aplicación de origen
  (P4); la conversión a UTC es responsabilidad del gateway.