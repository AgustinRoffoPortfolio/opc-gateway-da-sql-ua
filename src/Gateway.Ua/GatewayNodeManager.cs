using System.Globalization;
using Gateway.Core;
using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Ua;

/// Construye el arbol de tags a partir de las TagDefinition leidas del CSV y
/// copia sus valores dummy a los nodos. La jerarquia sale de los puntos en
/// el nombre del tag: cada segmento intermedio es una carpeta, el ultimo
/// segmento es la variable.
public class GatewayNodeManager : CustomNodeManager2
{
    // Definiciones en el orden del CSV: de ahi sale el arbol de nodos.
    private readonly IReadOnlyList<TagDefinition> _tagDefinitions;

    // Fuente de los valores publicados. El node manager no lee del servidor DA:
    // pide el ultimo estado conocido a su ritmo, sin saber a que frecuencia se
    // llena la cache del otro lado.
    private readonly TagCache _cache;

    // Cada nodo apareado con el nombre del tag que lo alimenta. Se arma una vez,
    // al construir el arbol, para no resolver nombres en cada ciclo.
    private readonly List<(BaseDataVariableState Node, string TagName)> _bindings = new();

    // Carpetas ya creadas, indexadas por su ruta de segmentos, para que dos tags
    // que comparten un tramo del nombre no dupliquen la carpeta intermedia.
    private readonly Dictionary<string, FolderState> _folders = new();

    // Nodos de diagnostico, indexados por su nombre corto. Separados de
    // _bindings porque no salen de la cache: los llena el snapshot que arma el
    // host, que es el unico que ve las dos mitades del gateway.
    private readonly Dictionary<string, BaseDataVariableState> _diagnosticNodes = new();

    public GatewayNodeManager(IServerInternal server, ApplicationConfiguration configuration,
        string namespaceUri, IReadOnlyList<TagDefinition> tagDefinitions, TagCache cache)
        : base(server, configuration, namespaceUri)
    {
        _tagDefinitions = tagDefinitions;
        _cache = cache;
    }

    /// Cantidad de tags publicados. La usa el arranque para loguear.
    public int TagCount => _bindings.Count;

