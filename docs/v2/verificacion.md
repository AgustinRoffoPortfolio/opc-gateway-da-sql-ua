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

## Fase 5, V2-32 — tags SQL a `Uncertain` con el vínculo caído · 02/10/2026

**Montaje.** `270b7be` (gateway) y el simulador de `aaad7b6`/`213fe4a`, mismo CSV
(`config/demo-mixto.tags.csv`, 21 tags válidos), Matrikon con `demo-10.opcsim.xml`
y la misma configuración SQL: polling 30 s, reintento 15 s, `CommandTimeout` 10 s.
Arranque con lo esperado: 21 válidos, dos drivers, un WRN de 1 tag ausente
(`PRUEBA_TAG_QUE_NO_EXISTE`) y uno de anomalías con 2 tags (`PRUEBA_NULO_CALIDAD`,
`PRUEBA_NULO_VALOR`).

**Método.** `docker stop` / `docker start` con `Get-Date -Format HH:mm:ss.fff` antes
y después de cada comando. Calidades desde `/api/diagnostics/tags?onlyDegraded=false`
(la misma cache que publica UA), sondeado cada 2 s, registrando cada cambio de
substatus, valor o `SourceTimestamp` de los tags SQL y el conteo de DA en `Good`.
Log del gateway con resolución de 1 s. Tres cortes; en el segundo el simulador
estaba detenido (caso b).

> **Salvedad agregada después.** Esta corrida se hizo con la CPU de la máquina al
> 99 % por otra carga ajena al gateway. Los tiempos de **recuperación** de esta
> tabla (27–51 s) quedan como registro de ese caso, no como representativos: la
> remedición con la máquina libre está más abajo, en "Remedición de la
> recuperación". La marca tag por tag, el caso b y la detección no dependen de eso.

| | Corte 1 | Corte 2 (simulador detenido) | Corte 3 |
|---|---|---|---|
| `docker stop` | 15:48:39,6–42,4 | 15:52:03,3–05,0 | 15:53:43,3 |
| Consulta que falla (fase del polling) | 15:48:59 | 15:52:05 | 15:53:45 |
| WRN en el log | 15:49:09 "Se corto" | 15:52:15 "Sigue caido" | 15:53:55 "Sigue caido" |
| Detección desde el stop | ~27–30 s | ~10–12 s | ~12 s |
| Marca vista en el sondeo | 15:49:10,7 | 15:52:16,1 | 15:53:57,3 |
| `docker start` | 15:49:42,4–44,0 | 15:52:17,1–18,6 | 15:53:54,6–55,5 |
| Gateway conectado | 15:50:35 | 15:52:45 | 15:54:25 |
| Recuperación desde el start | ~51 s | ~27 s | ~30 s |

**La marca, tag por tag (cortes 1 y 2).** En el mismo segundo del WRN, los 7 tags
SQL en `Good` pasaron a `UncertainLastUsableValue`, **incluido `PRESION_SALIDA`**, que
venía en `GoodLocalOverride`. Valor y `SourceTimestamp` idénticos al último ciclo
bueno (p. ej. `PRESION_ENTRADA` 13,29922 @ 18:48:29.157Z antes y después). No
cambiaron `DENSIDAD` (`Uncertain` por `Q`), `PRUEBA.NULO_VALOR` y `PRUEBA.NULO_CALIDAD`
(ya en `UncertainLastUsableValue`, V2-16) ni `PRUEBA.TAG_AUSENTE`
(`BadConfigurationError`). Al reconectar, el primer ciclo devolvió cada tag a la
calidad de su `Q` (`Good`, `GoodLocalOverride`). Los 10 tags DA estuvieron en `Good`
con `SourceTimestamp` avanzando en todos los sondeos de la corrida, sin excepción.

