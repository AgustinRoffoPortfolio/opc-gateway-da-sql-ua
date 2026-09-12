using System.Collections.Concurrent;

namespace Gateway.Core;

/// <summary>
/// Estado actual de un tag: lo que la cache responde cuando el lado UA pregunta.
/// </summary>
/// <param name="ScaledValue">
/// Valor ya transformado y convertido al DataType del CSV. Puede ser un valor
/// viejo: si la ultima lectura fue mala se conserva el anterior, con el
/// StatusCode malo y el timestamp original. Un dato congelado que se sabe
/// congelado es mas util que un vacio.
/// </param>
/// <param name="SourceTimestamp">
/// Momento de origen del valor que se esta devolviendo, no de la ultima lectura.
/// Si el valor es viejo, este timestamp tambien lo es: es lo que deja ver que
/// el tag esta congelado. Es el que vino del servidor DA y no se pisa nunca.
/// </param>
/// <param name="LastUpdateUtc">
/// Cuando el gateway incorporo la ultima muestra de este tag, buena o mala.
/// Reloj propio, no del origen: es el unico criterio confiable para decidir si
/// el dato dejo de refrescarse, porque no depende de que el servidor DA estampe
/// bien sus timestamps.
/// </param>
public readonly record struct TagState(
    object? ScaledValue,
    TagQuality Quality,
    DateTime SourceTimestamp,
    DateTime LastUpdateUtc);

/// <summary>
/// Frontera entre el mundo DA y el mundo UA. El driver empuja muestras a su
/// ritmo, el node manager pide estado al suyo, y ninguno de los dos conoce la
/// frecuencia del otro.
/// </summary>
/// <remarks>
/// Es la razon por la que diez clientes UA preguntando lo mismo no se traducen
/// en diez lecturas al servidor legado.
///
/// Tambien es la unica pieza que puede degradar un tag por antiguedad, porque
/// es la unica que conoce el estado anterior. El driver no tiene estado: si
/// fabricara muestras degradadas al caerse el vinculo, tendria que inventar un
/// SourceTimestamp para un dato que nunca leyo.
/// </remarks>
public sealed class TagCache
{
    // Indexado por nombre DA porque es la clave con la que llegan las muestras.
    // Es una lista y no una definicion sola: un mismo item DA puede alimentar
    // varios nodos UA con transformaciones distintas (el mismo caudal en m3/h
    // y en l/s, por ejemplo). La relacion es uno a muchos.
    private readonly Dictionary<string, List<TagDefinition>> _definitionsByDaName;

    // Camino inverso, solo para diagnostico: la relacion DA -> UA es uno a
    // muchos, pero UA -> DA es uno a uno, asi que se puede indexar directo.
    // Se arma una vez en el constructor y despues no se toca, por eso no es
    // concurrente: el hilo del request solo lee.
    private readonly Dictionary<string, string> _daNameByUaName;

    // ConcurrentDictionary porque el hilo que lee DA y el que publica UA son
    // distintos: uno escribe mientras el otro lee, sin lock explicito.
    private readonly ConcurrentDictionary<string, TagState> _stateByUaName = new();

    // Cuanto puede pasar sin refresco antes de considerar viejo cada tag, o
    // null para no degradarlo nunca. Deja de ser un valor unico de la cache
    // porque con dos fuentes no hay un ritmo unico (V2-11): un tag SQL que se
    // pollea cada 30 s estaria vencido casi siempre contra el umbral de DA.
    // Llega como duracion y no como configuracion: Gateway.Core no depende de
    // nadie, asi que el host traduce ciclos a tiempo y la cache solo mide.
    private readonly Dictionary<string, TimeSpan?> _staleAfterByUaName;

