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
}
