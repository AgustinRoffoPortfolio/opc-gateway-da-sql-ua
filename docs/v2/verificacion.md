# Verificación — v2

Números medidos sobre el código de la v2. Los de la v1, y los de la línea base
corrida en el clon, están en `docs/verificacion.md` y no se mezclan con estos.

## Suite de tests — 30/09/2026, sobre `e6539a6`

- Sin credenciales de prueba: 169 total, 164 correctas, 5 omitidas.
- Con `GATEWAY_SQL_TEST_USER` y `GATEWAY_SQL_TEST_PASSWORD` fijadas y el
  contenedor arriba: **169 de 169**, cero omitidas.

Las 5 omitidas son los tests de integración contra SQL Server. Se omitían por
falta de credenciales, no por falta del contenedor: el motivo del skip lo dice y
`SqlIntegrationFactAttribute.cs` lo documenta. No era un bug (hallazgo B4.2).

## Fase 5, escenario 1 — base caída con DA sano · 30/09/2026

**Montaje.** `e6539a6`, `config/demo-mixto.tags.csv` (21 tags válidos, DA y SQL),
Matrikon con `demo-10.opcsim.xml` (10 aliases), contenedor `gateway-sql` y el
simulador. Configuración efectiva: `PollingIntervalSeconds` 30,
`ReconnectDelaySeconds` 15, `CommandTimeoutSeconds` 10.

**Método.** La base se cortó con `docker stop` y se levantó con `docker start`,
con la hora impresa antes y después de cada comando. Se restó contra el log del
gateway (resolución de 1 s) y se miró en UaExpert: 6 tags DA, 4 SQL y los nodos
de diagnóstico de `Sql`.

| | Marca | Medido | Cota |
|---|---|---|---|
| Detección | stop 16:52:13,8–15,4 → WRN 16:52:46 | ~31–32 s | 40 s (polling + timeout) |
| Recuperación | start 16:55:42,6–43,2 → conectado 16:55:56 | ~13–14 s | ~25 s (ciclo real de reintento) |
| Reintentos con la base caída | 8 fallos, 16:52:46 → 16:55:41 | ~25 s c/u | 15 s configurados + 10 s de timeout |
| Tags DA | toda la caída | `Good`, `SourceTimestamp` avanzando | — |

**El invariante 8 se cumple.** La caída de la base no afectó a DA. El diagnóstico
de `Sql` siguió la falla (`LinkState` en `Reconnecting`, `Disconnections` 1) y
volvió a `Connected` con `ReconnectAttempts` en 0. Todos sus nodos quedaron en
`Good` (principio 4).

**Sin pérdida y sin invención.** Al reconectar, el gateway levantó las filas que
el simulador escribió entre el último ciclo bueno (16:52:06) y el corte, con su
`TS` real (16:52:10–13).

**Observado, sin explicación confirmada.** La consulta en curso no falló al
instante: se colgó hasta el `CommandTimeout` (error -2), y cada reintento también
tardó unos 10 s. Con el contenedor apagado, una conexión nueva al puerto se
rechaza (`Test-NetConnection` → `False`), así que no es un proxy que siga
aceptando. Lo que se colgó parece ser la conexión que ya estaba abierta. El
timeout de 10 s es lo que acotó la espera.

**Hallazgos que salen de esta corrida.**
- Con la base caída, los tags SQL siguen publicándose en `Good` con valores
  congelados, aunque el gateway sabe que perdió el vínculo. Es el hueco que
  V2-23 dejó para la Fase 5. Pendiente de decisión.
- El simulador muere con excepción no manejada cuando cae la base: no reintenta.
- El aviso del tag ausente se repite tras cada reconexión (variante de B4.3).
- `Connections` cuenta la primera conexión. La página la muestra como
  "reconexiones" (B3).

**No medido.** La base colgada con `docker pause` (el camino del timeout ya quedó
ejercitado) y la aplicación de origen muerta con la base viva (V2-23).