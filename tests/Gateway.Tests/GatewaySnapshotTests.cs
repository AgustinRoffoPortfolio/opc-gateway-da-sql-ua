using Gateway.Core;

namespace Gateway.Tests;

public class GatewaySnapshotTests
{
    private static readonly DateTime T1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    private static string UaName(int i) => $"PLANTA_01.MEDICION.TAG_{i:D3}";
    private static string DaName(int i) => $"Sim.Tag{i:D3}";

    private static TagDefinition Def(int i) =>
        new(UaName(i), TagSource.OpcDa, DaName(i), TagDataType.Double, 1.0, 0.0,
            StaleAfter: TimeSpan.FromHours(1));

    // Ventana larga: estos tests controlan la calidad a mano, no miden degradacion.
    private static TagCache CacheWith(int count) =>
        new(Enumerable.Range(0, count).Select(Def));

    /// Vinculo sano y sin actividad relevante, para que el diagnostico dependa
    /// solo de los contadores de tags.
    private static DaLinkStatus Link(LinkState state = LinkState.Connected) =>
        new(state, T1, 0, null, 10, 0, 1, 0, 5.0, 5.0, 9.0, 1000, T1);

    private static UaServerStatus Ua() => new(1, 0);

    private static GatewaySnapshot Snap(TagCache cache, LinkState state = LinkState.Connected) =>
        GatewaySnapshot.Build(cache, Link(state), Ua(), T1, UaAuditSnapshot.Empty);

    private static void Push(TagCache cache, int i, object? value, TagQuality quality) =>
        cache.Update(TagSource.OpcDa,new Dictionary<string, TagSample>
        {
            [DaName(i)] = new TagSample(value, quality, T1)
        });

    [Fact]
    public void TodosRespondiendo_DiagnosticaSano()
    {
        var cache = CacheWith(10);
        for (var i = 0; i < 10; i++)
            Push(cache, i, 1.0, TagQuality.Good);

        var snapshot = Snap(cache);

        Assert.Equal(10, snapshot.Counters.Good);
        Assert.Equal(0, snapshot.Counters.SilentTotal);
        Assert.Equal(Diagnosis.Healthy, snapshot.Diagnosis);
    }

    [Fact]
    public void TagsQueNuncaContestaron_ApuntanAlCsv()
    {
        var cache = CacheWith(10);
        for (var i = 0; i < 10; i++)
            Push(cache, i, null, TagQuality.ItemRejected);

        var snapshot = Snap(cache);

        Assert.Equal(10, snapshot.Counters.SilentNeverAnswered);
        Assert.Equal(0, snapshot.Counters.SilentPreviouslyAnswered);
        Assert.Equal(Diagnosis.LikelyCsvMismatch, snapshot.Diagnosis);
    }

    [Fact]
    public void TagsQueContestaronYSeCallaron_ApuntanAlServidorRepoblado()
    {
        var cache = CacheWith(10);

        // Primero entregan dato: eso deja ScaledValue poblado para siempre.
        for (var i = 0; i < 10; i++)
            Push(cache, i, 1.0, TagQuality.Good);

        // Despues el servidor DA vuelve sin sus items.
        for (var i = 0; i < 10; i++)
            Push(cache, i, null, TagQuality.ItemRejected);

        var snapshot = Snap(cache);

        Assert.Equal(0, snapshot.Counters.SilentNeverAnswered);
        Assert.Equal(10, snapshot.Counters.SilentPreviouslyAnswered);
        Assert.Equal(Diagnosis.DaServerRepopulatedEmpty, snapshot.Diagnosis);
    }

    /// El bug que motivo separar "mudo" de "mala calidad": un sensor fuera de
    /// rango contesta perfecto, solo que con calidad fea. Contarlo como mudo
    /// diagnosticaria una caida donde solo hay ruido de proceso.
    [Fact]
    public void UncertainDelServidor_NoCuentaComoMudo()
    {
        var cache = CacheWith(10);
        var sensorNotAccurate = new TagQuality(
            QualityMaster.Uncertain,
            QualitySubstatus.UncertainSensorNotAccurate,
            QualityLimit.NotLimited);

        for (var i = 0; i < 10; i++)
            Push(cache, i, 1.0, sensorNotAccurate);

        var snapshot = Snap(cache);

        Assert.Equal(10, snapshot.Counters.Uncertain);
        Assert.Equal(0, snapshot.Counters.SilentTotal);
        Assert.Equal(Diagnosis.Healthy, snapshot.Diagnosis);
    }

    [Fact]
    public void PocosTagsMudos_NoAfirmaCausaGlobal()
    {
        var cache = CacheWith(100);
        for (var i = 0; i < 100; i++)
            Push(cache, i, 1.0, TagQuality.Good);

        // 2 de 100: por debajo del umbral, no alcanza para culpar al servidor.
        Push(cache, 0, null, TagQuality.ItemRejected);
        Push(cache, 1, null, TagQuality.ItemRejected);

        var snapshot = Snap(cache);

        Assert.Equal(2, snapshot.Counters.SilentTotal);
        Assert.Equal(Diagnosis.PartialDegradation, snapshot.Diagnosis);
    }

    [Fact]
    public void BucketsParejos_NoElijePorElOperador()
    {
        var cache = CacheWith(20);

        // La mitad contesta antes de callarse; la otra mitad nunca contesto.
        for (var i = 0; i < 10; i++)
            Push(cache, i, 1.0, TagQuality.Good);
        for (var i = 0; i < 20; i++)
            Push(cache, i, null, TagQuality.ItemRejected);

        var snapshot = Snap(cache);

        Assert.Equal(10, snapshot.Counters.SilentNeverAnswered);
        Assert.Equal(10, snapshot.Counters.SilentPreviouslyAnswered);
        Assert.Equal(Diagnosis.Indeterminate, snapshot.Diagnosis);
    }

    /// El vinculo manda sobre los contadores: sin nadie del otro lado no tiene
    /// sentido preguntarse por el CSV.
    [Fact]
    public void VinculoCaido_MandaSobreElConteoDeTags()
    {
        var cache = CacheWith(10);
        for (var i = 0; i < 10; i++)
            Push(cache, i, null, TagQuality.ItemRejected);

        Assert.Equal(Diagnosis.DaLinkDown, Snap(cache, LinkState.Disconnected).Diagnosis);
        Assert.Equal(Diagnosis.DaServerStalled, Snap(cache, LinkState.Stalled).Diagnosis);
    }

    /// Al arrancar, todos los tags estan sin leer. Si contaran como mudos, cada
    /// inicio del gateway diagnosticaria una falla.
    [Fact]
    public void AlArrancar_NoDiagnosticaFalla()
    {
        var snapshot = Snap(CacheWith(10));

        Assert.Equal(10, snapshot.Counters.WaitingForInitialData);
        Assert.Equal(0, snapshot.Counters.SilentTotal);
        Assert.Equal(Diagnosis.Healthy, snapshot.Diagnosis);
    }
}