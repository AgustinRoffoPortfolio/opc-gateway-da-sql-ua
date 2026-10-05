# Calidad observada en `CURRENT_VALUES` (R7)

R7 dice que la columna `Q` trae códigos de calidad OPC DA y que 192 es buena. Eso
no alcanzaba para construir el simulador: el enum de la v1 tiene 16 substatus y
no sabíamos cuáles aparecen de verdad en la tabla. La Fase 1 cerró con ese hueco.

## Cómo se resolvió

Consulta contra la tabla real, corrida por mi padre el 12/09/2026:

```sql
SELECT Q, COUNT(*) AS Filas
FROM [PLANT_DB].[dbo].[CURRENT_VALUES] WITH (NOLOCK)
GROUP BY Q
ORDER BY Filas DESC
```

> Los nombres de base y tabla de esta cita se reemplazaron por genéricos (`PLANT_DB`, `CURRENT_VALUES`) a pedido del dueño del dato (P13). Columnas, tipos y opciones son los originales.

## Resultado

| `Q` | Filas | % | Substatus | Calidad maestra |
|---|---|---|---|---|
| 192 | 33.017 | 80,4 % | `Good` | Good |
| 20 | 7.248 | 17,7 % | `BadLastKnown` | Bad |
| 24 | 743 | 1,8 % | `BadCommFailure` | Bad |
| 216 | 19 | 0,05 % | `GoodLocalOverride` | **Good** |
| 64 | 15 | 0,04 % | `Uncertain` | Uncertain |

Total: 41.042 filas.

## Qué se concluye

**El decodificador de la v1 cubre los cinco códigos sin tocar una línea.** Los
cinco valores ya están en `QualitySubstatus`, en `src/Gateway.Core/TagQuality.cs`,
con esos mismos nombres. El enum se escribió contra la especificación OPC DA y no
contra los casos que fueron apareciendo, y por eso aguanta datos de una aplicación
que no conocíamos. R7 queda cubierto sin código nuevo.

**No hay valores negativos.** Era la preocupación abierta: `Q` es `smallint` con
signo, y si algún driver escribiera bits de proveedor en el byte alto el número se
leería en negativo. No pasa. El mapeo no necesita contemplar ese caso.

**La calidad mala no es un caso raro.** Casi uno de cada cinco tags está en
`BadLastKnown` en cualquier momento dado. No es una excepción que haya que forzar
para probar: es el estado normal de una porción grande de la tabla. Eso importa
para el gateway, que va a publicar miles de tags en `Bad` de entrada y no puede
tratar eso como una anomalía.

No importa, en cambio, para el simulador: con diez tags no hay forma honesta de
representar un 0,04 %. El andamiaje garantiza que los cinco códigos sean
*alcanzables* a voluntad, que es lo que necesita el driver para probar su mapeo,
y no que aparezcan en la proporción observada.

**Ninguna fila tiene `Q` en `NULL`.** Los cinco códigos suman 41.042, que es el
total de la tabla, así que el `GROUP BY` habría mostrado una fila aparte si
existiera. La columna es nullable y el mapeo del driver contempla el caso, pero
en producción no ocurre. El simulador lo provoca con tags dedicados, declarados a
mano.

**El 216 es maestra `Good`.** `GoodLocalOverride` significa que un operador forzó
el valor a mano. El gateway lo va a publicar en UA como `Good`, que es lo correcto
según el estándar, pero es una sutileza para tener escrita antes de que aparezca
en una demo: un valor puesto a mano se ve igual de bueno que uno medido.

## Qué falta, cerrado

El simulador generaba solo 192 y 20. Desde el paso 4 de la Fase 3 produce los
cinco: 20 y 24 por estado del grupo de scan, 216 y 64 como condición propia de un
tag. Ver `docs/v2/simulador.md`.