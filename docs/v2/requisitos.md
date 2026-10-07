# Fuente SQL Server — especificación

Especificación de la segunda fuente de datos de la v2 del gateway. Define qué se construye; el cómo se resuelve en `decisiones-v2.md`.

Los IDs `R1`–`R7` identifican requisitos y `P1`–`P15` puntos de especificación aclarados durante el diseño. El resto de `docs/` los referencia por número.

---

## Requisitos

### R1. Segunda fuente: SQL Server

Además de levantar datos de un OPC DA Server, el gateway levanta datos desde una base SQL Server. Los tags de las dos fuentes conviven en el mismo servidor OPC UA y en la misma estructura de datos. Los nombres de tag son únicos y no se repiten entre fuentes.

### R2. Columna de origen en el CSV de tags

El CSV de configuración incorpora una columna que identifica el origen de cada tag, con valor `OPCDA` (u `OPC_DA`) o `SQL`.

Igual que un tag DA declara su nombre del lado DA y su nombre del lado OPC UA, un tag SQL declara su nombre del lado SQL (el valor de la columna `TAG` de la tabla) y su correspondiente nombre del lado OPC UA.

### R3. Consulta: toda la tabla, sin filtrar

Cada ciclo ejecuta una sola consulta que trae la tabla completa:

```sql
SELECT [TAG]
      ,[TS]
      ,[V]
      ,[Q]
  FROM [PLANT_DB].[dbo].[CURRENT_VALUES] WITH (NOLOCK)
```

> Los nombres de base y tabla de esta cita se reemplazaron por genéricos (`PLANT_DB`, `CURRENT_VALUES`) a pedido del dueño del dato (P13). Columnas, tipos y opciones son los originales.

- **Sin `WHERE`.** El filtrado a los tags configurados se hace en el gateway, no en el servidor SQL, para no cargarlo con una cláusula de miles de términos.
- **Con `WITH (NOLOCK)`.** Evita bloqueos mientras otras aplicaciones actualizan la tabla.
- Base, esquema y tabla están parametrizados (R6).

### R4. Intervalo de polling configurable

El intervalo de interrogación a la base se configura en segundos. El rango de trabajo esperado va de 20 s a 1 min.

### R5. Conexión persistente con reconexión automática

- Una única conexión inicial, sobre la que se ejecutan todas las consultas sucesivas.
- El gateway monitorea el estado de esa conexión. Si detecta que no está activa, o si falla una consulta o lectura, intenta reconectar.
- Los reintentos se espacian con un intervalo de reconexión configurable, en el rango de 10 a 30 s, para no entrar en un loop ante un problema de red.

### R6. Parámetros de configuración en el JSON

| Parámetro | Valor de referencia |
|---|---|
| IP del servidor SQL | — |
| Puerto | `1433` (opcional) |
| Nombre de la base | `PLANT_DB` |
| Esquema (owner) | `dbo` |
| Nombre de la tabla | `CURRENT_VALUES` |
| Intervalo de polling (s) | 20–60 |
| Intervalo de reconexión (s) | 10–30 |
| Columna del nombre de tag | `TAG` |
| Columna del timestamp | `TS` |
| Columna del valor | `V` |
| Columna de la calidad | `Q` |
| Usuario | — |
| Password | — |

Los nombres de columna son parámetros y no constantes porque pueden diferir entre instalaciones de la aplicación de origen.

### R7. Calidad en códigos OPC DA

La columna `Q` usa los mismos valores de calidad que OPC DA. El valor `192` corresponde a calidad buena.

---

## Sistema de origen

La tabla no la define el gateway: existe en un sistema en producción y otra aplicación la escribe.

- **Tabla de valores actuales, no historiador.** Una única fila por tag, que se pisa con `UPDATE` (P3). La clave primaria sobre `TAG` garantiza que no haya duplicados.
- **Escritura concurrente.** Otras aplicaciones actualizan la tabla mientras el gateway la lee. Es el motivo del `NOLOCK` (R3).
- **Origen de los datos.** La aplicación que escribe la tabla los toma de un OPC DA Server. Por eso valor, timestamp y calidad conservan la semántica de OPC DA.
- **Tamaño.** Del orden de 10.000 filas, de las que el gateway consumiría algunos miles.
- **Ritmo de actualización.** Cada tag se actualiza cuando llegan datos de campo, con intervalos distintos según su grupo de scan (P7). No existe un período único de refresco, así que no hay un umbral de antigüedad aplicable a todos los tags por igual.

### Esquema de la tabla (P1)

```sql
CREATE TABLE [dbo].[CURRENT_VALUES](
	[TAG] [varchar](50) NOT NULL,
	[TS] [datetime] NOT NULL,
	[V] [real] NULL,
	[Q] [smallint] NULL,
 CONSTRAINT [PK_CURRENT_VALUES] PRIMARY KEY CLUSTERED
(
	[TAG] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
```

> Los nombres de base y tabla de esta cita se reemplazaron por genéricos (`PLANT_DB`, `CURRENT_VALUES`) a pedido del dueño del dato (P13). Columnas, tipos y opciones son los originales.

