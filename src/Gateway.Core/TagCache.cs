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
    // Indexado por el par (origen, nombre de origen), que es la clave con la
    // que llegan las muestras. El origen entra en la clave porque las dos
    // fuentes pueden usar el mismo nombre para tags distintos (V2-12), y el
    // comparador decide como se comparan los nombres de cada una (V2-17).
    // Es una lista y no una definicion sola: un mismo tag de origen puede
    // alimentar varios nodos UA con transformaciones distintas (el mismo caudal
    // en m3/h y en l/s, por ejemplo). La relacion es uno a muchos.
    private readonly Dictionary<TagKey, List<TagDefinition>> _definitionsByKey;

    // Camino inverso, solo para diagnostico: la relacion origen -> UA es uno a
    // muchos, pero UA -> origen es uno a uno, asi que se puede indexar directo.
    // Se arma una vez en el constructor y despues no se toca, por eso no es
    // concurrente: el hilo del request solo lee.
    private readonly Dictionary<string, TagKey> _keyByUaName;

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
        _definitionsByKey = definitions
            .GroupBy(d => new TagKey(d.Source, d.SourceTag), TagKeyComparer.Instance)
            .ToDictionary(g => g.Key, g => g.ToList(), TagKeyComparer.Instance);

        _keyByUaName = _definitionsByKey
            .SelectMany(entry => entry.Value.Select(d => (d.OpcUaName, entry.Key)))
            .ToDictionary(pair => pair.OpcUaName, pair => pair.Key);

        // Indexado por nombre UA y no por la clave de origen porque la
        // degradacion se evalua al leer, y del otro lado siempre se pregunta
        // por nombre UA.
        _staleAfterByUaName = _definitionsByKey.Values
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
        foreach (var definition in _definitionsByKey.Values.SelectMany(list => list))
            _stateByUaName[definition.OpcUaName] =
                new TagState(null, TagQuality.WaitingForInitialData, default, now);
    }

    public int Count => _stateByUaName.Count;

    /// <summary>Nombres UA de todos los tags configurados.</summary>
    public IEnumerable<string> UaNames => _stateByUaName.Keys;

    /// <summary>
    /// Nombres de origen de una sola fuente: los ItemIDs a pedirle al servidor
    /// DA, o los tags a buscar en la tabla SQL.
    /// </summary>
    /// <remarks>
    /// Pide la fuente y no devuelve todo junto porque el consumidor de esta
    /// lista da de alta items contra su propio servidor. Con los nombres de la
    /// otra fuente adentro, el servidor DA los rechazaria y quedarian
    /// reintentandose para siempre contra un servidor legado (V2-12).
    /// </remarks>
    public IEnumerable<string> SourceTags(TagSource source) =>
        _definitionsByKey.Keys.Where(k => k.Source == source).Select(k => k.SourceTag);

    /// <summary>
    /// Nombres declarados para una fuente que no aparecen entre los recibidos.
    /// </summary>
    /// <remarks>
    /// El cruce vive aca y no en el consumidor porque el criterio de comparacion
    /// depende de la fuente (V2-17): SQL Server no distingue mayusculas y DA si.
    /// Preguntandole al diccionario de definiciones, ese criterio queda
    /// garantizado por construccion en vez de depender de que quien llama arme
    /// su coleccion con el mismo comparador.
    /// </remarks>
    public IReadOnlyCollection<string> MissingTags(TagSource source, IReadOnlyDictionary<string, TagSample> received)
    {
        // El set se arma con el comparador de la fuente en vez de usar el
        // ContainsKey del diccionario recibido, para que el criterio no dependa
        // de como lo armo quien llama. Es una pasada por las filas recibidas
        // por ciclo, no una por cada tag declarado.
        var arrived = new HashSet<string>(received.Keys, TagKeyComparer.ComparerFor(source));

        var missing = new List<string>();

        foreach (var key in _definitionsByKey.Keys)
        {
            if (key.Source != source)
                continue;

            if (!arrived.Contains(key.SourceTag))
                missing.Add(key.SourceTag);
        }

        return missing;
    }

    /// <summary>
    /// Fuente y nombre de origen que alimentan a un nodo UA, o null si el
    /// nombre no esta configurado. Es para la vista de diagnostico: ver los dos
    /// nombres juntos es lo que permite decidir si un tag mudo es culpa del CSV
    /// o de la fuente.
    /// </summary>
    public TagKey? GetSourceKey(string uaName) =>
        _keyByUaName.TryGetValue(uaName, out var key) ? key : null;

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
    /// Incorpora una tanda de muestras de una fuente. Las muestras se cruzan
    /// solo contra las definiciones de esa fuente.
    /// </summary>
    /// <remarks>
    /// El descarte en silencio de una muestra no declarada es, ademas, el
    /// filtrado que pide R3: el driver SQL entrega la tabla entera y aca se
    /// queda solo lo que declara el CSV.
    /// </remarks>
    public void Update(TagSource source, IReadOnlyDictionary<string, TagSample> samples)
    {
        var now = DateTime.UtcNow;

        foreach (var (sourceTag, sample) in samples)
        {
            if (!_definitionsByKey.TryGetValue(new TagKey(source, sourceTag), out var definitions))
                continue;   // la fuente mando algo que no pedimos

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

        // Muestra con calidad utilizable pero sin valor: es la columna V en
        // NULL de CURR_DATA (V2-16). Hay fila, pero no hay medicion que fechar.
        // Se conserva el valor anterior con su SourceTimestamp -que no avanza
        // aunque TS haya cambiado, porque avanzarlo afirmaria una medicion que
        // no existe- y solo se toca la calidad. Sin esta rama el nulo caeria en
        // TryScale y saldria como ConversionError, que es Bad y le borra al
        // cliente el ultimo dato bueno: exactamente lo contrario del principio 3.
        if (sample.Value is null)
        {
            // Un valor viejo publicado en Good afirmaria una frescura que no
            // tiene. Desde SQL no pasa (el driver ya manda Uncertain), pero la
            // rama es compartida con DA y esa es la unica mentira que podria
            // introducir. Una calidad Uncertain se respeta tal cual: ya dice
            // lo que hay que decir.
            var quality = sample.Quality.Master == QualityMaster.Good
                ? TagQuality.LastUsableValue
                : sample.Quality;

            return new TagState(previous.ScaledValue, quality, previous.SourceTimestamp, now);
        }

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
                // Multiplier y Offset no se aplican nunca a un booleano: no
                // significan nada, y un Offset distinto de 0 lo daria vuelta.
                if (raw is bool b) { scaled = b; return true; }

                // Desde SQL un booleano llega como 0 o 1 en la columna real
                // (P2, V2-15). Distinto de 0 es true: es la convencion del
                // mundo del proceso y tolera un 0,9999 de una conversion
                // intermedia, en vez de descartarlo como valor imposible.
                if (TryToDouble(raw, out var flag)) { scaled = flag != 0; return true; }
                return false;

            case TagDataType.Double:
            case TagDataType.Float:
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

                // El escalado se calcula en double y el cast baja recien al
                // final (V2-14): calcular en la precision alta evita acumular
                // error en la propia cuenta, que es un problema distinto del de
                // la precision del dato de origen.
                if (definition.DataType == TagDataType.Float)
                {
                    scaled = (float)value;
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