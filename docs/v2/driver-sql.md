# Driver SQL — evidencia de verificación

Evidencia de lo que se probó del driver SQL, con fecha y resultado. Las
decisiones y sus porqués están en `decisiones.md`; acá va lo que se corrió.

## Fase 3, paso 2 — Esqueleto de `Gateway.Sql`

**13/09/2026.**

### El borde con el cliente SQL es real (V2-8)

V2-8 dice que el principio 1 lo impone el grafo de referencias entre proyectos
y no la disciplina de quien escribe, y que la forma de comprobarlo no es leer
código sino intentar romperlo.

Se agregó a `src/Gateway.Host/Program.cs` una línea temporal:

```csharp
var probeBorde = new Microsoft.Data.SqlClient.SqlConnection();
```

El build falla con `CS0234`: el espacio de nombres `Data` no existe en
`Microsoft`. Falla incluso escribiendo el nombre completo del tipo, porque el
paquete está declarado dentro de `Gateway.Sql` y no se propaga a quien lo
referencia. La línea se quitó después de verificarlo y el árbol quedó limpio.

### Target framework

`Gateway.Sql` es `net10.0` sin sufijo de plataforma (V2-8). Para que pudiera
referenciar a `Gateway.Core`, que era `net10.0-windows`, hubo que bajar `Core`
a `net10.0`: un proyecto sin sufijo no puede referenciar a uno con él. Ningún
archivo de `Core` usaba una API exclusiva de Windows. Detalle y porqué en la
entrada V2-8 de `decisiones.md`.

En el build de la solución se lee de un vistazo cuáles son los proyectos que
atan el gateway a Windows (`Da`, `Ua`, `Web`, `Host`, `Tests`) y cuáles no
(`Core`, `Sql`, `SqlSimulator`).

### El driver conecta y trae filas

Verificación del paso: `SqlTagSource` conecta contra el SQL Server del
contenedor y lee filas de `CURR_DATA`. Cinco tests de integración en verde,
con la base levantada y el simulador habiendo corrido antes.

Los tests **no escriben** en la tabla: leen lo que dejó el simulador, que es
la situación real donde `CURR_DATA` la escribe otra aplicación.

**Cómo se corren:**

```powershell
docker compose up -d
$env:GATEWAY_SQL_TEST_USER = "sa"
$env:GATEWAY_SQL_TEST_PASSWORD = (Get-Content .env | Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' }) -replace '^MSSQL_SA_PASSWORD=', ''
dotnet test Gateway.slnx --filter Category=Integration
Remove-Item Env:\GATEWAY_SQL_TEST_PASSWORD
```

La password se lee del `.env` en vez de tipearse: no queda en el historial de
la terminal, y no se duplica un secreto que ya existe en un archivo.

**Por qué el interruptor son las credenciales.** xUnit 2.9.3 decide el `Skip`
al descubrir los tests, no al correrlos, así que un test no puede saltearse a
sí mismo al ver que no hay base. Como las credenciales no pueden vivir en el
repositorio (V2-7), el test las lee de variables de entorno de todos modos, y
sin ellas se omite. La misma pieza que protege el secreto hace el salteo.

**Resultado sin base:** 111 correctos, 5 omitidos con el motivo explicado.
`dotnet test` sigue en verde en una máquina sin Docker.

### Qué NO se verificó todavía

Los tres se cerraron en el paso 5, más abajo: la reconexión (R5) y el timeout
de consulta se provocaron cortando el contenedor, y el mapeo corrió contra
`CURR_DATA` en el ciclo de polling.

## Fase 3, paso 3 — Mapeo de fila a `TagSample`

**13/09/2026.**

### Todo el paso se verificó sin base y sin esperas

Es la consecuencia práctica del corte que introdujo `SqlTagRow`: leer necesita
la base y no tiene lógica; mapear tiene toda la lógica y no necesita la base.
Los 10 tests de `SqlTagMapperTests` construyen las filas a mano, así que corren
en cualquier máquina sin Docker y sin `Thread.Sleep`.

Es la diferencia con los tests de degradación por antigüedad de la v1, que
esperan 120 ms cada uno porque `Degrade` lee el reloj por dentro.

### Qué cubren los tests

Del mapeo: conversión de hora local a UTC contra la zona configurada, zona
vacía resuelta a la de la máquina, `V` en `NULL`, `Q` en `NULL`, pérdida de
campo (`Q = 20`, el caso de P6), substatus no previsto y nombres que difieren
en mayúsculas.

