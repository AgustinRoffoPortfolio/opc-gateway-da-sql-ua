namespace Gateway.Sql;

/// Validacion y escape de identificadores de SQL Server (V2-9).
///
/// Por que existe: un nombre de tabla no puede viajar como parametro de
/// ADO.NET. Un parametro ocupa el lugar de un *valor*, y "SELECT * FROM @tabla"
/// no es SQL valido. El nombre se arma pegando texto por definicion, y pegar
/// texto es el mecanismo de la inyeccion. Como la concatenacion no se puede
/// evitar, lo que se controla es que se acepta antes de concatenar.
public static class SqlIdentifier
{
    /// Limite de SQL Server para un identificador regular.
    public const int MaxLength = 128;

    /// Lista blanca: letra o guion bajo inicial, despues letras ASCII, digitos
    /// o guion bajo. Deliberadamente mas estrecha que lo que SQL Server acepta
    /// (que admite espacios y acentos entre corchetes): los nombres de esta
    /// tabla son todos ASCII simple, y una lista blanca angosta que se amplia
    /// si hace falta es mejor que una ancha que hay que achicar despues.
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        if (!IsLetterOrUnderscore(value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!IsLetterOrUnderscore(c) && !(c >= '0' && c <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    /// Encierra el identificador entre corchetes, duplicando cualquier ]
    /// interno. Es la misma regla de QUOTENAME de SQL Server, aplicada aca
    /// antes de abrir la conexion en vez de gastar una consulta en pedirsela
    /// al servidor.
    ///
    /// El escape es redundante si el valor ya paso por IsValid, que rechaza el
    /// ]. Esta igual para que Quote sea correcta sola: una funcion que solo es
    /// segura porque su llamador la cuida no es segura.
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "[" + value.Replace("]", "]]") + "]";
    }

    /// Nombre completo de la tabla, como lo pide la consulta de R3.
    /// Ejemplo: [PLANT_DB].[dbo].[CURRENT_VALUES]
    public static string QualifyTable(string database, string schema, string table)
        => Quote(database) + "." + Quote(schema) + "." + Quote(table);

    private static bool IsLetterOrUnderscore(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
}