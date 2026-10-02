using Gateway.Core;
using Opc.Ua;

namespace Gateway.Tests;

/// <summary>
/// Cubre lo que GatewayNodeManagerTestHarness hace posible probar sin un
/// servidor OPC UA real: el tipo de dato publicado y que el origen del tag
/// (OPCDA o SQL) no cambie como se ve el nodo desde el cliente.
/// </summary>
public class GatewayNodeManagerTests
{
    private static readonly DateTime T1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TagFloat_SePublicaComoFloatUa_SinPerdidaMasAllaDeLaDelFloat()
    {
        // V2-14: 8009.57 en la columna V (real de SQL Server) tiene que llegar
        // al nodo UA como el mismo float de 4 bytes, no como el double que
        // inventaria digitos que la medicion no tiene.
        var definitions = new List<TagDefinition>
        {
            new("Planta.Presion", TagSource.Sql, "F1", TagDataType.Float, 1.0, 0.0)
        };
        var cache = new TagCache(definitions);
        var (manager, namespaces) = GatewayNodeManagerTestHarness.Build(definitions, cache);

        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["F1"] = new TagSample(8009.57, TagQuality.Good, T1)
        });
        manager.UpdateValues();

        var node = manager.FindPredefinedNode<Opc.Ua.BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Planta.Presion"));

        Assert.NotNull(node);
        Assert.Equal(DataTypeIds.Float, node!.DataType);
        var value = Assert.IsType<float>(node.Value);
        Assert.Equal((float)8009.57, value);
    }

    [Fact]
    public void TagSql_ProduceNodo_ConMismoStatusCodeYTimestampQueUnTagDa()
    {
        // El origen no tiene que cambiar como se ve el nodo: mismo StatusCode,
        // mismo SourceTimestamp, para el mismo valor y la misma calidad.
        var definitions = new List<TagDefinition>
        {
            new("Planta.DesdeSql", TagSource.Sql, "TagSql", TagDataType.Float, 1.0, 0.0),
            new("Planta.DesdeDa", TagSource.OpcDa, "TagDa", TagDataType.Double, 1.0, 0.0)
        };
        var cache = new TagCache(definitions);
        var (manager, namespaces) = GatewayNodeManagerTestHarness.Build(definitions, cache);

        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TagSql"] = new TagSample(42.0, TagQuality.Good, T1)
        });
        cache.Update(TagSource.OpcDa, new Dictionary<string, TagSample>
        {
            ["TagDa"] = new TagSample(42.0, TagQuality.Good, T1)
        });
        manager.UpdateValues();

        var sqlNode = manager.FindPredefinedNode<Opc.Ua.BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Planta.DesdeSql"));
        var daNode = manager.FindPredefinedNode<Opc.Ua.BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Planta.DesdeDa"));

        Assert.NotNull(sqlNode);
        // El SQL es Float y el DA Double (V2-24): lo que tiene que coincidir es
        // la calidad y el timestamp, no el tipo CLR del valor.
        Assert.Equal(42f, Assert.IsType<float>(sqlNode!.Value));
        Assert.Equal(daNode!.StatusCode, sqlNode.StatusCode);
        Assert.Equal(daNode.Timestamp, sqlNode.Timestamp);
        Assert.Equal(T1, sqlNode.Timestamp);
    }

    [Fact]
    public void JerarquiaDeNombreConPuntos_EsIgualParaLasDosFuentes()
    {
        // El arbol sale de los puntos de TAG_NAME_OPC_UA, no del origen: dos
        // tags que comparten prefijo tienen que compartir la misma carpeta
        // intermedia aunque vengan de fuentes distintas.
        var definitions = new List<TagDefinition>
        {
            new("Planta.Grupo.DesdeDa", TagSource.OpcDa, "ItemA", TagDataType.Double, 1.0, 0.0),
            new("Planta.Grupo.DesdeSql", TagSource.Sql, "TagB", TagDataType.Float, 1.0, 0.0)
        };
        var cache = new TagCache(definitions);
        var (manager, namespaces) = GatewayNodeManagerTestHarness.Build(definitions, cache);

        var daNode = manager.FindPredefinedNode<Opc.Ua.BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Planta.Grupo.DesdeDa"));
        var sqlNode = manager.FindPredefinedNode<Opc.Ua.BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Planta.Grupo.DesdeSql"));

        Assert.NotNull(daNode);
        Assert.NotNull(sqlNode);
        Assert.Same(daNode!.Parent, sqlNode!.Parent);
    }

    [Fact]
    public void TagSqlMarcado_SePublicaUncertainLastUsableValueConSuTimestamp()
    {
        // El caso delicado de V2-32: la marca cambia solo la calidad. Valor y
        // SourceTimestamp quedan iguales, y el cliente suscripto se tiene que
        // enterar igual.
        var definitions = new List<TagDefinition>
        {
            new("Planta.DesdeSql", TagSource.Sql, "TagSql", TagDataType.Double, 1.0, 0.0)
        };
        var cache = new TagCache(definitions);
        var (manager, namespaces) = GatewayNodeManagerTestHarness.Build(definitions, cache);

        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TagSql"] = new TagSample(42.0, TagQuality.Good, T1)
        });
        manager.UpdateValues();

        var node = manager.FindPredefinedNode<BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Planta.DesdeSql"));
        Assert.NotNull(node);
        var before = new DataValue(new Variant(node!.Value), node.StatusCode, node.Timestamp);

        var notified = NodeStateChangeMasks.None;
        node.StateChanged += (_, _, masks) => notified |= masks;

        cache.MarkSourceDown(TagSource.Sql);
        manager.UpdateValues();

        var after = new DataValue(new Variant(node.Value), node.StatusCode, node.Timestamp);

        Assert.Equal(42.0, after.Value);
        Assert.Equal(before.Value, after.Value);
        Assert.Equal(T1, after.SourceTimestamp);
        Assert.Equal(StatusCodes.Good, before.StatusCode.Code);
        Assert.Equal(StatusCodes.UncertainLastUsableValue, after.StatusCode.Code);

        // Las dos mitades del camino hacia el cliente suscripto: el nodo avisa
        // un cambio de Value (el setter de StatusCode lo levanta aunque el valor
        // no cambie), y el filtro del MonitoredItem, con el trigger por defecto
        // StatusValue, lo deja pasar porque cambio el status.
        Assert.True(notified.HasFlag(NodeStateChangeMasks.Value));
        Assert.True(Opc.Ua.Server.MonitoredItem.ValueChanged(
            after, null, before, null, null, 0));
    }

    [Fact]
    public void FuenteSqlInactiva_PublicaDisconnectedYConfiguracionInvalidaEnGood()
    {
        var definitions = new List<TagDefinition>
        {
            new("Planta.DesdeSql", TagSource.Sql, "TagSql", TagDataType.Double, 1.0, 0.0)
        };
        var cache = new TagCache(definitions);
        var (manager, namespaces) = GatewayNodeManagerTestHarness.Build(definitions, cache);

        manager.PublishInactiveSources([new InactiveSource(TagSource.Sql, "motivo para la pagina")]);

        var linkState = manager.FindPredefinedNode<BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Gateway.Sql.LinkState"));
        var lastError = manager.FindPredefinedNode<BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Gateway.Sql.LastError"));
        var readCycles = manager.FindPredefinedNode<BaseDataVariableState>(
            GatewayNodeManagerTestHarness.NodeIdFor(namespaces, "Gateway.Sql.ReadCycles"));

        Assert.NotNull(linkState);
        Assert.NotNull(lastError);
        Assert.Equal("Disconnected", linkState!.Value);
        Assert.Equal("Configuracion invalida", lastError!.Value);
        // Diagnostico siempre en Good: la falla va en el contenido.
        Assert.Equal(StatusCodes.Good, linkState.StatusCode.Code);
        Assert.Equal(StatusCodes.Good, lastError.StatusCode.Code);
        // El resto de la rama queda como estaba: no hay ciclos que contar.
        Assert.Null(readCycles!.Value);
    }
}
