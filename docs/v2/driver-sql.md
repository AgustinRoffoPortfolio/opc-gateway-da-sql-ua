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
- **Mapeo a `TagSample`.** `ReadRows` devuelve filas crudas. Tipos, `NULL`,
  calidad y hora local a UTC son el paso 3.
- **Timeout de consulta.** Configurado, sin provocar todavía.