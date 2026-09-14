using System.Reflection;
using Gateway.Core;
using Gateway.Ua;
using Opc.Ua;
using Opc.Ua.Server;

namespace Gateway.Tests;

/// <summary>
/// Arma un GatewayNodeManager real sin levantar un servidor OPC UA de verdad
/// detras. El constructor de CustomNodeManager2 pide un IServerInternal
/// completo, pero construir el arbol de nodos (CreateAddressSpace) y publicar
/// valores (UpdateValues) solo tocan un punado de sus miembros: las tablas de
/// namespaces y de tipos, la factory de tipos codificables y el contexto de
/// sistema por default. El resto de la interfaz (sesiones, suscripciones,
/// diagnostico del stack) no hace falta para probar como se arma el arbol, asi
/// que se cubre con un stub minimo en vez de levantar un servidor con
/// certificados y sockets, que es como se verifica manualmente contra UaExpert
/// (ver docs/verificacion.md).
/// </summary>
internal static class GatewayNodeManagerTestHarness
{
    private const string NamespaceUri = "urn:gateway-tests";

    public static (GatewayNodeManager Manager, NamespaceTable Namespaces) Build(
        IReadOnlyList<TagDefinition> tags, TagCache cache)
    {
        var namespaces = new NamespaceTable();
        var typeTree = new TypeTable(namespaces);

        var proxyObj = DispatchProxy.Create<IServerInternal, ServerStub>();
        var stub = (ServerStub)proxyObj;
        stub.Namespaces = namespaces;
        stub.ServerUris = new StringTable();
        stub.TypeTreeInstance = typeTree;
        stub.Factory = EncodeableFactory.Create();

        var server = (IServerInternal)proxyObj;
        stub.SystemCtx = new ServerSystemContext(server);

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "Gateway.Tests",
            ApplicationUri = NamespaceUri,
            ServerConfiguration = new ServerConfiguration()
        };

        var manager = new GatewayNodeManager(server, configuration, NamespaceUri, tags, cache);
        manager.CreateAddressSpace(new Dictionary<NodeId, IList<IReference>>());

        return (manager, namespaces);
    }

    /// <summary>NodeId de un tag publicado, en el namespace que usa este harness.</summary>
    public static NodeId NodeIdFor(NamespaceTable namespaces, string tagName) =>
        new(tagName, (ushort)namespaces.GetIndex(NamespaceUri));

    /// <summary>
    /// Solo responde lo que CreateAddressSpace y UpdateValues llegan a pedirle
    /// a IServerInternal. Cualquier miembro no previsto tira una excepcion con
    /// su nombre en vez de devolver un valor por default: si un cambio futuro
    /// en GatewayNodeManager empieza a necesitar algo mas, el test tiene que
    /// fallar explicando que, no arrastrar un null silencioso.
    /// </summary>
    private class ServerStub : DispatchProxy
    {
        public NamespaceTable Namespaces = null!;
        public StringTable ServerUris = null!;
        public TypeTable TypeTreeInstance = null!;
        public IEncodeableFactory Factory = null!;
        public ServerSystemContext SystemCtx = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name switch
            {
                "get_NamespaceUris" => Namespaces,
                "get_ServerUris" => ServerUris,
                "get_TypeTree" => TypeTreeInstance,
                "get_Factory" => Factory,
                "get_DefaultSystemContext" => SystemCtx,
                // Unico miembro que se devuelve en null: el SDK lo pide al
                // construir el contexto y no lo usa para armar el arbol. Si
                // alguna vez importa, va a fallar aca y no en silencio.
                "get_Telemetry" => null,
                var name => throw new NotSupportedException(
                    $"GatewayNodeManagerTestHarness no cubre '{name}': el stub necesita un caso mas.")
            };
    }
}
