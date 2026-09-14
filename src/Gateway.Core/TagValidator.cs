namespace Gateway.Core;

/// Punto de entrada publico para cargar el CSV de tags: parsea con
/// CsvTagLoader y aplica las reglas que necesitan ver mas que una columna
/// aislada. La unicidad de TAG_NAME_OPC_UA necesita ver el archivo completo;
/// el cruce SOURCE/DATA_TYPE de V2-24 y el aviso de SCAN_RATE_MS/DEADBAND de
/// V2-22 solo necesitan la fila, pero cruzan columnas entre si y por eso
/// tampoco son responsabilidad de CsvTagLoader (que parsea cada columna sola).
public static class TagValidator
{
    // V2-24: en una fila SQL el driver nunca va a entregar una cadena (V es
    // real, P1/P2), asi que un tag SQL declarado String no se actualizaria
    // jamas y quedaria muerto en silencio. Es mas barato que falle la carga.
    private static readonly TagDataType[] SqlDataTypesAceptados =
        { TagDataType.Float, TagDataType.Boolean, TagDataType.Int32 };

    public static TagLoadResult LoadAndValidate(string relativePath)
    {
        var path = ConfigPathResolver.Resolve(relativePath);
        var parsed = CsvTagLoader.Parse(path);

        var errors = new List<TagLoadError>(parsed.Errors);
        var warnings = new List<string>();
        var tags = new List<TagDefinition>();
        // Gana la primera aparicion de cada nombre: comportamiento
        // deterministico y facil de explicar ("el primero que aparece en
        // el archivo"), en vez de rechazar ambas filas o quedarse con la
        // ultima.
        var seenNames = new HashSet<string>();

        foreach (var row in parsed.Rows)
        {
            if (!seenNames.Add(row.Tag.OpcUaName))
            {
                errors.Add(new TagLoadError(row.LineNumber, row.Tag.OpcUaName,
                    $"linea {row.LineNumber}: '{row.Tag.OpcUaName}' ya aparecio antes en el archivo, esta fila queda fuera de servicio."));
                continue;
            }

            if (row.Tag.Source == TagSource.Sql && !SqlDataTypesAceptados.Contains(row.Tag.DataType))
            {
                errors.Add(new TagLoadError(row.LineNumber, row.Tag.OpcUaName,
                    $"linea {row.LineNumber}: '{row.Tag.OpcUaName}' es SOURCE=SQL con DATA_TYPE '{row.Tag.DataType}', no aceptado para esa fuente " +
                    $"(valores aceptados: {string.Join(", ", SqlDataTypesAceptados)})."));
                continue;
            }

            // V2-22: el ritmo de una fila SQL lo decide la aplicacion de
            // origen, no el gateway. Un valor distinto de cero es casi
            // siempre una fila DA copiada y no corregida, pero no afecta el
            // comportamiento (el driver SQL los ignora), asi que es aviso y
            // no error, a diferencia del DATA_TYPE de arriba.
            if (row.Tag.Source == TagSource.Sql && (row.Tag.ScanRateMs != 0 || row.Tag.Deadband != 0))
            {
                warnings.Add(
                    $"linea {row.LineNumber}: '{row.Tag.OpcUaName}' es SOURCE=SQL con SCAN_RATE_MS={row.Tag.ScanRateMs} y DEADBAND={row.Tag.Deadband}; " +
                    "el driver SQL los ignora, revisar si se copiaron de una fila DA por error.");
            }

            tags.Add(row.Tag);
        }

        return new TagLoadResult(tags, errors, warnings);
    }
}