using System.Globalization;

namespace Gateway.Core;

/// Parsea el CSV de tags fila por fila, sin frenar el archivo entero por una
/// fila mal formada: cada fila que no parsea se acumula como error en vez de
/// tirar excepcion. La validacion que necesita ver el CSV completo (como
/// nombres TAG_NAME_OPC_UA duplicados) es responsabilidad de TagValidator,
/// no de esta clase - esta solo lee filas, no las compara entre si.
internal static class CsvTagLoader
{
    private const char Separator = ';';
    private const char CommentPrefix = '#';

    internal static CsvParseResult Parse(string path)
    {
        var rows = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(row => row.Line.Length > 0 && row.Line[0] != CommentPrefix)
            .ToArray();

        var parsedRows = new List<ParsedTagRow>();
        var errors = new List<TagLoadError>();

        if (rows.Length == 0)
        {
            return new CsvParseResult(parsedRows, errors);
        }

        // La primera fila no comentada es la cabecera. Con el CSV a 12
        // columnas (V2-5) dejo de ser decorativa: de ella sale que columna es
        // cual, y sin una cabecera valida ninguna fila se puede interpretar
        // con confianza.
        var (headerLine, headerLineNumber) = rows[0];
        if (!CsvHeader.TryParse(headerLine.Split(Separator), headerLineNumber, path, out var columnIndex, out var headerErrors))
        {
            errors.AddRange(headerErrors);
            return new CsvParseResult(parsedRows, errors);
        }

        var expectedColumns = columnIndex.Count;

        for (var i = 1; i < rows.Length; i++)
        {
            var (line, lineNumber) = rows[i];
            var fields = line.Split(Separator);
            if (fields.Length != expectedColumns)
            {
                errors.Add(new TagLoadError(lineNumber, "",
                    $"'{path}' linea {lineNumber}: tiene {fields.Length} columnas, se esperaban {expectedColumns} ('{line}')."));
                continue;
            }

            var opcUaName = fields[columnIndex["TAG_NAME_OPC_UA"]];
            try
            {
                var tag = new TagDefinition(
                    OpcUaName: opcUaName,
                    Source: ParseSource(fields[columnIndex["SOURCE"]]),
                    SourceTag: fields[columnIndex["SOURCE_TAG"]],
                    DataType: ParseEnum<TagDataType>(fields[columnIndex["DATA_TYPE"]], "DATA_TYPE"),
                    Multiplier: ParseDouble(fields[columnIndex["MULTIPLICADOR"]], "MULTIPLICADOR"),
                    Offset: ParseDouble(fields[columnIndex["OFFSET"]], "OFFSET"),
                    EngineeringUnit: fields[columnIndex["EU"]],
                    ScanRateMs: int.Parse(fields[columnIndex["SCAN_RATE_MS"]], CultureInfo.InvariantCulture),
                    Deadband: ParseDouble(fields[columnIndex["DEADBAND"]], "DEADBAND"),
                    AccessLevel: ParseEnum<TagAccessLevel>(fields[columnIndex["ACCESS_LEVEL"]], "ACCESS_LEVEL"),
                    Description: fields[columnIndex["DESCRIPTION"]],
                    Enabled: bool.Parse(fields[columnIndex["ENABLED"]]));
                parsedRows.Add(new ParsedTagRow(lineNumber, tag));
            }
            catch (Exception ex)
            {
                errors.Add(new TagLoadError(lineNumber, opcUaName,
                    $"'{path}' linea {lineNumber}, tag '{opcUaName}': {ex.Message}"));
            }
        }

        return new CsvParseResult(parsedRows, errors);
    }

    /// Parsea un decimal en cultura invariante SIN permitir separador de miles.
    /// El default de double.Parse incluye NumberStyles.AllowThousands, con lo
    /// que "1,5" (un Excel en es-AR que piso el punto por coma) se leia como
    /// 15 en silencio: el tag cargaba bien y quedaba escalado 10 veces mal.
    private static double ParseDouble(string field, string columnName)
    {
        if (!double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException(
                $"{columnName} '{field}' no es un decimal valido (se espera PUNTO como separador decimal, no coma).");
        }

        return value;
    }

    /// Parsea un enum y, si falla, lista los valores aceptados. El mensaje que
    /// da Enum.Parse por defecto ("Requested value 'X' was not found") no dice
    /// que se esperaba, con lo que el operador tiene que ir a leer el codigo
    /// para corregir una fila del CSV.
    private static TEnum ParseEnum<TEnum>(string field, string columnName) where TEnum : struct, Enum
    {
        if (!Enum.TryParse<TEnum>(field, ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            throw new FormatException(
                $"{columnName} '{field}' no es valido (valores aceptados: {string.Join(", ", Enum.GetNames<TEnum>())}).");
        }

        return value;
    }

    /// SOURCE no puede resolverse con ParseEnum: Enum.TryParse con ignoreCase
    /// resuelve "OPCDA" contra el miembro OpcDa, pero no "OPC_DA", que V2-5
    /// acepta igual. Se normaliza sacando el guion bajo y los espacios de los
    /// costados antes de resolver contra el enum; no hay default, un SOURCE
    /// vacio cae por el mismo camino que uno desconocido.
    private static TagSource ParseSource(string field)
    {
        var normalized = field.Trim().Replace("_", "");
        if (!Enum.TryParse<TagSource>(normalized, ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            // A diferencia de ParseEnum, la lista no sale solo de
            // Enum.GetNames: "OPC_DA" es un alias aceptado que no es nombre
            // de ningun miembro del enum, asi que se agrega a mano. El resto
            // sale del enum para que, si algun dia suma una fuente, el
            // mensaje no mienta.
            var aceptados = Enum.GetNames<TagSource>()
                .Select(n => n.ToUpperInvariant())
                .Append("OPC_DA");
            throw new FormatException(
                $"SOURCE '{field}' no es valido (valores aceptados: {string.Join(", ", aceptados)}).");
        }

        return value;
    }
}
