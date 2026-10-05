using Gateway.Core;

namespace Gateway.Tests;

public class TagDiagnosticsQueryTests
{
    private static readonly DateTime T1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ValorFloat_SeFormateaConPuntoAunqueLaCulturaSeaEsAr()
    {
        // V2-14: los tags SQL publican float. El formateo tenia caso para double
        // pero no para float, y el float caia en un ToString() con la cultura de
        // la maquina: la tabla de detalle mostraba "1,5" para SQL y "1.5" para DA.
        var cache = new TagCache([new TagDefinition(
            "PLANTA_01.DESDE_SQL", TagSource.Sql, "TIC101.PV", TagDataType.Float, 1.0, 0.0, StaleAfter: null)]);
        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TIC101.PV"] = new TagSample(1.5f, TagQuality.Good, T1)
        });

        using var _ = new CultureScope("es-AR");
        var row = Assert.Single(TagDiagnosticsQuery.Query(cache, onlyDegraded: false).Rows);

        Assert.Equal("1.5", row.ScaledValue);
    }
}
