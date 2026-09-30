namespace Gateway.Sql;

/// Nivel de un aviso que el tracker le pide al host que loguee.
public enum SqlWarningLevel
{
    Information,
    Warning
}

/// Un aviso ya redactado. El host solo elige el metodo de log segun Level.
public sealed record SqlWarning(SqlWarningLevel Level, string Message);

/// Decide que avisar sobre los tags declarados que la consulta no trajo y
/// sobre las filas anomalas de tags declarados (B6.5, B4.3).
///
/// Avisa cuando cambia lo que reporta, no por ciclo ni por sesion: la primera
/// vez que aparece un conjunto, cada vez que cambia, y una sola vez cuando
/// vuelve a quedar vacio. El estado vive en la instancia y el host la crea una
/// sola vez, asi que una reconexion no vuelve a disparar avisos que ya se
/// dieron: la reconexion ya tiene sus propios avisos, y repetir el de un tag
/// ausente cada vez que se corta la red lo convertia en ruido.
///
/// Funcion pura con estado, igual que SqlSourceActivation en lo demas: no
/// loguea, no abre conexiones. Se testea sin base y sin logger.
public sealed class SqlWarningTracker
{
    /// Cuantos nombres entran en un aviso antes de resumir con "y N mas". Con
    /// 10.000 tags declarados, un aviso con todos seria una linea ilegible.
    public const int MaxNamesPerWarning = 10;

    private readonly HashSet<string> _declared;

    // Null = todavia no hubo ciclo. Distinto de vacio: el primer ciclo sano
    // de anomalias igual se informa una vez (arranque).
    private HashSet<string>? _lastMissing;
    private Dictionary<string, SqlRowAnomaly>? _lastAnomalies;

    public SqlWarningTracker(IEnumerable<string> declaredSqlTags, IEqualityComparer<string> comparer)
    {
        ArgumentNullException.ThrowIfNull(declaredSqlTags);
        ArgumentNullException.ThrowIfNull(comparer);

        _declared = new HashSet<string>(declaredSqlTags, comparer);
    }

    /// Evalua un ciclo y devuelve los avisos a emitir, posiblemente ninguno.
    public IReadOnlyList<SqlWarning> Evaluate(
        IReadOnlyCollection<string> missing,
        IReadOnlyDictionary<string, SqlRowAnomaly> anomalies,
        int totalRows)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(anomalies);

        var warnings = new List<SqlWarning>();
        EvaluateMissing(missing, warnings);
        EvaluateAnomalies(anomalies, totalRows, warnings);
        return warnings;
    }

    private void EvaluateMissing(IReadOnlyCollection<string> missing, List<SqlWarning> warnings)
    {
        var current = new HashSet<string>(missing, _declared.Comparer);

        // Sin ausentes en el primer ciclo no hay nada que decir: es el caso
        // normal y el INF de "sin ausentes" solo tiene sentido como cierre de
        // un aviso anterior.
        var previous = _lastMissing ?? new HashSet<string>(_declared.Comparer);
        _lastMissing = current;

        if (current.SetEquals(previous)) return;

        if (current.Count == 0)
        {
            warnings.Add(new SqlWarning(SqlWarningLevel.Information,
                "La consulta SQL vuelve a traer todos los tags declarados con origen SQL"));
            return;
        }

        warnings.Add(new SqlWarning(SqlWarningLevel.Warning,
            $"{current.Count} tag(s) declarados con origen SQL que la consulta no trajo: "
            + $"{FormatNames(current.Order(StringComparer.OrdinalIgnoreCase))}. Revisar el CSV o la tabla"));
    }

    private void EvaluateAnomalies(
        IReadOnlyDictionary<string, SqlRowAnomaly> anomalies, int totalRows, List<SqlWarning> warnings)
    {
        // Solo los tags declarados: una fila con NULL de un tag que el CSV no
        // pide no llega a ningun cliente, y avisarla era ruido (B6.5).
        var current = new Dictionary<string, SqlRowAnomaly>(_declared.Comparer);
        foreach (var (tag, anomaly) in anomalies)
            if (anomaly != SqlRowAnomaly.None && _declared.Contains(tag))
                current[tag] = anomaly;

        var first = _lastAnomalies is null;
        var previous = _lastAnomalies;
        _lastAnomalies = current;

        // Se compara nombre y flags: un tag que pasa de "sin valor" a "sin
        // calidad" es otra anomalia y se vuelve a avisar.
        if (!first && SameAnomalies(current, previous!)) return;

        if (current.Count == 0)
        {
            // En el primer ciclo sano va una linea de arranque; despues, solo
            // la transicion desde un aviso. Va el conteo de filas porque una
            // tabla vacia tambien da cero anomalias y se leeria como sana.
            warnings.Add(new SqlWarning(SqlWarningLevel.Information,
                $"Ciclo SQL sin filas anomalas en tags declarados sobre {totalRows} filas"));
            return;
        }

        var names = current
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key} ({Describe(p.Value)})");

        warnings.Add(new SqlWarning(SqlWarningLevel.Warning,
            $"Filas anomalas en tags declarados del ciclo SQL: {current.Count} tag(s): {FormatNames(names)}"));
    }

    private static bool SameAnomalies(
        Dictionary<string, SqlRowAnomaly> current, Dictionary<string, SqlRowAnomaly> previous)
    {
        if (current.Count != previous.Count) return false;

        foreach (var (tag, anomaly) in current)
            if (!previous.TryGetValue(tag, out var before) || before != anomaly)
                return false;

        return true;
    }

    private static string FormatNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        var shown = string.Join(", ", list.Take(MaxNamesPerWarning));
        return list.Count > MaxNamesPerWarning
            ? $"{shown} y {list.Count - MaxNamesPerWarning} mas"
            : shown;
    }

    /// Texto sin tildes, como el resto del log.
    private static string Describe(SqlRowAnomaly anomaly)
    {
        var parts = new List<string>(4);
        if (anomaly.HasFlag(SqlRowAnomaly.NullValue)) parts.Add("sin valor");
        if (anomaly.HasFlag(SqlRowAnomaly.NullQuality)) parts.Add("sin calidad");
        if (anomaly.HasFlag(SqlRowAnomaly.UnknownSubstatus)) parts.Add("substatus desconocido");
        if (anomaly.HasFlag(SqlRowAnomaly.InvalidTimestamp)) parts.Add("timestamp inexistente");
        return string.Join(", ", parts);
    }
}