> **Nota posterior (V2-35).** Al momento de esta medición, `PRUEBA.NULO_CALIDAD` salía
> en `UncertainLastUsableValue`. Desde V2-35, `Q` en `NULL` se publica como `Uncertain`
> sin substatus (`0x40000000`); `PRUEBA.NULO_VALOR` sigue en `UncertainLastUsableValue`.
> La marca de V2-32 tampoco lo cambia ahora: solo degrada tags en `Good`. Con
> `demo-mixto` y el vínculo sano, la tarjeta SQL de la pestaña Operador pasa de
> `Indeterminate` ("2 tags nunca respondieron y 1 dejó de responder.") a
> `LikelyCsvMismatch`: "2 de 11 tags nunca entregaron un dato desde que arrancó el
> gateway." Los dos son `PRUEBA.TAG_AUSENTE` y `PRUEBA.NULO_VALOR`; la indicación de
> revisar el CSV es cierta para el primero y no para el segundo (pendiente de V2-35).

**Segundo corte.** El log dice "Sigue caido" y no "Se corto" (el bug conocido de
`faultLogged`, no se arregla), y la marca se aplicó igual. Es lo que V2-32 previó al
no atarla a esa bandera.

**Caso b (P14), medido una vez.** Simulador detenido a las 15:51:50,1; base
reiniciada (corte 2). Al reconectar, los tags SQL volvieron a `Good` con valores y
`SourceTimestamp` congelados en la última escritura del simulador (18:51:48.643Z) y
siguieron así en el ciclo siguiente (15:53:15), con `LastUpdateUtc` avanzando. El
gateway no distingue "la aplicación de origen dejó de escribir" de "el valor no
cambió": es el hueco que V2-32 declara no cubrir. Al relanzar el simulador
(15:53:30) los valores volvieron a moverse.

**Desvíos contra la predicción.**
- **Detección.** Predicho ~30–40 s; medido entre ~10 y ~30 s. La detección es la
  próxima consulta más el `CommandTimeout` (la conexión abierta se cuelga hasta los
  10 s, igual que en el escenario 1), así que depende de en qué punto del ciclo de
  30 s cae el stop: va de ~10 s a 40 s. La cota de V2-32 (40 s) se sostiene.
- **Recuperación.** Predicho ~13–14 s (lo del escenario 1); medido 27–51 s. Lo que
  manda es cuánto tarda SQL Server en aceptar logins después de `docker start`: los
  reintentos intermedios fallan con "error durante el inicio de sesión previo del
  protocolo de enlace", que es el contenedor arriba con el motor todavía arrancando.
  Al tiempo de arranque se le suma hasta un ciclo de reintento (15 s). El 13–14 s
  del escenario 1 fue un arranque rápido, no la regla. *Corregido por la
  remedición:* con la máquina libre la recuperación dio ~15–16 s en los dos cortes.
  El mecanismo (arranque del motor más hasta un ciclo de reintento) se confirma; lo
  que alargaba el arranque del motor a 27–51 s era la CPU saturada.
- **Caso b durante el corte 1, sin querer.** El simulador de `aaad7b6` tardó ~37 s
  más que el gateway en reconectar (15:51:12 contra 15:50:35) porque usaba pooling:
  SqlClient devolvía el error cacheado del "blocking period". Durante ese tramo el
  gateway publicó `Good` con valores congelados, lo mismo que el caso b. Corregido en
  `213fe4a` (sin pooling, como el gateway, V2-20); en el corte 3 el simulador
  reconectó a las 15:54:20, antes que el gateway.

## Remedición de la recuperación, con la máquina libre · 02/10/2026

**Por qué.** La corrida de V2-32 dio una recuperación de 27–51 s contra los 13–14 s
del escenario 1, y se hizo con la CPU al 99 % por otra carga. Se repitieron dos
cortes sin esa carga para saber cuál de los dos números es el representativo.