    /// <summary>
    /// Sesiones y monitored items actuales, contados desde el servidor.
    /// </summary>
    /// <remarks>
    /// Se cuenta aca y no se lee de SessionsDiagnosticsSummary porque ese nodo
    /// aparecia vacio con sesion anonima y nunca se supo por que. Un numero que
    /// se sabe de donde sale vale mas que uno oficial que a veces miente.
    /// </remarks>
    public UaServerStatus GetServerStatus()
    {
        try
        {
            var sessions = Server.SessionManager?.GetSessions()?.Count ?? 0;
            var subscriptions = Server.SubscriptionManager?.GetSubscriptions();

            var monitoredItems = subscriptions?.Sum(s => s.MonitoredItemCount) ?? 0;

            return new UaServerStatus(sessions, monitoredItems);
        }
        catch (Exception)
        {
            // El diagnostico nunca puede tirar abajo al que lo consulta: si el
            // server esta arrancando o apagandose, estos managers pueden no
            // estar. Cero es menos malo que una excepcion en el timer.
            return new UaServerStatus(0, 0);
        }
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var root = new FolderState(null)
            {
                NodeId = new NodeId("Gateway", NamespaceIndex),
                BrowseName = new QualifiedName("Gateway", NamespaceIndex),
                DisplayName = "Gateway",
                TypeDefinitionId = ObjectTypeIds.FolderType
            };
            root.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            LinkToParent(externalReferences, ObjectIds.ObjectsFolder,
                root.NodeId, ReferenceTypeIds.Organizes);

            // Los nodos nacen con el estado que la cache ya tiene: antes de la
            // primera lectura DA eso es "esperando dato inicial", no Good. Un
            // cliente que conecta en ese hueco tiene que ver que todavia no hay
            // dato, no un cero con calidad buena.
            // Los tags Hidden se saltean a proposito: el driver DA los sigue
            // leyendo y la cache los mantiene al dia, pero no se crea el nodo
            // UA, asi que ningun cliente los ve. Es distinto de ENABLED=False,
            // que directamente no se lee del servidor DA.
            foreach (var tag in _tagDefinitions)
            {
                if (tag.AccessLevel == TagAccessLevel.Hidden) continue;
                AddTag(root, tag, _cache.Get(tag.OpcUaName));
            }

            AddDiagnosticNodes(root);

            AddPredefinedNode(SystemContext, root);
        }
    }

    /// <summary>
    /// Arma las tres carpetas de diagnostico bajo la raiz Gateway.
    /// </summary>
    /// <remarks>
    /// Todos los nodos son String salvo los contadores, que son Int64/Double.
    /// El estado del vinculo va como texto y no como enum numerico: el valor lo
    /// tiene que poder leer un operador en UaExpert sin tabla de conversion.
    /// </remarks>
    private void AddDiagnosticNodes(NodeState root)
    {
        // Status queda con lo que es del proceso. El vinculo se fue a una rama
        // por fuente: con dos fuentes, un unico LinkState obligaria a publicar
        // "el peor de los dos" y una base caida con DA sano se veria como un
        // gateway caido entero.
        var status = GetOrAddFolder(root, "Gateway.Status", "Status");
        AddDiagnosticVariable(status, "UptimeSeconds", DataTypeIds.Double);

        // Una rama por fuente que realmente tenga tags declarados. Publicar una
        // rama SQL vacia cuando no hay tags SQL seria afirmar que existe algo
        // que no existe, y el address space es un contrato.
        foreach (var source in Enum.GetValues<TagSource>())
        {
            if (!_cache.SourceTags(source).Any()) continue;

            var folder = GetOrAddFolder(root, $"Gateway.{source}", source.ToString());
            AddDiagnosticVariable(folder, "LinkState", DataTypeIds.String);
            AddDiagnosticVariable(folder, "LastSuccessfulCycleUtc", DataTypeIds.String);
            AddDiagnosticVariable(folder, "SecondsSinceLastCycle", DataTypeIds.Double);
            AddDiagnosticVariable(folder, "ReconnectAttempts", DataTypeIds.Int32);
            AddDiagnosticVariable(folder, "LastError", DataTypeIds.String);
            AddDiagnosticVariable(folder, "ReadCycles", DataTypeIds.Int64);
            AddDiagnosticVariable(folder, "ReadFailures", DataTypeIds.Int64);
            AddDiagnosticVariable(folder, "Connections", DataTypeIds.Int64);
            AddDiagnosticVariable(folder, "Disconnections", DataTypeIds.Int64);
            AddDiagnosticVariable(folder, "LastCycleMs", DataTypeIds.Double);
            AddDiagnosticVariable(folder, "AvgCycleMs", DataTypeIds.Double);
            AddDiagnosticVariable(folder, "MaxCycleMs", DataTypeIds.Double);
            AddDiagnosticVariable(folder, "ConfiguredIntervalMs", DataTypeIds.Int32);
            // Sello del gateway al cerrar la ultima actualizacion de cache. Va
            // como String ISO-8601 y no como DateTime para que el cliente lo
            // parsee sin depender de como el stack UA convierta el tipo fecha.
            AddDiagnosticVariable(folder, "CacheStampUtc", DataTypeIds.String);
        }

        var counters = GetOrAddFolder(root, "Gateway.Counters", "Counters");
        AddDiagnosticVariable(counters, "TotalConfigured", DataTypeIds.Int32);
        AddDiagnosticVariable(counters, "Good", DataTypeIds.Int32);
        AddDiagnosticVariable(counters, "Uncertain", DataTypeIds.Int32);
        AddDiagnosticVariable(counters, "Bad", DataTypeIds.Int32);
        AddDiagnosticVariable(counters, "WaitingForInitialData", DataTypeIds.Int32);
        AddDiagnosticVariable(counters, "SilentNeverAnswered", DataTypeIds.Int32);
        AddDiagnosticVariable(counters, "SilentPreviouslyAnswered", DataTypeIds.Int32);
        // ReadCycles, ReadFailures, Connections y Disconnections se fueron a la
        // rama de cada fuente: sumarlos entre fuentes de ritmos distintos daba
        // un numero que no significaba nada.

        // Auditoria del lado UA, en la misma carpeta que los contadores DA: el
        // que abre el diagnostico todavia no sabe de que lado esta el problema,
        // asi que separarlos lo obligaria a adivinar antes de mirar.
        AddDiagnosticVariable(counters, "UaSessionsCreated", DataTypeIds.Int64);
        AddDiagnosticVariable(counters, "UaSessionsClosed", DataTypeIds.Int64);
        AddDiagnosticVariable(counters, "UaRejectedByCertificate", DataTypeIds.Int64);
        AddDiagnosticVariable(counters, "UaRejectedByToken", DataTypeIds.Int64);
        AddDiagnosticVariable(counters, "UaRejectedTotal", DataTypeIds.Int64);
        AddDiagnosticVariable(counters, "UaLastRejectionReason", DataTypeIds.String);
        AddDiagnosticVariable(counters, "UaLastRejectionUtc", DataTypeIds.String);

        var performance = GetOrAddFolder(root, "Gateway.Performance", "Performance");
        // Los tiempos de ciclo tambien viven por fuente: 1000 ms de DA y
        // decenas de segundos de SQL no promedian a nada util.
        AddDiagnosticVariable(performance, "ConnectedUaSessions", DataTypeIds.Int32);
        AddDiagnosticVariable(performance, "MonitoredItems", DataTypeIds.Int32);
        AddDiagnosticVariable(performance, "WorkingSetMb", DataTypeIds.Double);
    }

    /// <summary>Crea un nodo de diagnostico y lo registra para poder publicarlo.</summary>
    private void AddDiagnosticVariable(NodeState parent, string name, NodeId dataType)
    {
        var nodeId = $"{parent.BrowseName.Name}.{name}";

        var variable = new BaseDataVariableState(parent)
        {
            NodeId = new NodeId($"Gateway.{nodeId}", NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow
        };

        parent.AddChild(variable);
        _diagnosticNodes[nodeId] = variable;
    }

    /// Copia a los nodos el ultimo estado conocido de cada tag y avisa a los
    /// clientes suscriptos. Se llama desde el timer del programa principal.
    public void UpdateValues()
    {
        lock (Lock)
        {
            foreach (var (node, tagName) in _bindings)
                Publish(node, _cache.Get(tagName));
        }
    }

    /// <summary>
    /// Vuelca el snapshot a los nodos de diagnostico. Lo llama el host, que es
    /// quien puede armarlo: el node manager no ve el lado DA.
    /// </summary>
    /// <remarks>
    /// Todos los nodos se publican con StatusCode Good aunque reporten una
    /// falla. Por especificacion un DataValue con StatusCode de master Bad no
    /// transporta valor, asi que un nodo "el DA esta caido" publicado como Bad
    /// se veria vacio en el cliente justo cuando hace falta leerlo. La falla va
    /// en el contenido, nunca en la calidad del nodo que la reporta.
    /// </remarks>
    public void PublishDiagnostics(GatewaySnapshot snapshot)
    {
        lock (Lock)
        {
            var now = DateTime.UtcNow;

            SetDiagnostic("Status.UptimeSeconds", snapshot.Status.UptimeSeconds, now);

            foreach (var s in snapshot.Sources)
            {
                var f = s.Link.Source.ToString();
                SetDiagnostic($"{f}.LinkState", s.Link.State.ToString(), now);
                SetDiagnostic($"{f}.LastSuccessfulCycleUtc",
                    s.Link.LastSuccessfulCycleUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "nunca", now);
                SetDiagnostic($"{f}.SecondsSinceLastCycle", s.SecondsSinceLastCycle ?? 0d, now);
                SetDiagnostic($"{f}.ReconnectAttempts", s.Link.ReconnectAttempts, now);
                SetDiagnostic($"{f}.LastError", s.Link.LastError ?? "", now);
                SetDiagnostic($"{f}.ReadCycles", s.Link.ReadCycles, now);
                SetDiagnostic($"{f}.ReadFailures", s.Link.ReadFailures, now);
                SetDiagnostic($"{f}.Connections", s.Link.Connections, now);
                SetDiagnostic($"{f}.Disconnections", s.Link.Disconnections, now);
                SetDiagnostic($"{f}.LastCycleMs", s.Link.LastCycleMs, now);
                SetDiagnostic($"{f}.AvgCycleMs", s.Link.AvgCycleMs, now);
                SetDiagnostic($"{f}.MaxCycleMs", s.Link.MaxCycleMs, now);
                SetDiagnostic($"{f}.ConfiguredIntervalMs", s.Link.ConfiguredIntervalMs, now);
                SetDiagnostic($"{f}.CacheStampUtc",
                    s.Link.LastCacheStampUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "", now);
            }

            var c = snapshot.Counters;
            SetDiagnostic("Counters.TotalConfigured", c.TotalConfigured, now);
            SetDiagnostic("Counters.Good", c.Good, now);
            SetDiagnostic("Counters.Uncertain", c.Uncertain, now);
            SetDiagnostic("Counters.Bad", c.Bad, now);
            SetDiagnostic("Counters.WaitingForInitialData", c.WaitingForInitialData, now);
            SetDiagnostic("Counters.SilentNeverAnswered", c.SilentNeverAnswered, now);
            SetDiagnostic("Counters.SilentPreviouslyAnswered", c.SilentPreviouslyAnswered, now);


            var a = snapshot.Audit;
            SetDiagnostic("Counters.UaSessionsCreated", a.SessionsCreated, now);
            SetDiagnostic("Counters.UaSessionsClosed", a.SessionsClosed, now);
            SetDiagnostic("Counters.UaRejectedByCertificate", a.RejectedByCertificate, now);
            SetDiagnostic("Counters.UaRejectedByToken", a.RejectedByToken, now);
            SetDiagnostic("Counters.UaRejectedTotal", a.RejectedTotal, now);
            // Vacio y no "nunca" cuando no hubo rechazos: el nodo es un dato,
            // no una frase. Que este vacio ya dice que no paso nada.
            // Palabra explicita y no string vacio: el resto del arbol ya usa
            // este patron (Status.LastSuccessfulCycleUtc publica "nunca"), y un
            // "" obliga al cliente a adivinar si no hubo rechazos o si el nodo
            // dejo de publicarse.
            SetDiagnostic("Counters.UaLastRejectionReason", a.LastRejectionReason ?? "ninguno", now);
            SetDiagnostic("Counters.UaLastRejectionUtc",
                a.LastRejectionUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "nunca", now);

            var p = snapshot.Performance;
            SetDiagnostic("Performance.ConnectedUaSessions", p.ConnectedUaSessions, now);
            SetDiagnostic("Performance.MonitoredItems", p.MonitoredItems, now);
            SetDiagnostic("Performance.WorkingSetMb", Math.Round(p.WorkingSetMb, 1), now);
        }
    }

    /// <summary>
    /// Publica el estado de las fuentes con tags declarados que no arrancaron
    /// (B10). Lo llama el host una sola vez, al arrancar.
    /// </summary>
    /// <remarks>
    /// La rama de una fuente inactiva se crea igual (hay tags declarados), pero
    /// PublishDiagnostics solo recorre las fuentes activas, asi que sus nodos
    /// quedaban en null para siempre. Alcanza con publicar una vez: ningun ciclo
    /// vuelve a tocar esta rama. Solo LinkState y LastError; el resto queda en
    /// null, porque no hay ciclos ni conexiones que contar.
    ///
    /// Disconnected y no un valor nuevo del enum: el contrato del nodo ya tiene
    /// ese vocabulario, y el motivo va en LastError. El texto es fijo y no el
    /// Reason de InactiveSource: ese esta redactado para la pagina.
    /// </remarks>
    public void PublishInactiveSources(IReadOnlyList<InactiveSource> inactiveSources)
    {
        lock (Lock)
        {
            var now = DateTime.UtcNow;

            foreach (var inactive in inactiveSources)
            {
                var f = inactive.Source.ToString();
                SetDiagnostic($"{f}.LinkState", LinkState.Disconnected.ToString(), now);
                SetDiagnostic($"{f}.LastError", "Configuracion invalida", now);
            }
        }
    }

    private void SetDiagnostic(string key, object value, DateTime timestamp)
    {
        if (!_diagnosticNodes.TryGetValue(key, out var node)) return;

        node.Value = value;
        node.StatusCode = StatusCodes.Good;

        // Aca el SourceTimestamp si es la hora del gateway, y esta bien: el dato
        // se origina aca. Es lo contrario de un tag, que lo trae del servidor DA.
        node.Timestamp = timestamp;
        node.ClearChangeMasks(SystemContext, false);
    }

    /// Vuelca un estado de la cache a un nodo UA.
    private void Publish(BaseDataVariableState node, TagState state)
    {
        node.Value = state.ScaledValue;
        node.StatusCode = QualityMapper.ToStatusCode(state.Quality);

        // node.Timestamp es el SourceTimestamp: el momento en que el dato se
        // origino en el servidor DA. El ServerTimestamp lo pone el stack UA al
        // responder, y son cosas distintas. Pisar este con la hora de lectura
        // corrompe el historico de cualquier cosa que este aguas abajo.
        node.Timestamp = state.SourceTimestamp;

        node.ClearChangeMasks(SystemContext, false);
    }

    // ---------- Construccion del arbol ----------

    /// Agrega un tag al arbol, creando las carpetas intermedias que hagan
    /// falta segun los segmentos separados por punto en su nombre.
    private void AddTag(NodeState root, TagDefinition tag, TagState state)
    {
        var segments = tag.OpcUaName.Split('.');
        NodeState parent = root;
        var path = "";

        for (var i = 0; i < segments.Length - 1; i++)
        {
            path = path.Length == 0 ? segments[i] : $"{path}.{segments[i]}";
            parent = GetOrAddFolder(parent, path, segments[i]);
        }

        var variable = CreateVariable(parent, tag.OpcUaName, segments[^1], tag.DataType, state);
        parent.AddChild(variable);
        _bindings.Add((variable, tag.OpcUaName));
    }

    /// Devuelve la carpeta para esa ruta, creandola la primera vez que se pide.
    private FolderState GetOrAddFolder(NodeState parent, string path, string name)
    {
        if (_folders.TryGetValue(path, out var existing)) return existing;

        var folder = new FolderState(parent)
        {
            NodeId = new NodeId(path, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            ReferenceTypeId = ReferenceTypeIds.Organizes
        };
        parent.AddChild(folder);
        _folders[path] = folder;
        return folder;
    }

    /// Variable simple, sin EUInformation ni rango. El CSV ya trae EU desde la
    /// Fase 3, pero todavia no se expone como propiedad del nodo: queda para
    /// cuando haya un cliente que la consuma.
    private BaseDataVariableState CreateVariable(NodeState parent, string tagName, string name,
        TagDataType dataType, TagState state)
    {
        var uaDataType = dataType switch
        {
            TagDataType.Double => DataTypeIds.Double,
            TagDataType.Boolean => DataTypeIds.Boolean,
            TagDataType.Int32 => DataTypeIds.Int32,
            TagDataType.String => DataTypeIds.String,

            // V2-14: V es real (float de 4 bytes) y se publica como Float, no
            // como Double, para no inventar precision que el dato no tiene.
            TagDataType.Float => DataTypeIds.Float,
            _ => throw new InvalidOperationException(
                $"Tipo de dato no soportado para '{tagName}': {dataType}")
        };

        return new BaseDataVariableState(parent)
        {
            NodeId = new NodeId(tagName, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.HasComponent,
            DataType = uaDataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = state.ScaledValue,
            StatusCode = QualityMapper.ToStatusCode(state.Quality),
            Timestamp = state.SourceTimestamp
        };
    }

    /// Registra la referencia inversa desde un nodo que no es nuestro.
    private static void LinkToParent(IDictionary<NodeId, IList<IReference>> externalReferences,
        NodeId parentId, NodeId childId, NodeId referenceType)
    {
        if (!externalReferences.TryGetValue(parentId, out IList<IReference>? refs))
        {
            externalReferences[parentId] = refs = new List<IReference>();
        }
        refs.Add(new NodeStateReference(referenceType, false, childId));
    }
}
