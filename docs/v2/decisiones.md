# Decisiones de diseño — v2

Registro de decisiones de la fuente SQL. Continúa el criterio de `../decisiones.md`:
**los números son identificadores estables, no se renumeran ni se reordenan**, aunque
una decisión quede superada. Cuando eso pasa, la entrada vieja se marca y apunta a la
que la reemplaza.

Los identificadores llevan prefijo `V2-` para que no se confundan con los del registro
de la v1, que se referencian sin prefijo.

---

## V2-1. El motor no se elige: viene dado

SQL Server no salió de una comparación técnica. Es un requisito (R1): la tabla existe,
está en producción, y otra aplicación la escribe. El gateway se adapta a ella.

Esto descarta de entrada la comparación con PostgreSQL que hubiera correspondido en un
proyecto nuevo, y descarta también cualquier discusión sobre el esquema. La tabla no se
diseña: se replica tal cual está (P1).

Lo que sí queda como trabajo propio es todo lo que pasa del lado del gateway: cómo se
lee, cada cuánto, qué se hace con una fila que no cambia, y cómo se traduce al modelo
OPC UA sin inventar información que el dato no tiene.

## V2-2. Cliente: `Microsoft.Data.SqlClient` 7.0.2

El driver ADO.NET oficial de Microsoft para SQL Server. Tres cosas lo justifican y las
tres se verificaron antes de adoptarlo:

- **Licencia MIT.** No aprieta la licencia heredada, que en este repo la fija la
  dependencia más restrictiva.
- **Funciona en x86.** El host del gateway compila en 32 bits por el COM interop de OPC
  DA, y este driver lleva una parte nativa en Windows. La verificación está en
  `verificacion.md`: el proceso carga la biblioteca de `runtimes/win-x86/native`.
- **Es el camino soportado.** `System.Data.SqlClient` está deprecado y las
  características nuevas solo llegan a este paquete.

Se fijó 7.0.2 y no la última publicada, para trabajar sobre una versión con rodaje.

**Nota de peso:** arrastra 17 dependencias transitivas, varias de identidad de Azure que
este proyecto no usa. No es un problema de funcionamiento, pero es un argumento más para
que el driver SQL viva en su propio proyecto: así ese árbol queda contenido detrás del
borde y no se filtra al resto de la solución.

**Pendiente:** si se suma Dapper o se usa ADO.NET directo. Se decide al diseñar el
driver, y la carga de la prueba la tiene Dapper: con una sola consulta de cuatro columnas,
una dependencia más tiene que justificarse.

## V2-3. La base de desarrollo corre en un contenedor

SQL Server 2019 Developer Edition, imagen oficial `mcr.microsoft.com/mssql/server:2019-latest`,
en un contenedor con volumen persistente.

Por qué contenedor y no SQL Server Express instalado en Windows:

- **Un comando lo levanta.** Quien clone el repo reproduce el entorno sin instalador ni
  instructivo. Es la diferencia entre una línea en el README y una página.
- **La edición se parece al destino.** La imagen corre Developer, que en funcionalidad
  equivale a Enterprise. Express es la edición recortada, y el servidor real es 2019 o
  2025 completo (P8). Desarrollar contra Developer acerca el entorno de desarrollo al de
  destino.
- **Se borra sin dejar rastro.** No queda un servicio de Windows corriendo después del
  proyecto.

Por qué la etiqueta `2019` y no `2025`: el entorno de destino puede ser cualquiera de las
dos (P8), y desarrollar contra la más vieja evita apoyarse sin querer en algo que exista
solo en la nueva. Lo que corre en 2019 corre en 2025; al revés no está garantizado.

El costo es 2 GB de RAM mientras el contenedor está arriba. En una máquina de gama media
eso obliga a no tener otros motores de base corriendo en paralelo.

## V2-4. El cifrado de la conexión es un parámetro, no una constante

El driver exige cifrado por defecto (`Encrypt=True`) y valida quién emitió el certificado
del servidor. Contra un servidor que se autofirmó el suyo —el contenedor local, y
probablemente también el de TEST— esa validación falla y la conexión no se abre.

Se resuelve con `TrustServerCertificate`, que mantiene el tráfico cifrado pero omite la
validación del emisor. La decisión no es *usarlo*, sino **dónde vive el valor**: en la
configuración (R6), nunca fijo en el código.

El motivo es que la respuesta correcta cambia según el servidor. En una base local el
riesgo es nulo. Contra un servidor real, omitir la validación protege de que alguien
escuche el tráfico, pero no de que alguien se haga pasar por el servidor. Ahí la decisión
correcta puede ser distinta, y tiene que poder tomarse editando un JSON y no
recompilando (P10).

Los dos comportamientos se verificaron contra el contenedor, fallando y funcionando. Ver
`verificacion.md`.