**Montaje.** `f455787`, build Debug, `config/demo-mixto.tags.csv` (21 tags válidos),
Matrikon con `demo-10.opcsim.xml` (10 tags DA en `Good` antes de empezar), contenedor
`gateway-sql` y el simulador sin pooling (`213fe4a`). Misma configuración SQL:
polling 30 s, reintento 15 s, `CommandTimeout` 10 s. Arranque con lo esperado: 21
válidos, dos drivers, los dos WRN conocidos (1 tag ausente, 2 con anomalías).

**Carga de la máquina.** `LoadPercentage` de `Win32_Processor`, muestreado a mano:
23–38 % antes de empezar, 30 % después del primer `docker stop`, 67 % justo después
del primer `docker start` (el arranque del propio contenedor) y 26–29 % en el
segundo corte. Lejos del 99 % de la corrida anterior, pero no una máquina ociosa:
había otras aplicaciones de escritorio abiertas.

**Método.** `docker stop` / `docker start` con `Get-Date -Format HH:mm:ss.fff` antes
y después. Sondeo de `/api/diagnostics` cada 1 s, registrando cada cambio de
`LinkState` de `Sql`, los conteos de tags SQL por calidad y los tags DA en `Good`.
Log del gateway con resolución de 1 s. El simulador reintenta cada 5 s, así que su
log acota cuándo el motor empezó a aceptar logins, independiente del ciclo de
reintento del gateway.

| | Corte 1 | Corte 2 |
|---|---|---|
| `docker stop` | 16:28:16,3–17,6 | 16:29:31,2–31,9 |
| WRN en el log | 16:28:29 "Se corto" | 16:29:45 "Sigue caido" |
| Marca vista en el sondeo | 16:28:30,7 | 16:29:46,5 |
| **Detección desde el stop** | **~12–13 s** | **~13–14 s** |
| `docker start` | 16:28:49,4–50,5 | 16:29:59,3–30:00,1 |
| Motor acepta logins (log del simulador) | entre 16:29:00 y 16:29:05 | entre 16:30:08 y 16:30:13 |
| Gateway conectado (log) | 16:29:05 | 16:30:15 |
| Sondeo en `Connected` | 16:29:07,1 | 16:30:16,8 |
| **Recuperación desde el start** | **~15–16 s** | **~15–16 s** |

**Lectura.** Con la máquina libre el motor tardó unos 9–15 s en aceptar logins
después de `docker start`, y el gateway se conectó en el primer reintento
posterior: en los dos cortes el reintento anterior cayó con el contenedor recién
levantado y falló con el error del "inicio de sesión previo del protocolo de
enlace", y el siguiente, 15 s después, entró. La recuperación es el arranque del
motor más lo que falte para el próximo reintento, así que va de ~10 s a ~30 s según
la fase; los dos cortes dieron ~15–16 s, cerca de los 13–14 s del escenario 1. **Estos
son los números representativos**; los 27–51 s de la corrida de V2-32 fueron el
mismo mecanismo con el arranque del motor alargado por la CPU saturada.

La detección cayó en ~12–14 s en los dos cortes: la consulta siguiente al stop
llegó pocos segundos después y se colgó hasta el `CommandTimeout` (en el corte 2,
error "Se agotó el tiempo de espera de ejecución"), igual que en las corridas
anteriores. Sigue dentro de la cota de 40 s de V2-32.

**Lo demás, igual que antes.** En la marca, los 7 tags SQL en `Good` pasaron a
`Uncertain` (conteos: `Good` 7 → 0, `Uncertain` 3 → 10, `Bad` 1 sin cambio) y
volvieron a 7/3/1 en el primer ciclo después de reconectar. Los 10 tags DA
estuvieron en `Good` en todos los sondeos de las dos caídas: el invariante 8 se
sigue cumpliendo. El segundo corte volvió a loguear "Sigue caido" y no "Se corto"
(el bug conocido de `faultLogged`), y la marca se aplicó igual.

**No medido en esta corrida.** El caso b (aplicación de origen muerta con la base
viva) y la marca tag por tag con valores y `SourceTimestamp`: se midieron una vez
en la corrida de V2-32 y no dependen de la carga de la CPU.