Los dos casos de horario de verano se verifican contra `Pacific Standard Time`
y no contra la zona local: Argentina no lo aplica desde 2009, así que la hora
inexistente y la ambigua no se pueden reproducir acá.

De la cache, con los tipos nuevos: `Float` escalado en `double` y casteado al
final, booleano que llega como número, y muestra con calidad utilizable pero
sin valor.

**Resultado:** 128 correctos, 0 con error, 2,7 s.

## Fase 3, paso 5 — Polling, reconexión y tags ausentes

**13/09/2026.**

### Números medidos

Son del driver SQL corriendo en este repo, contra la base del simulador en
Docker. No son números de la v1 ni de la línea base.

| Qué | Valor |
|---|---|
| Detección de la base caída | ~10 s: el `CommandTimeout`, no el intervalo de polling |
| Recuperación | 40 s: dos esperas de reconexión de 15 s más el arranque de SQL Server |
| Ciclo SQL con 10 filas | 3,5 ms el último, 15,8 ms de promedio, 44,3 ms el máximo |
| DA durante el corte de SQL | 234 ciclos, 0 fallos, 0 desconexiones |
| SQL tras el ciclo completo | 7 ciclos, 2 fallos, 2 conexiones, 1 desconexión |

El promedio de 15,8 ms está inflado por el arranque de ADO.NET en el primer
ciclo y se midió sobre 4 ciclos, que no alcanzan para diluirlo. Si el número se
cita afuera, va con esa aclaración o se vuelve a medir con más ciclos.

### Cómo se provocó la caída

Con `docker compose stop`, no con un mock. El stack trace real mostró
`Error Number: -2`, que es el timeout de ADO.NET, y con eso quedó confirmado
V2-20: una conexión abierta no se entera de que se cayó la base hasta que falla
una operación. El fallo de la consulta es el mecanismo de detección, y por eso
la detección tarda el timeout y no el polling.

El log de la corrida no se versiona: quedó en `scratch/`, que git ignora.

### La fuente caída no arrastró a la otra

La fila de DA de la tabla es evidencia del primer escenario de la Fase 5 —base
caída sin afectar a DA— obtenida de rebote en esta corrida: con SQL cortado y
reconectando, DA completó 234 ciclos sin un solo fallo ni desconexión. Falta
el escenario inverso y la verificación en UaExpert, que son de la Fase 5.

### Tags declarados que la consulta no trae (V2-21)

El primer caso de V2-21 quedó implementado: el cruce entre lo declarado y lo
recibido vive en `TagCache.MissingTags`, y el host publica `RowMissing` —mismo
`StatusCode` que `ItemRejected`, otro nombre— y avisa una vez por tag y por
sesión.

**Verificado con tests, no de punta a punta.** Hoy no se puede ver funcionando:
el CSV todavía no declara tags con origen SQL (V2-5 es de la Fase 4), así que
la lista de declarados para SQL viene vacía y no hay nada que pueda faltar. Lo
sostienen tres tests sobre `TagCache`, de los cuales el que importa es el de
mayúsculas: es el que falla si alguien desalinea el criterio de comparación de
las dos puntas, que es la forma en que este cruce puede romperse en silencio
contra la base real. Ver un `Bad` real en UaExpert por un tag declarado y
ausente queda como verificación de la Fase 4.

**Lo que sí quedó probado de rebote es el filtrado de R3:** con los diez tags
leídos y ninguno declarado como SQL, la cache los descartó enteros. De la tabla
solo entra lo que el CSV declara.

### Lo que quedó flojo y se sabe

- **El comentario de `SqlTagSource.ReadRows` no dice la verdad.** Afirma que una
  fila rota se saltea y la lectura sigue, pero lo único que se saltea es un
  `TAG` nulo o vacío: si falla la lectura del timestamp, se pierden las diez mil
  filas. Hay que corregir el comentario o el código.
- **Los 5 tests de integración se omiten aun con el contenedor arriba.** La
  condición de omisión mira las variables de entorno, así que dependen de cómo
  quedó la sesión de PowerShell y no del estado real de la base.
- **El aviso de anomalías del mapeo se repite tras una reconexión.** El estado
  entre ciclos no se reinicia al abrir una sesión nueva, así que la huella se
  vuelve a loguear aunque no haya cambiado. Es aceptable —una reconexión es
  información— pero no estaba escrito.