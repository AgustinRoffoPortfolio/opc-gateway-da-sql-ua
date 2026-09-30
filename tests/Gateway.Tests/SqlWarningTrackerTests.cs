using Gateway.Sql;

namespace Gateway.Tests;

public class SqlWarningTrackerTests
{
    private static readonly IReadOnlyDictionary<string, SqlRowAnomaly> SinAnomalias =
        new Dictionary<string, SqlRowAnomaly>();

    private static SqlWarningTracker Tracker(params string[] declarados) =>
        new(declarados, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, SqlRowAnomaly> Anomalias(params (string Tag, SqlRowAnomaly A)[] filas) =>
        filas.ToDictionary(f => f.Tag, f => f.A);

    [Fact]
    public void PrimerCicloSano_UnSoloInfDeArranque()
    {
        var tracker = Tracker("A", "B");

        var avisos = tracker.Evaluate([], SinAnomalias, totalRows: 2);

        var aviso = Assert.Single(avisos);
        Assert.Equal(SqlWarningLevel.Information, aviso.Level);
        Assert.Contains("sin filas anomalas", aviso.Message);
    }

    [Fact]
    public void CicloSanoRepetido_NoVuelveAAvisar()
    {
        var tracker = Tracker("A");
        tracker.Evaluate([], SinAnomalias, 1);

        Assert.Empty(tracker.Evaluate([], SinAnomalias, 1));
    }

    [Fact]
    public void AnomaliaEnTagNoDeclarado_NoGeneraWarning()
    {
        // B6.5: un NULL en un tag que el CSV no pide no llega a ningun cliente.
        var tracker = Tracker("A");

        var avisos = tracker.Evaluate([], Anomalias(("OTRO", SqlRowAnomaly.NullValue)), 2);

        Assert.DoesNotContain(avisos, a => a.Level == SqlWarningLevel.Warning);
    }

    [Fact]
    public void AnomaliaEnTagDeclarado_WarningConNombreYMotivo()
    {
        var tracker = Tracker("A");

        var avisos = tracker.Evaluate([], Anomalias(("a", SqlRowAnomaly.NullValue)), 1);

        var aviso = Assert.Single(avisos);
        Assert.Equal(SqlWarningLevel.Warning, aviso.Level);
        Assert.Contains("a (sin valor)", aviso.Message);
    }

    [Fact]
    public void MismaAnomalia_SeAvisaUnaSolaVez()
    {
        var tracker = Tracker("A");
        var anomalias = Anomalias(("A", SqlRowAnomaly.NullValue));
        tracker.Evaluate([], anomalias, 1);

        Assert.Empty(tracker.Evaluate([], anomalias, 1));
    }

    [Fact]
    public void CambioDeFlagsEnElMismoTag_VuelveAAvisar()
    {
        var tracker = Tracker("A");
        tracker.Evaluate([], Anomalias(("A", SqlRowAnomaly.NullValue)), 1);

        var avisos = tracker.Evaluate([], Anomalias(("A", SqlRowAnomaly.NullQuality)), 1);

        var aviso = Assert.Single(avisos);
        Assert.Equal(SqlWarningLevel.Warning, aviso.Level);
        Assert.Contains("A (sin calidad)", aviso.Message);
    }

    [Fact]
    public void AnomaliasQueSeResuelven_UnSoloInf()
    {
        var tracker = Tracker("A");
        tracker.Evaluate([], Anomalias(("A", SqlRowAnomaly.NullValue)), 1);

        var avisos = tracker.Evaluate([], SinAnomalias, 1);
        Assert.Equal(SqlWarningLevel.Information, Assert.Single(avisos).Level);

        Assert.Empty(tracker.Evaluate([], SinAnomalias, 1));
    }

    [Fact]
    public void TagAusente_SeAvisaUnaSolaVezAunqueSeRepitaElCiclo()
    {
        // B4.3: la misma instancia sobrevive a la reconexion, asi que
        // repetir el ciclo es lo mismo que reconectar y volver a leer.
        var tracker = Tracker("A", "B");
        tracker.Evaluate([], SinAnomalias, 1);

        var avisos = tracker.Evaluate(["B"], SinAnomalias, 1);
        var aviso = Assert.Single(avisos);
        Assert.Equal(SqlWarningLevel.Warning, aviso.Level);
        Assert.Contains("B", aviso.Message);

        Assert.Empty(tracker.Evaluate(["B"], SinAnomalias, 1));
        Assert.Empty(tracker.Evaluate(["b"], SinAnomalias, 1));
    }

    [Fact]
    public void CambioDelConjuntoDeAusentes_VuelveAAvisar()
    {
        var tracker = Tracker("A", "B");
        tracker.Evaluate(["A"], SinAnomalias, 1);

        var avisos = tracker.Evaluate(["A", "B"], SinAnomalias, 0);

        Assert.Contains(avisos, a => a.Level == SqlWarningLevel.Warning && a.Message.Contains("A, B"));
    }

    [Fact]
    public void AusentesQueVuelven_UnSoloInf()
    {
        var tracker = Tracker("A");
        tracker.Evaluate(["A"], SinAnomalias, 0);

        var avisos = tracker.Evaluate([], SinAnomalias, 1);
        Assert.Contains(avisos, a => a.Level == SqlWarningLevel.Information && a.Message.Contains("todos los tags"));

        Assert.Empty(tracker.Evaluate([], SinAnomalias, 1));
    }

    [Fact]
    public void PrimerCicloSinAusentes_NoInformaNada()
    {
        // El INF de "vuelven todos" solo cierra un aviso anterior.
        var tracker = Tracker("A");

        var avisos = tracker.Evaluate([], SinAnomalias, 1);

        Assert.DoesNotContain(avisos, a => a.Message.Contains("todos los tags"));
    }

    [Fact]
    public void MasDeDiezTags_NombraDiezYResumeElResto()
    {
        var declarados = Enumerable.Range(1, 13).Select(i => $"T{i:00}").ToArray();
        var tracker = Tracker(declarados);

        var avisos = tracker.Evaluate(declarados, SinAnomalias, 0);

        var aviso = Assert.Single(avisos, a => a.Level == SqlWarningLevel.Warning);
        Assert.Contains("T10", aviso.Message);
        Assert.DoesNotContain("T11", aviso.Message);
        Assert.Contains("y 3 mas", aviso.Message);
    }
}
