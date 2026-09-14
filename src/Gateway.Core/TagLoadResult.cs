namespace Gateway.Core;

/// Un problema encontrado leyendo o validando una fila del CSV de tags.
/// LineNumber es la linea real del archivo (cuenta cabecera y comentarios),
/// para poder ubicarla con Ctrl+F. OpcUaName puede venir vacio si el error
/// impidio incluso leer esa columna.
public sealed record TagLoadError(int LineNumber, string OpcUaName, string Message);

/// Resultado final de cargar y validar el CSV de tags. Un CSV con errores
/// no impide arrancar: los tags validos se sirven igual (carga parcial),
/// Errors queda para loguear o mostrar en un reporte. Warnings son avisos que
/// no sacan la fila de servicio (V2-22): mismo destino que Errors, logueo o
/// reporte, pero sin restar de Tags.
public sealed record TagLoadResult(
    IReadOnlyList<TagDefinition> Tags,
    IReadOnlyList<TagLoadError> Errors,
    IReadOnlyList<string> Warnings);

/// Una fila que parseo bien, junto con el numero de linea de origen. Uso
/// interno: sirve de puente entre CsvTagLoader (lee la fila) y TagValidator
/// (compara filas entre si, por ejemplo para detectar TAG_NAME_OPC_UA
/// duplicados). El resto del gateway nunca ve este tipo, solo TagDefinition.
internal sealed record ParsedTagRow(int LineNumber, TagDefinition Tag);

/// Salida cruda de CsvTagLoader: filas que parsearon bien mas los errores
/// de las que no. Todavia no paso por las reglas que necesitan ver el CSV
/// entero (como unicidad de nombres) - eso lo hace TagValidator despues.
internal sealed record CsvParseResult(IReadOnlyList<ParsedTagRow> Rows, IReadOnlyList<TagLoadError> Errors);