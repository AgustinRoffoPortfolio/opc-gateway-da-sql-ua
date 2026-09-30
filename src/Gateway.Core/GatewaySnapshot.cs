namespace Gateway.Core;

/// <summary>Estado del vinculo con una fuente, visto por su driver.</summary>
public enum LinkState
{
    Disconnected,
    Reconnecting,
    Connected,

    /// El vinculo dice estar vivo pero el ciclo de adquisicion no vuelve. Es el
    /// caso del servidor DA colgado sin morir: COM no falla, simplemente no
    /// contesta. Sin este estado la pagina mostraria "conectado" con el hilo
    /// bloqueado, que es la peor mentira posible en un diagnostico.
    Stalled
}

/// <summary>
/// Interpretacion de los contadores de UNA fuente. Es una heuristica, no una
/// medicion: por eso se muestra siempre junto a los numeros que la generaron y
/// nunca se publica como nodo UA (el address space es un contrato, y una
/// opinion no va ahi).
/// </summary>
public enum Diagnosis
{
    Healthy,
    LinkDown,
    ServerStalled,

    /// Los tags mudos nunca entregaron un dato: esos nombres de origen no
    /// existieron nunca del otro lado. Apunta al CSV, no a la red.
    LikelyCsvMismatch,

    /// Los tags mudos si entregaron datos antes. La fuente sigue ahi pero
    /// perdio sus items: el caso del simulador relanzado sin su configuracion.
    ServerRepopulatedEmpty,

    /// Hay tags mudos pero son una minoria del total. No es una causa global, y
    /// afirmar una mandaria a reiniciar un servidor que esta sano.
    PartialDegradation,

    /// Los dos buckets estan parejos: estan pasando dos cosas a la vez y el
    /// gateway no elige por el operador.
    Indeterminate
}

/// <summary>
/// Lo que un driver reporta sobre si mismo. Antes era DaLinkStatus: el unico
/// campo atado a DA era el nombre del record, todo lo demas es generico de
/// cualquier vinculo con polling. Lo llena Gateway.Da o el driver SQL.
/// </summary>
public sealed record SourceLinkStatus(
    TagSource Source,
    LinkState State,
    DateTime? LastSuccessfulCycleUtc,
    int ReconnectAttempts,
    string? LastError,
    long ReadCycles,
    long ReadFailures,
    long Connections,
    long Disconnections,
    double LastCycleMs,
    double AvgCycleMs,
    double MaxCycleMs,
    int ConfiguredIntervalMs,
    /// <param name="LastCacheStampUtc">
    /// Hora del gateway al terminar la ultima actualizacion de cache. Se publica
    /// como nodo UA para que un cliente pueda medir la latencia cache->cliente
    /// restandola de su propio UtcNow. Null hasta el primer ciclo.
    /// </param>
    DateTime? LastCacheStampUtc);

/// <summary>Lo que el server UA reporta sobre sus clientes. Lo llena Gateway.Ua.</summary>
public sealed record UaServerStatus(
    int ConnectedSessions,
    int MonitoredItems);

/// <summary>
/// Resumen global del proceso. Ya no habla del vinculo: con dos fuentes, un
/// unico LinkState obligaria a inventar "el peor de los dos" y una base caida
/// con DA sano se veria como un gateway caido entero (invariante 8).
/// </summary>
public sealed record GatewayStatus(
    DateTime StartedUtc,
    double UptimeSeconds);

/// <summary>
/// Conteo de tags por calidad. Sirve para el total y para cada fuente: los
/// contadores de ciclos, fallos y conexiones salen de SourceLinkStatus, que es
/// donde ya vivian, en vez de copiarse aca.
/// </summary>
public sealed record GatewayCounters(
    int TotalConfigured,
    int Good,
    int Uncertain,
    int Bad,
    int WaitingForInitialData,
    int SilentNeverAnswered,
    int SilentPreviouslyAnswered)
{
    /// <summary>Tags que dejaron de contestar, sin contar los que aun no se leyeron.</summary>
    public int SilentTotal => SilentNeverAnswered + SilentPreviouslyAnswered;
}