    public TagCache(IEnumerable<TagDefinition> definitions)
    {
        _definitionsByDaName = definitions
            .GroupBy(d => d.OpcDaName)
            .ToDictionary(g => g.Key, g => g.ToList());

        _daNameByUaName = _definitionsByDaName
            .SelectMany(entry => entry.Value.Select(d => (d.OpcUaName, entry.Key)))
            .ToDictionary(pair => pair.OpcUaName, pair => pair.Key);

        // Indexado por nombre UA y no por nombre DA porque la degradacion se
        // evalua al leer, y del otro lado siempre se pregunta por nombre UA.
        _staleAfterByUaName = _definitionsByDaName.Values
            .SelectMany(list => list)
            .ToDictionary(d => d.OpcUaName, d => d.StaleAfter);

        var now = DateTime.UtcNow;

        // Todo tag arranca declarado pero sin dato.Sin esto, un cliente UA que
        // conecta antes de la primera lectura recibiria "tag desconocido", que
        // es una causa distinta y mandaria a buscar el problema al lugar equivocado.
        //
        // El SourceTimestamp arranca en default y no en la hora actual: un tag
        // sin dato no tiene momento de origen, y ponerle "ahora" seria afirmar
        // una frescura que no existe. LastUpdateUtc si arranca ahora, porque es
        // desde este instante que se cuenta cuanto hace que no llega nada.
        foreach (var definition in _definitionsByDaName.Values.SelectMany(list => list))
            _stateByUaName[definition.OpcUaName] =
                new TagState(null, TagQuality.WaitingForInitialData, default, now);
    }

    public int Count => _stateByUaName.Count;

    /// <summary>Nombres UA de todos los tags configurados.</summary>
    public IEnumerable<string> UaNames => _stateByUaName.Keys;

    /// <summary>ItemIDs a pedirle al servidor DA.</summary>
    public IEnumerable<string> DaNames => _definitionsByDaName.Keys;

    /// <summary>
    /// ItemID DA que alimenta a un nodo UA, o null si el nombre no esta
    /// configurado. Es para la vista de diagnostico: ver los dos nombres juntos
    /// es lo que permite decidir si un tag mudo es culpa del CSV o del servidor.
    /// </summary>
    public string? GetDaName(string uaName) =>
        _daNameByUaName.GetValueOrDefault(uaName);

    /// <summary>
    /// Estado actual de un tag, ya degradado si dejo de refrescarse. Un tag que
    /// no esta en el CSV no es un error de lectura sino de configuracion, y se
    /// distingue como tal.
    /// </summary>
    public TagState Get(string uaName) =>
        _stateByUaName.TryGetValue(uaName, out var state)
            ? Degrade(state, _staleAfterByUaName.GetValueOrDefault(uaName), DateTime.UtcNow)
            : new TagState(null, TagQuality.UnknownTag, default, DateTime.UtcNow);

    /// <summary>
    /// Incorpora una tanda de muestras del driver DA.
    /// </summary>
    public void Update(IReadOnlyDictionary<string, TagSample> samples)
    {
        var now = DateTime.UtcNow;

        foreach (var (daName, sample) in samples)
        {
            if (!_definitionsByDaName.TryGetValue(daName, out var definitions))
                continue;   // el servidor DA mando algo que no pedimos

            // Una muestra puede alimentar varios nodos UA, cada uno con su
            // propia transformacion.
            foreach (var definition in definitions)
                _stateByUaName[definition.OpcUaName] =
                    Apply(definition, sample, _stateByUaName.GetValueOrDefault(definition.OpcUaName), now);
        }
    }

    /// <summary>
    /// Degrada la calidad de un tag que dejo de refrescarse.
    /// </summary>
    /// <remarks>
    /// Se calcula al leer y no en un barrido periodico: no hace falta otro hilo,
    /// y sobre todo el estado guardado queda intacto. Cuando el vinculo DA
    /// vuelve, la comparacion con la muestra nueva se hace contra la ultima
    /// calidad real que llego, no contra una degradacion que nos inventamos.
    ///
    /// La regla que ordena los casos: degradar nunca mejora un StatusCode. Si un
    /// tag ya venia malo y encima dejamos de tener noticias, pasarlo a Uncertain
    /// seria decirle al cliente que el dato mejoro justo cuando se corto.
    /// </remarks>
    private static TagState Degrade(TagState state, TimeSpan? staleAfter, DateTime now)
    {
        // Sin umbral no hay nada que medir: el tag conserva la calidad que le
        // puso su fuente. Es el caso SQL, donde la calidad viene en la columna Q.
        if (staleAfter is not { } window)
            return state;

        if (now - state.LastUpdateUtc < window)
            return state;

        // Unico caso en que la antiguedad empeora un Bad: "todavia no leimos"
        // envejecido pasa a "no hay nadie del otro lado". Es informacion nueva.
        if (state.Quality == TagQuality.WaitingForInitialData)
            return state with { Quality = TagQuality.NotConnected };

        // Cualquier otro Bad ya explica algo mas especifico que "esta viejo",
        // y pisarlo perderia la causa real.
        if (state.Quality.Master is QualityMaster.Bad or QualityMaster.Error)
            return state;

        return state with { Quality = TagQuality.LastUsableValue };
    }

