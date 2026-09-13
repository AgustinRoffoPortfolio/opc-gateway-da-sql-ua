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

- **Reconexión (R5).** El driver no tiene política de reintentos adentro por
  diseño (V2-10); vive en el host y se prueba en el paso 5.
- **El mapeo contra la base real.** Se verificó con filas construidas a mano
  (paso 3, más abajo); que lo que llega de `CURR_DATA` produzca las muestras
  esperadas se prueba de punta a punta en el paso 5.
- **Timeout de consulta.** Configurado, sin provocar todavía.

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