/// <summary>
/// Lo que es global al proceso y no pertenece a ninguna fuente. Los tiempos de
/// ciclo se fueron a SourceLinkStatus: con dos fuentes de ritmos distintos
/// (1000 ms contra decenas de segundos) un promedio unico no significa nada.
/// </summary>
public sealed record GatewayPerformance(
    int ConnectedUaSessions,
    int MonitoredItems,
    double WorkingSetMb);

/// <summary>
/// Todo lo que se reporta de una fuente: su vinculo, sus tags y su diagnostico.
/// Es la unidad que la pagina de diagnostico dibuja como una seccion y que el
/// node manager publica como una rama.
/// </summary>
public sealed record SourceSnapshot(
    SourceLinkStatus Link,
    double? SecondsSinceLastCycle,
    GatewayCounters Counters,
    Diagnosis Diagnosis);

/// <summary>
/// Una fuente con tags declarados en el CSV que no arranco. No tiene vinculo
/// ni contadores que reportar, pero si un motivo: sin esta entrada la pagina
/// la dibujaria igual que una fuente sin tags, que es no dibujarla, y el
/// operador no se enteraria de que hay tags que nunca se van a actualizar.
/// </summary>
/// <param name="Reason">Motivo ya redactado para mostrar tal cual.</param>
public sealed record InactiveSource(
    TagSource Source,
    string Reason);

