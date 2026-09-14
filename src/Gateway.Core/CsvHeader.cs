namespace Gateway.Core;

/// Cabecera del CSV de tags (V2-5): 12 columnas identificadas por nombre, no
/// por posicion. Aislado de CsvTagLoader porque la deteccion de la cabecera
/// de la v1 mas las tres formas de cabecera invalida (falta, sobra, repetida)
/// no entran comodas al lado del parseo de filas sin que ese archivo deje de
/// poder revisarse de una sentada.
internal static class CsvHeader
{
    // Orden de referencia para armar mensajes y para los CSV que se escriben
    // en el repositorio. El parseo real es por nombre: el archivo puede
    // traer estas mismas columnas en otro orden y carga igual.
    private static readonly string[] Columns =
    {
        "TAG_NAME_OPC_UA", "SOURCE", "SOURCE_TAG", "DATA_TYPE", "MULTIPLICADOR",
        "OFFSET", "EU", "SCAN_RATE_MS", "DEADBAND", "ACCESS_LEVEL",
        "DESCRIPTION", "ENABLED"
    };

    private const string LegacyOpcDaColumn = "TAG_NAME_OPC_DA";
    private const string SourceColumn = "SOURCE";

    /// Interpreta la primera fila no comentada como cabecera. Si devuelve
    /// false, columnIndex queda vacio y errors tiene al menos un problema:
    /// sin una cabecera valida no hay forma de mapear las filas, asi que
    /// CsvTagLoader no intenta parsear ninguna.
    internal static bool TryParse(string[] fields, int lineNumber, string path,
        out IReadOnlyDictionary<string, int> columnIndex, out IReadOnlyList<TagLoadError> errors)
    {
        // Un CSV editado a mano o exportado de Excel llega con espacios sueltos
        // alrededor de los nombres. Sin esto, "SOURCE " se reporta como columna
        // desconocida y el mensaje lista SOURCE entre las esperadas: se lee
        // como un error del gateway, no del archivo.
        fields = fields.Select(f => f.Trim()).ToArray();

        var problems = new List<TagLoadError>();

        // Cabecera de la v1: tiene el nombre viejo de la columna de origen y
        // no tiene la nueva columna obligatoria. Un mensaje que dice
        // exactamente que hacer, en vez de que el operador reciba "falta
        // SOURCE" y "TAG_NAME_OPC_DA desconocida" por separado y tenga que
        // atar cabos para llegar a la misma conclusion.
        if (fields.Contains(LegacyOpcDaColumn) && !fields.Contains(SourceColumn))
        {
            problems.Add(new TagLoadError(lineNumber, "",
                $"'{path}' linea {lineNumber}: cabecera de la version anterior detectada. " +
                $"Renombrar la columna '{LegacyOpcDaColumn}' a 'SOURCE_TAG' y agregar la columna '{SourceColumn}' " +
                "(valores aceptados: OPCDA, OPC_DA, SQL)."));
            columnIndex = new Dictionary<string, int>();
            errors = problems;
            return false;
        }

        var seen = new HashSet<string>();
        foreach (var field in fields)
        {
            if (!seen.Add(field))
            {
                problems.Add(new TagLoadError(lineNumber, "",
                    $"'{path}' linea {lineNumber}: la columna '{field}' aparece repetida en la cabecera."));
            }
            else if (!Columns.Contains(field))
            {
                problems.Add(new TagLoadError(lineNumber, "",
                    $"'{path}' linea {lineNumber}: la columna '{field}' de la cabecera no es ninguna de las esperadas ({string.Join(", ", Columns)})."));
            }
        }

        foreach (var expected in Columns)
        {
            if (!seen.Contains(expected))
            {
                problems.Add(new TagLoadError(lineNumber, "",
                    $"'{path}' linea {lineNumber}: falta la columna '{expected}' en la cabecera."));
            }
        }

        if (problems.Count > 0)
        {
            columnIndex = new Dictionary<string, int>();
            errors = problems;
            return false;
        }

        var index = new Dictionary<string, int>();
        for (var i = 0; i < fields.Length; i++)
        {
            index[fields[i]] = i;
        }

        columnIndex = index;
        errors = problems;
        return true;
    }
}
