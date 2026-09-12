# Calidad observada en `CURR_DATA` (R7)

R7 dice que la columna `Q` trae códigos de calidad OPC DA y que 192 es buena. Eso
no alcanzaba para construir el simulador: el enum de la v1 tiene 16 substatus y
no sabíamos cuáles aparecen de verdad en la tabla. La Fase 1 cerró con ese hueco.

## Cómo se resolvió

Consulta contra la tabla real, corrida por mi padre el 12/09/2026:

```sql
SELECT Q, COUNT(*) AS Filas
FROM [SCADA_HST].[dbo].[CURR_DATA] WITH (NOLOCK)
GROUP BY Q
ORDER BY Filas DESC
```

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
para probar: es el estado normal de una porción grande de la tabla. El simulador
debería reflejar esa proporción.

**El 216 es maestra `Good`.** `GoodLocalOverride` significa que un operador forzó
el valor a mano. El gateway lo va a publicar en UA como `Good`, que es lo correcto
según el estándar, pero es una sutileza para tener escrita antes de que aparezca
en una demo: un valor puesto a mano se ve igual de bueno que uno medido.

## Qué falta

El simulador genera solo 192 y 20. Los otros tres códigos están en su catálogo
(`commFailureQuality`, `localOverrideQuality`, `uncertainQuality`) pero el loop
todavía no los usa. Cubrirlos es trabajo de la Fase 3, donde se testea el mapeo
de calidad del driver.