/// <summary>
/// Foto del gateway en un instante. Unica fuente para los nodos UA de
/// diagnostico y para la pagina web: si cada vista armara sus propios numeros,
/// terminarian discrepando justo cuando hay un problema.
/// </summary>
public sealed record GatewaySnapshot(
    DateTime TakenUtc,
    GatewayStatus Status,
    /// <param name="Sources">
    /// Una entrada por fuente activa, en el orden en que las paso el host. No
    /// hay diagnostico global: combinar dos en uno volveria a esconder que una
    /// fuente esta sana mientras la otra no.
    /// </param>
    IReadOnlyList<SourceSnapshot> Sources,
    GatewayCounters Counters,
    GatewayPerformance Performance,
    /// <param name="Audit">
    /// Conexiones e intentos rechazados. Va en la foto y no por un costado
    /// porque las dos vistas tienen que ver los mismos numeros: un contador de
    /// rechazos que discrepa entre la pagina y los nodos UA es peor que no tenerlo.
    /// </param>
    UaAuditSnapshot Audit,
    /// <param name="InactiveSources">
    /// Fuentes con tags declarados que no arrancaron (B3). Una fuente sin tags
    /// declarados no esta ni aca ni en Sources: no se usa. El node manager no
    /// la lee; va en la foto para que la pagina la muestre sin un canal aparte.
    /// </param>
    IReadOnlyList<InactiveSource> InactiveSources)
{
    /// Por debajo de esta fraccion de tags mudos no se afirma una causa global.
    private const double PartialDegradationThreshold = 0.05;

    /// Cuanto tiene que dominar un bucket para atribuirle la causa. No es magia:
    /// dos tags mal tipeados en un CSV de 8.000 no cambian el diagnostico, pero
    /// mitad y mitad si significa que estan pasando dos cosas.
    private const double AttributionThreshold = 0.90;

    /// <summary>
    /// Arma la foto recorriendo la cache por la misma puerta que usa el node
    /// manager (<see cref="TagCache.Get"/>), que degrada al leer. Leer el estado
    /// por otro camino daria una vista que puede contradecir a la del cliente UA.
    /// Los tags se cuentan una sola vez y se acumulan en paralelo en el total y
    /// en el bucket de su fuente.
    /// </summary>
    public static GatewaySnapshot Build(
        TagCache cache,
        IReadOnlyList<SourceLinkStatus> links,
        UaServerStatus ua,
        DateTime startedUtc,
        UaAuditSnapshot audit,
        IReadOnlyList<InactiveSource>? inactiveSources = null)
    {
        var now = DateTime.UtcNow;

        var total = new Tally();
        var bySource = new Dictionary<TagSource, Tally>();
        foreach (var link in links) bySource[link.Source] = new Tally();

        foreach (var uaName in cache.UaNames)
        {
            var state = cache.Get(uaName);

            // Un tag cuya fuente no tiene link reportado se cuenta igual en el
            // total: el numero global no puede depender de que el host haya
            // registrado esa fuente.
            Tally? source = null;
            if (cache.GetSourceKey(uaName) is { } key)
                bySource.TryGetValue(key.Source, out source);

            Count(state, total, source);
        }

        var sources = links
            .Select(link => new SourceSnapshot(
                link,
                link.LastSuccessfulCycleUtc is { } last ? (now - last).TotalSeconds : null,
                bySource[link.Source].ToCounters(),
                Diagnose(link.State, bySource[link.Source].ToCounters())))
            .ToList();

        var status = new GatewayStatus(startedUtc, (now - startedUtc).TotalSeconds);

        var performance = new GatewayPerformance(
            ua.ConnectedSessions, ua.MonitoredItems,
            System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d);

        return new GatewaySnapshot(
            now, status, sources, total.ToCounters(), performance, audit,
            inactiveSources ?? []);
    }

    /// <summary>Acumulador mutable: evita recorrer la cache una vez por fuente.</summary>
    private sealed class Tally
    {
        public int Configured, Good, Uncertain, Bad, Waiting, NeverAnswered, PreviouslyAnswered;

        public GatewayCounters ToCounters() => new(
            Configured, Good, Uncertain, Bad, Waiting, NeverAnswered, PreviouslyAnswered);
    }

    private static void Count(TagState state, Tally total, Tally? source)
    {
        total.Configured++;
        if (source is not null) source.Configured++;

        switch (state.Quality.Master)
        {
            case QualityMaster.Good:
                total.Good++; if (source is not null) source.Good++; break;
            case QualityMaster.Uncertain:
                total.Uncertain++; if (source is not null) source.Uncertain++; break;
            default:
                total.Bad++; if (source is not null) source.Bad++; break;
        }

        // Al arranque todos los tags estan en este estado. Contarlos como
        // mudos dispararia un diagnostico de falla en cada inicio.
        if (state.Quality.Substatus == QualitySubstatus.BadWaitingForInitialData)
        {
            total.Waiting++;
            if (source is not null) source.Waiting++;
            return;
        }

        // Mudo = no se esta refrescando, que no es lo mismo que tener mala
        // calidad. Un tag que llega Uncertain porque el sensor esta fuera de
        // rango esta contestando bien; contarlo aca diagnosticaria una caida
        // donde solo hay ruido de proceso. Solo cuentan los dos casos en que
        // no llega dato: los Bad (rechazado, no conectado, no convierte) y
        // el Uncertain que la propia cache fabrica por antiguedad.
        var silent = state.Quality.Master is QualityMaster.Bad or QualityMaster.Error
                     || state.Quality.Substatus == QualitySubstatus.UncertainLastUsableValue;

        if (!silent) return;

        // ScaledValue solo se puebla con una muestra utilizable y ningun
        // camino lo vuelve a null: que no sea null significa que este tag
        // contesto alguna vez. Ese es todo el discriminante.
        if (state.ScaledValue is null)
        {
            total.NeverAnswered++;
            if (source is not null) source.NeverAnswered++;
        }
        else
        {
            total.PreviouslyAnswered++;
            if (source is not null) source.PreviouslyAnswered++;
        }
    }

    /// <summary>
    /// Primero manda el vinculo: no tiene sentido preguntarse por el CSV cuando
    /// no hay con quien hablar. Recien con el vinculo sano se mira la proporcion
    /// interna del bucket de mudos. Se evalua por fuente, con los tags de esa
    /// fuente: un CSV mal escrito del lado SQL no dice nada sobre el lado DA.
    /// </summary>
    private static Diagnosis Diagnose(LinkState state, GatewayCounters c)
    {
        if (state == LinkState.Stalled) return Diagnosis.ServerStalled;
        if (state is LinkState.Disconnected or LinkState.Reconnecting) return Diagnosis.LinkDown;

        var silent = c.SilentTotal;
        if (silent == 0) return Diagnosis.Healthy;

        if (c.TotalConfigured > 0 &&
            (double)silent / c.TotalConfigured < PartialDegradationThreshold)
            return Diagnosis.PartialDegradation;

        if ((double)c.SilentNeverAnswered / silent >= AttributionThreshold)
            return Diagnosis.LikelyCsvMismatch;

        if ((double)c.SilentPreviouslyAnswered / silent >= AttributionThreshold)
            return Diagnosis.ServerRepopulatedEmpty;

        return Diagnosis.Indeterminate;
    }
}