    /// <summary>
    /// Calcula el nuevo estado de un tag a partir de una muestra y del estado previo.
    /// </summary>
    /// <remarks>
    /// LastUpdateUtc se refresca en los tres caminos, incluso cuando la muestra
    /// es mala. Esa es la diferencia entre "el servidor DA contesto con una
    /// calidad fea" y "el servidor DA no contesto": la primera es informacion
    /// legitima y no tiene que disparar la degradacion por antiguedad.
    /// </remarks>
    private static TagState Apply(TagDefinition definition, TagSample sample, TagState previous, DateTime now)
    {
        // NotConnected no es una respuesta del servidor DA: es la duda que el
        // gateway publica cuando un alta se rechaza por primera vez, sin saber
        // todavia si el ItemID no existe o si el servidor no termino de levantar.
        // Pisar con esa duda un tag que ya tenia valor bueno lo deja peor que
        // durante la caida: por especificacion un DataValue con StatusCode Bad
        // no transporta valor, asi que el cliente pierde el ultimo dato conocido
        // y su SourceTimestamp. Se devuelve el estado previo tal cual, sin
        // refrescar LastUpdateUtc, para que la antiguedad lo siga degradando
        // hasta LastUsableValue, que es Uncertain y si conserva el valor.
        // Un rechazo confirmado en el reintento llega como ItemRejected y ese si
        // pisa: ahi ya es un error de configuracion, no una duda.
        if (sample.Quality == TagQuality.NotConnected && previous.ScaledValue is not null)
            return previous;

        // Calidad no utilizable: se conserva el valor anterior con su timestamp
        // original, y se pega la calidad nueva. Escalar sobre una lectura mala
        // produce un numero con apariencia de valido, que es peor que no publicar.
        if (!sample.Quality.IsUsable)
            return new TagState(previous.ScaledValue, sample.Quality, previous.SourceTimestamp, now);

        if (!TryScale(definition, sample.Value, out var scaled))
            return new TagState(previous.ScaledValue, TagQuality.ConversionError, previous.SourceTimestamp, now);

        return new TagState(scaled, sample.Quality, sample.SourceTimestamp, now);
    }

    /// <summary>
    /// Aplica multiplicador y offset y convierte al tipo declarado en el CSV.
    /// </summary>
    /// <remarks>
    /// La transformacion es numerica, asi que Boolean y String pasan derecho:
    /// multiplicar un texto no significa nada. Si el CSV declara un tipo que el
    /// valor DA no puede tomar, es error de configuracion y se marca como tal.
    /// </remarks>
    private static bool TryScale(TagDefinition definition, object? raw, out object? scaled)
    {
        scaled = null;
        if (raw is null) return false;

        switch (definition.DataType)
        {
            case TagDataType.String:
                scaled = raw.ToString();
                return true;

            case TagDataType.Boolean:
                if (raw is bool b) { scaled = b; return true; }
                return false;

            case TagDataType.Double:
            case TagDataType.Int32:
                // InvariantCulture: el valor puede llegar como texto con punto
                // decimal, y la maquina esta en es-AR (coma). Sin esto, "8009.57"
                // no parsea o parsea mal.
                if (!TryToDouble(raw, out var numeric)) return false;

                var value = numeric * definition.Multiplier + definition.Offset;

                if (definition.DataType == TagDataType.Double)
                {
                    scaled = value;
                    return true;
                }

                // Fuera de rango no es un redondeo: es un tipo mal declarado.
                if (value is < int.MinValue or > int.MaxValue) return false;
                scaled = (int)Math.Round(value);
                return true;

            default:
                return false;
        }
    }

    private static bool TryToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f: value = f; return true;
            case int i: value = i; return true;
            case short s: value = s; return true;
            case long l: value = l; return true;
            case string text:
                return double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value);
            default:
                value = 0;
                return false;
        }
    }
}