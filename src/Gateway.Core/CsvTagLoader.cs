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
    private const int ExpectedColumns = 11;

    internal static CsvParseResult Parse(string path)
    {
        var rows = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(row => row.Line.Length > 0 && row.Line[0] != CommentPrefix)
            .ToArray();

        var parsedRows = new List<ParsedTagRow>();
        var errors = new List<TagLoadError>();

        // La primera fila no comentada es la cabecera, se saltea.
        for (var i = 1; i < rows.Length; i++)
        {
            var (line, lineNumber) = rows[i];
            var fields = line.Split(Separator);
            if (fields.Length != ExpectedColumns)
            {
                errors.Add(new TagLoadError(lineNumber, "",
                    $"'{path}' linea {lineNumber}: tiene {fields.Length} columnas, se esperaban {ExpectedColumns} ('{line}')."));
                continue;
            }

            var opcUaName = fields[0];
            try
            {
                var tag = new TagDefinition(
                    OpcUaName: opcUaName,
                    // PROVISORIO (Fase 3): el CSV todavia tiene 11 columnas y no
                    // declara SOURCE. Hasta que la Fase 4 implemente V2-5 completo
                    // -12 columnas, lectura por cabecera y validacion de SOURCE-
                    // todo lo que entra por CSV es OPC DA, que es lo unico que
                    // habia en la v1. No se puede cargar un tag SQL asi.
                    Source: TagSource.OpcDa,
                    SourceTag: fields[1],
                    DataType: ParseEnum<TagDataType>(fields[2], "DATA_TYPE"),
                    Multiplier: ParseDouble(fields[3], "MULTIPLICADOR"),
                    Offset: ParseDouble(fields[4], "OFFSET"),
                    EngineeringUnit: fields[5],
                    ScanRateMs: int.Parse(fields[6], CultureInfo.InvariantCulture),
                    Deadband: ParseDouble(fields[7], "DEADBAND"),
                    AccessLevel: ParseEnum<TagAccessLevel>(fields[8], "ACCESS_LEVEL"),
                    Description: fields[9],
                    Enabled: bool.Parse(fields[10]));
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
}