using System.Globalization;

namespace Gateway.Core;

/// <summary>Una fila de la tabla de detalle: el estado de un tag, ya aplanado para mostrar.</summary>
/// <param name="ScaledValue">
/// Valor formateado en cultura invariante. Se manda como texto y no como objeto
/// para que el JSON tenga tipo estable: el valor de un tag puede ser numero,
/// booleano o texto segun el CSV, y serializarlo crudo daria un campo que cambia
/// de tipo por fila. Ademas la maquina corre en es-AR: sin cultura invariante un
/// 8009.57 saldria "8009,57" y romperia a cualquier consumidor.
/// </param>
/// <param name="SourceTimestamp">
/// Momento de origen segun el servidor DA, o null si el tag nunca entrego dato.
/// Va al lado de LastUpdateUtc en la vista a proposito: son relojes distintos y
/// verlos juntos es lo que deja distinguir un tag congelado de uno que no llega.
/// </param>
public sealed record TagDiagnosticsRow(
    string UaName,
    // La fuente va en su propio campo y no concatenada al nombre: la vista la
    // usa como columna filtrable, y con dos fuentes el nombre de origen solo
    // no dice donde esta buscando el gateway ese tag.
    string Source,
    string SourceTag,
    string? ScaledValue,
    string QualityMaster,
    string QualitySubstatus,
    DateTime? SourceTimestamp,
    DateTime LastUpdateUtc,
    double SecondsSinceUpdate,
    bool EverAnswered);

/// <summary>Una pagina de resultados, con el total para que la vista sepa cuanto falta.</summary>
public sealed record TagDiagnosticsPage(
    int TotalMatching,
    int Offset,
    IReadOnlyList<TagDiagnosticsRow> Rows);

/// <summary>
/// Consulta la cache para la tabla de detalle. Vive en Core y no en la capa web
/// porque decidir que cuenta como "degradado" es la misma regla que aplica el
/// snapshot: escrita en dos lados, terminaria contradiciendose.
/// </summary>
public static class TagDiagnosticsQuery
{
    /// <summary>
    /// Filtra y pagina del lado del servidor. Por defecto solo los tags que no
    /// estan en Good: volcar 8.000 filas por segundo arrastra el navegador y
    /// gasta CPU en algo que nadie mira.
    /// </summary>
    public static TagDiagnosticsPage Query(
        TagCache cache,
        bool onlyDegraded = true,
        string? search = null,
        int offset = 0,
        int limit = 100)
    {
        var now = DateTime.UtcNow;
        var matching = new List<TagDiagnosticsRow>();

        foreach (var uaName in cache.UaNames)
        {
            // Misma puerta que usa el node manager: Get degrada al leer, asi que
            // la tabla no puede contradecir a lo que ve el cliente UA.
            var state = cache.Get(uaName);

            if (onlyDegraded && state.Quality.Master == QualityMaster.Good) continue;

            if (!string.IsNullOrWhiteSpace(search) &&
                uaName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            matching.Add(new TagDiagnosticsRow(
                uaName,
                cache.GetSourceKey(uaName)?.Source.ToString() ?? string.Empty,
                cache.GetSourceKey(uaName)?.SourceTag ?? string.Empty,
                Format(state.ScaledValue),
                state.Quality.Master.ToString(),
                state.Quality.Substatus.ToString(),
                // default significa "nunca hubo dato de origen", no el ano 1.
                state.SourceTimestamp == default ? null : state.SourceTimestamp,
                state.LastUpdateUtc,
                (now - state.LastUpdateUtc).TotalSeconds,
                state.ScaledValue is not null));
        }

        // Orden estable: sin esto la paginacion sobre un ConcurrentDictionary
        // podria repetir u omitir filas entre paginas.
        matching.Sort(static (a, b) => string.CompareOrdinal(a.UaName, b.UaName));

        var page = matching
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();

        return new TagDiagnosticsPage(matching.Count, offset, page);
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        // "R" (round-trip) da la cadena mas corta que reconstruye el mismo
        // double. "G17" tambien reconstruye, pero fuerza 17 digitos y termina
        // mostrando 29973.389843749999 donde el valor es 29973.38984375: la
        // basura binaria es real, pero exhibirla en un diagnostico solo confunde.
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value.ToString()
    };
}