| Columna | Tipo | Nulo | Implicancias |
|---|---|---|---|
| `TAG` | `varchar(50)` | no | Clave primaria. Hasta 50 caracteres, sin duplicados. SQL Server compara sin distinguir mayúsculas |
| `TS` | `datetime` | no | Resolución de unos 3 ms. No existe el caso de timestamp nulo |
| `V` | `real` | sí | Float de 4 bytes, unos 7 dígitos significativos |
| `Q` | `smallint` | sí | Entero con signo de 16 bits |

### Semántica de los datos

- **Tipos de tag (P2).** Solo analógicos. Un booleano, si aparece, llega como 0 o 1 en la misma columna `real`. No hay tags de texto.
- **Timestamp (P4).** `TS` está en hora local de la aplicación de origen. En OPC UA el `SourceTimestamp` es UTC por definición, así que publicarlo sin convertir correría los tags SQL respecto de los DA. La conversión es opcional si implicara un costo de CPU relevante; no lo implica.
- **Calidad (P5).** `Q` replica los códigos de OPC DA, con la misma codificación que ya mapea el driver DA de la v1.
- **Pérdida del campo (P6).** Cuando la aplicación de origen pierde comunicación con el campo, marca `Q` con una calidad mala del tipo *Last Known Value* (por ejemplo `20`). No deja la fila con `192` y el valor congelado. Una fuente muerta se detecta por la calidad, sin necesidad de inferirla desde el gateway.

---

## Entorno objetivo

- **Motor (P8).** SQL Server 2019 o 2025, con autenticación por usuario y password de SQL Server. Edición Developer o superior.
- **Despliegue (P10).** El desarrollo va contra una base simulada; el sistema tiene que poder probarse contra un servidor real en un ambiente de TEST. La adecuación entre ambos se resuelve por configuración, sin recompilar.
- **Credenciales (P12).** En esta etapa es aceptable tratar usuario y password como parámetros de configuración más, y se confirmó para la instancia de prueba de concepto. Más adelante hace falta un guardado seguro de la password; el tratamiento definitivo (cifrado de la configuración, gestor de secretos) queda fuera de alcance.
- **Zona horaria de `TS` (P11).** La aplicación de origen guarda `TS` en hora local, GMT-3. Si hace falta, la zona se parametriza en la configuración.
- **Instancia (P12).** El gateway es una prueba de concepto temporal en el ambiente de TEST. La integración definitiva va a provenir de un servidor OPC UA que todavía no está disponible.
- **Consumidor (P12).** El cliente OPC UA es un PI System que corre en otra máquina. En la prueba de concepto no hay restricciones de firewall entre ambas.
- **Certificados del canal OPC UA (P12).** Se acepta la confianza en certificados autofirmados.

---

## Comportamiento ante casos borde

- **Tag del CSV ausente en la tabla (P9).** Se publica `Bad` en OPC UA y se registra un aviso en el log.
- **`V` o `Q` en `NULL`.** Sin definir. Se resuelve en el diseño.
- **`Q` negativo.** `smallint` tiene signo, así que un proveedor que use el byte alto para bits propios produciría un valor negativo. El mapeo debe contemplarlo.

---

## Puntos abiertos

### P13. Nombres reales en un repositorio público

Resuelto (05/10/2026). El dueño del dato considera que los nombres de base y tabla son bastante genéricos y no exponen nada, pero prefiere que se cambien si es posible. El repositorio usa desde entonces `PLANT_DB` para la base y `CURRENT_VALUES` para la tabla; el esquema (`dbo`) y las columnas (`TAG`, `TS`, `V`, `Q`) no cambian. Los nombres reales van solo en la configuración local de cada instalación (`appsettings.Local.json`, V2-26), nunca en el repositorio. El renombre es solo hacia adelante (V2-34).

### P14. Aplicación de origen muerta con la base viva

Abierta, no bloquea. Pregunta: ¿qué pasa si la aplicación de origen muere con la base viva: se congela `TS`, hay alguna señal de "estoy vivo"? Está declarada como deuda en las Limitaciones del README, y la mencionan también `docs/v2/verificacion.md` (caso b, medido una vez), `docs/operacion.md` (suscripción y cambios de solo timestamp) y `docs/v2/decisiones.md` (V2-32, lo que no cubre).

### P15. Repositorio público con los nombres reales en el historial

Resuelto (07/10/2026). Pregunta, tal como se mandó por WhatsApp: "En el código y la documentación actual los nombres de la base y la tabla ya son genéricos, pero en el historial viejo de cambios siguen apareciendo los nombres reales (no hay IPs, usuarios ni contraseñas, eso lo verifiqué). ¿Te parece bien que quede así público, o preferís que lo deje privado?". El dueño del dato respondió el 07/10/2026 a las 19:06: "publicalo". Alcance exacto, verificado con `git grep` (tag `v1.0.0`) y `git log -S` (rango): los commits de `4814f63` a `513779f`, ambos inclusive (en este último, en el diff y en el mensaje); el tag `v1.0.0` no los contiene.

---

## Valores no cerrados

Rangos y opciones donde la especificación admite más de una resolución válida:

- Los intervalos de polling y de reconexión son rangos de trabajo, no números fijos.
- El literal de la columna de origen puede ser `OPCDA` u `OPC_DA`, indistintamente.
- La posición de la columna de origen dentro del CSV es libre.
- La estructura de la cache no se impone: sirve la que ya usa el gateway.