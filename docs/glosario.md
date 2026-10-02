# Glosario

Términos del dominio que aparecen en el código y en la documentación. El proyecto
cruza dos estándares con vocabulario distinto para las mismas ideas, así que
conviene tener las equivalencias a mano.

## Los dos estándares

**OPC DA (Data Access)** — El estándar OPC viejo, de los años 90. Corre sobre
COM/DCOM y existe solo en Windows. Por cada punto de dato entrega tres cosas:
valor, calidad y timestamp. Sigue vivo en plantas que no migraron.

**OPC UA (Unified Architecture)** — El reemplazo moderno: multiplataforma, con
modelo de información propio, seguridad integrada y transporte propio. No es una
versión nueva de DA, es un estándar distinto con otro vocabulario.

## Del lado DA

**Item** — Un punto de dato individual en el servidor DA. Es el equivalente de una
variable o nodo en UA.

**ItemID** — El identificador del item dentro del servidor DA, por ejemplo
`PLANTA_01.MEDICION.PRESION_ENTRADA`. Es lo que el gateway usa para pedir el dato,
y no tiene por qué coincidir con el nombre que se expone por UA: el CSV mapea uno
al otro.

**Group** — Un conjunto de items que se leen juntos con una misma frecuencia. Es
la unidad de suscripción del lado DA. La analogía útil es un cliente Modbus que
agrupa registros contiguos en una sola lectura en vez de pedirlos de a uno:
agrupar por frecuencia de scan es lo que evita machacar al servidor legado con
miles de pedidos individuales.

**Calidad DA** — Un campo con estados (`Good`, `Uncertain`, `Bad`) y subcódigos
que precisan la causa (`Bad_NotConnected`, `Bad_OutOfService`,
`Bad_LastKnownValue`). **No es un booleano.** Es la diferencia con Modbus, donde
la calidad se reduce a si el dispositivo contestó o no.

## Del lado UA

**StatusCode** — El equivalente UA de la calidad DA, con otro vocabulario y otros
códigos. Como los dos conjuntos no coinciden uno a uno, hace falta una tabla de
traducción explícita, documentada en [`calidad-da-ua.md`](calidad-da-ua.md).

**SourceTimestamp** — Cuándo se originó el dato en el campo.

**ServerTimestamp** — Cuándo lo registró el servidor que lo está publicando.

En un servidor común los dos suelen coincidir y confundirlos no molesta. **En un
gateway no son lo mismo**: entre que el dato se originó en el campo y que el
gateway lo publica hay una lectura DA, una cache y un ciclo de publicación de por
medio. Pisar el `SourceTimestamp` con la hora de publicación corrompe cualquier
dato histórico que se guarde aguas abajo.

**MonitoredItem / Subscription** — El mecanismo por el que un cliente UA pide que
le notifiquen los cambios de un nodo, con su intervalo de muestreo y sus filtros,
en vez de preguntar en un bucle.

**NodeId** — El identificador único de un nodo en el address space. En este
proyecto es el nombre completo del tag, con puntos.

**AccessLevel** — Qué se puede hacer con un nodo: leer, escribir, o ambas. El
gateway es de solo lectura.

## Del lado Windows

**COM** — La tecnología de componentes de Windows sobre la que corre OPC DA.
Permite que un programa use objetos que viven en otro proceso como si fueran
propios.

**DCOM** — La versión de COM que funciona a través de la red. Es la parte
históricamente problemática de OPC DA (configuración de permisos, firewalls,
timeouts largos), y acá se evita por diseño: el gateway corre en la misma máquina
que el servidor DA.

**ProgID / CLSID** — Las dos formas de identificar un componente COM en el
registro de Windows. El ProgID es legible (`Matrikon.OPC.Simulation.1`); el CLSID
es el identificador único real, un GUID. El ProgID se resuelve a CLSID contra el
registro.

**Bitness (x86 / x64)** — Si un proceso es de 32 o de 64 bits. Importa porque un
componente COM in-process de 32 bits no se puede cargar en un proceso de 64 bits.
Es la razón por la que el ejecutable del gateway se compila como x86.

## Del lado SQL Server (v2)

**SQL Server** — El motor de base de datos de Microsoft donde vive la tabla que
lee la segunda fuente del gateway. No se eligió: viene dado, porque la tabla ya
existe y otra aplicación la escribe (V2-1). En desarrollo corre en un contenedor.

**T-SQL (Transact-SQL)** — El dialecto de SQL de SQL Server. Lo que se ve en la
consulta del gateway y no es SQL estándar, como los corchetes alrededor de los
nombres (`[dbo].[Tabla]`) o la pista `WITH (NOLOCK)`, es T-SQL.

**NOLOCK** — Una pista de la consulta que le dice a SQL Server que lea sin pedir
bloqueos, para no frenar a la aplicación que escribe la tabla al mismo tiempo.
Equivale a leer en modo *read uncommitted*: a cambio, la lectura puede traer un
valor que todavía no se confirmó, o saltear o repetir una fila que se está
moviendo. La consulta del gateway la usa porque lo pide el requisito R3.

**Polling** — Preguntar cada cierto tiempo en vez de esperar a que avisen. El
gateway consulta la tabla entera cada `Sql:PollingIntervalSeconds` (30 s por
defecto): SQL Server no le avisa cuando una fila cambia.

**Connection string (cadena de conexión)** — El texto con todo lo necesario para
conectarse a una base: servidor, puerto, base, usuario, password, cifrado,
timeouts. El gateway no la recibe armada: recibe los parámetros sueltos en
`Sql:*` y la arma con `SqlConnectionStringBuilder`, para que una password con `;`
no rompa la cadena.

**Pool de conexiones** — Un mecanismo de la biblioteca cliente que, al "cerrar"
una conexión, la guarda abierta para reusarla en vez de cortarla. Sirve cuando
muchos consumidores abren y cierran conexiones todo el tiempo. El gateway lo
deshabilita (V2-20): tiene un solo consumidor, y con el pool activo el estado que
reporta el diagnóstico podría no coincidir con el de la conexión real.

**Override local de configuración** — Un archivo que se lee después de
`appsettings.json` y pisa sus claves una por una. En este gateway es
`appsettings.Local.json`, junto al ejecutable: ahí van usuario y password de SQL
y, en la POC, la URL del endpoint expuesto a la red. Git lo ignora, así que nada
de lo que tiene se versiona (V2-26). Las variables de entorno (`Sql__Password`,
con doble guion bajo) se leen después y le ganan a los dos.

## Del dominio de proceso

**EU (Engineering Units)** — La unidad de ingeniería con la que se expone el valor
al cliente: bar, kg/cm², °C. El servidor DA suele entregar un número crudo, y el
gateway lo convierte a la unidad que corresponde.

**Deadband** — Umbral de cambio mínimo para considerar que un valor efectivamente
cambió. Sirve para no publicar el ruido de la última cifra decimal de un sensor.
En este gateway la columna `DEADBAND` del CSV se lee pero no filtra nada, en
ninguna de las dos fuentes (V2-22).

**Scan rate** — Cada cuánto se lee un grupo de items del servidor DA. Para un tag
SQL el ritmo lo decide la aplicación de origen, no el gateway, y `SCAN_RATE_MS`
se ignora (V2-22).

**Modbus slave** — El rol de servidor en Modbus, que responde a los pedidos de un
master. Se menciona porque un puente DA→Modbus es la alternativa clásica a un
gateway UA, y puede usarse como referencia independiente para validar que los
valores que publica el gateway son los correctos.