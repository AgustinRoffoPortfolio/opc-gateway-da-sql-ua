using Gateway.Core;

namespace Gateway.Tests;

public class TagCacheTests
{
    private static readonly DateTime T1 = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 8, 12, 10, 5, 0, DateTimeKind.Utc);

    private static TagDefinition Def(
        TagDataType type = TagDataType.Double,
        double multiplier = 1.0,
        double offset = 0.0) =>
        new("PLANTA_01.MEDICION.PRESION_ENTRADA", TagSource.OpcDa, "Random.Real8", type, multiplier, offset);

    // Ventana de antiguedad larga a proposito: estos tests miden transformacion
    // y calidad, no degradacion por tiempo. La degradacion tiene sus propios tests.
    private static TagCache CacheWith(TagDefinition definition) =>
        new([definition with { StaleAfter = TimeSpan.FromHours(1) }]);

    private static Dictionary<string, TagSample> Sample(object? value, TagQuality quality, DateTime timestamp) =>
        new() { ["Random.Real8"] = new TagSample(value, quality, timestamp) };

    // --- Indice por par (origen, tag de origen): V2-12 y V2-17 ---

    private static Dictionary<string, TagSample> SampleOf(string sourceTag, double value) =>
        new() { [sourceTag] = new TagSample(value, TagQuality.Good, T1) };

    private static TagDefinition DefOf(string uaName, TagSource source, string sourceTag) =>
        new(uaName, source, sourceTag, TagDataType.Double, 1.0, 0.0, StaleAfter: TimeSpan.FromHours(1));

    [Fact]
    public void MismoNombreEnDosFuentes_NoSePisan()
    {
        // El caso que justifica la clave compuesta: los dos origenes vienen del
        // mismo mundo y pueden usar el mismo nombre para tags distintos.
        var cache = new TagCache([
            DefOf("PLANTA_01.DESDE_DA", TagSource.OpcDa, "TIC101.PV"),
            DefOf("PLANTA_01.DESDE_SQL", TagSource.Sql, "TIC101.PV")]);

        cache.Update(TagSource.OpcDa, SampleOf("TIC101.PV", 11.0));

        Assert.Equal(11.0, cache.Get("PLANTA_01.DESDE_DA").ScaledValue);
        // El tag SQL sigue sin dato: la muestra DA no lo toco.
        Assert.Equal(TagQuality.WaitingForInitialData, cache.Get("PLANTA_01.DESDE_SQL").Quality);
    }

    [Fact]
    public void SourceTags_DevuelveSoloLosDeLaFuentePedida()
    {
        // Si los nombres SQL entraran en el alta de items DA, el servidor legado
        // los rechazaria y quedarian reintentandose para siempre.
        var cache = new TagCache([
            DefOf("PLANTA_01.A", TagSource.OpcDa, "Random.Real8"),
            DefOf("PLANTA_01.B", TagSource.Sql, "TIC101.PV")]);

        Assert.Equal(["Random.Real8"], cache.SourceTags(TagSource.OpcDa));
        Assert.Equal(["TIC101.PV"], cache.SourceTags(TagSource.Sql));
    }

    [Fact]
    public void NombreSql_SeCruzaSinDistinguirMayusculas()
    {
        // SQL Server no distingue mayusculas y TAG es clave primaria, asi que
        // la tabla no puede tener las dos formas. El CSV si puede diferir.
        var cache = new TagCache([DefOf("PLANTA_01.DESDE_SQL", TagSource.Sql, "tic101.pv")]);

        cache.Update(TagSource.Sql, SampleOf("TIC101.PV", 42.0));

        Assert.Equal(42.0, cache.Get("PLANTA_01.DESDE_SQL").ScaledValue);
    }

    [Fact]
    public void NombreDa_SiDistingueMayusculas()
    {
        // OPC DA si distingue: dos ItemID que difieren en una mayuscula son dos
        // items distintos, y colapsarlos seria un error peor que el que se evita
        // del lado SQL.
        var cache = new TagCache([DefOf("PLANTA_01.DESDE_DA", TagSource.OpcDa, "random.real8")]);

        cache.Update(TagSource.OpcDa, SampleOf("Random.Real8", 42.0));

        Assert.Equal(TagQuality.WaitingForInitialData, cache.Get("PLANTA_01.DESDE_DA").Quality);
    }

    [Fact]
    public void TagSinLeer_QuedaEsperandoDatoInicial()
    {
        var cache = CacheWith(Def());

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");

        Assert.Equal(TagQuality.WaitingForInitialData, state.Quality);
        Assert.Null(state.ScaledValue);
    }

    [Fact]
    public void TagFueraDelCsv_SeDistingueDeUnoSinLeer()
    {
        var cache = CacheWith(Def());

        var state = cache.Get("PLANTA_01.NO.EXISTE");

        Assert.Equal(TagQuality.UnknownTag, state.Quality);
    }

    [Fact]
    public void CalidadBuena_AplicaMultiplicadorYOffset()
    {
        var cache = CacheWith(Def(multiplier: 2.0, offset: 10.0));

        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(20.0, state.ScaledValue);
        Assert.Equal(T1, state.SourceTimestamp);
    }

    [Fact]
    public void CalidadMala_ConservaValorYTimestampAnteriores()
    {
        var cache = CacheWith(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        // Llega una lectura mala con un valor distinto: no debe pisar nada.
        var bad = new TagQuality(QualityMaster.Bad, QualitySubstatus.BadOutOfService, QualityLimit.NotLimited);
        cache.Update(TagSource.OpcDa, Sample(999.0, bad, T2));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(10.0, state.ScaledValue);      // el valor viejo, escalado
        Assert.Equal(T1, state.SourceTimestamp);    // el timestamp viejo
        Assert.Equal(bad, state.Quality);           // pero la calidad nueva
    }

    [Fact]
    public void CalidadUncertain_SeEscalaIgual()
    {
        var cache = CacheWith(Def(multiplier: 2.0));
        var uncertain = new TagQuality(
            QualityMaster.Uncertain, QualitySubstatus.UncertainLastUsableValue, QualityLimit.NotLimited);

        cache.Update(TagSource.OpcDa, Sample(5.0, uncertain, T1));

        Assert.Equal(10.0, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    [Fact]
    public void ValorQueNoConvierte_MarcaErrorDeConversion()
    {
        var cache = CacheWith(Def(TagDataType.Boolean));

        cache.Update(TagSource.OpcDa, Sample("no soy un booleano", TagQuality.Good, T1));

        Assert.Equal(TagQuality.ConversionError, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").Quality);
    }

    [Fact]
    public void ValorNumericoComoTexto_ParseaConCulturaInvariante()
    {
        var cache = CacheWith(Def(multiplier: 1.0));

        cache.Update(TagSource.OpcDa, Sample("8009.57", TagQuality.Good, T1));

        Assert.Equal(8009.57, (double)cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue!, 2);
    }

    [Fact]
    public void Int32_RedondeaEnVezDeTruncar()
    {
        var cache = CacheWith(Def(TagDataType.Int32, multiplier: 1.0, offset: 0.6));

        cache.Update(TagSource.OpcDa, Sample(10.0, TagQuality.Good, T1));

        Assert.Equal(11, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    [Fact]
    public void MuestraDeUnTagQueNoPedimos_SeIgnora()
    {
        var cache = CacheWith(Def());

        cache.Update(TagSource.OpcDa, new Dictionary<string, TagSample>
        {
            ["Random.Int4"] = new TagSample(1.0, TagQuality.Good, T1)
        });

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void UnItemDaPuedeAlimentarVariosNodosUa()
    {
        // Mismo tag DA expuesto dos veces con transformaciones distintas: es el
        // caso de la misma medicion en dos unidades de ingenieria.
        var enBar = new TagDefinition("PLANTA_01.PRESION_BAR", TagSource.OpcDa, "Random.Real8", TagDataType.Double, 1.0, 0.0);
        var enKgCm2 = new TagDefinition("PLANTA_01.PRESION_KGCM2", TagSource.OpcDa, "Random.Real8", TagDataType.Double, 1.02, 0.0);

        var stale = TimeSpan.FromHours(1);
        var cache = new TagCache([enBar with { StaleAfter = stale }, enKgCm2 with { StaleAfter = stale }]);
        cache.Update(TagSource.OpcDa, Sample(100.0, TagQuality.Good, T1));

        Assert.Equal(100.0, cache.Get("PLANTA_01.PRESION_BAR").ScaledValue);
        Assert.Equal(102.0, (double)cache.Get("PLANTA_01.PRESION_KGCM2").ScaledValue!, 2);
    }

    // --- Rechazos de items en el reenganche --------------------------------
    // Un item rechazado entra a la cache como TagSample.NoData: sin valor y con
    // timestamp fresco. La primera vez es una duda y no tiene que pisar lo que
    // el tag ya sabia; confirmada en el reintento, si.

    [Fact]
    public void NotConnectedSobreTagConValor_NoPisaNiReiniciaLaAntiguedad()
    {
        // Rechazo transitorio en el reenganche: el tag no tiene que perder lo que
        // ya sabia, y tiene que seguir envejeciendo hacia LastUsableValue.
        var cache = CacheQueEnvejeceRapido(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        cache.Update(TagSource.OpcDa, new Dictionary<string, TagSample>
        {
            ["Random.Real8"] = TagSample.NoData(TagQuality.NotConnected)
        });

        EsperarAQueEnvejezca();

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(TagQuality.LastUsableValue, state.Quality);
        Assert.Equal(10.0, state.ScaledValue);
        Assert.Equal(T1, state.SourceTimestamp);
    }

    [Fact]
    public void ItemRejectedSobreTagConValor_SiPisaLaCalidad()
    {
        // Rechazo confirmado en el reintento: ya no es una duda sino un error de
        // configuracion, y se publica como tal aunque cueste el valor.
        var cache = CacheWith(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        cache.Update(TagSource.OpcDa, new Dictionary<string, TagSample>
        {
            ["Random.Real8"] = TagSample.NoData(TagQuality.ItemRejected)
        });

        Assert.Equal(TagQuality.ItemRejected,
            cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").Quality);
    }


    // --- Degradacion por antiguedad ---------------------------------------
    // La ventana es de milisegundos y se espera con Sleep porque Degrade lee el
    // reloj por dentro. Es el precio de no tener el tiempo inyectado todavia.

    private static TagCache CacheQueEnvejeceRapido(TagDefinition definition) =>
        new([definition with { StaleAfter = TimeSpan.FromMilliseconds(30) }]);

    // Sin umbral: la calidad la manda la fuente y el reloj no opina (V2-11).
    // Es como van a entrar los tags SQL, donde la columna Q ya dice si el dato
    // sirve, y donde 30 s entre polling y polling serian "viejo" casi siempre.
    private static TagCache CacheQueNuncaEnvejece(TagDefinition definition) =>
        new([definition with { StaleAfter = null }]);

    private static void EsperarAQueEnvejezca() => Thread.Sleep(120);

    [Fact]
    public void TagFresco_NoDegrada()
    {
        var cache = CacheQueEnvejeceRapido(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(TagQuality.Good, state.Quality);
    }

    [Fact]
    public void TagViejoConValorBueno_PasaALastUsableValue()
    {
        var cache = CacheQueEnvejeceRapido(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        EsperarAQueEnvejezca();

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(TagQuality.LastUsableValue, state.Quality);
        Assert.Equal(10.0, state.ScaledValue);      // el valor no se toca
        Assert.Equal(T1, state.SourceTimestamp);    // el timestamp tampoco
    }

    [Fact]
    public void TagSinUmbral_NoDegradaAunqueEnvejezca()
    {
        var cache = CacheQueNuncaEnvejece(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        EsperarAQueEnvejezca();

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(TagQuality.Good, state.Quality);
        Assert.Equal(10.0, state.ScaledValue);
        Assert.Equal(T1, state.SourceTimestamp);
    }

    [Fact]
    public void TagSinUmbralYSinDato_SigueEsperandoDatoInicial()
    {
        // Un tag SQL puede tardar hasta un ciclo de polling entero en recibir su
        // primera muestra. Durante esa espera sigue siendo "todavia no llego",
        // no "no hay nadie del otro lado".
        var cache = CacheQueNuncaEnvejece(Def());

        EsperarAQueEnvejezca();

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(TagQuality.WaitingForInitialData, state.Quality);
    }

    [Fact]
    public void TagViejoSinDato_PasaANotConnected()
    {
        var cache = CacheQueEnvejeceRapido(Def());

        EsperarAQueEnvejezca();

        Assert.Equal(TagQuality.NotConnected,
            cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").Quality);
    }

    [Fact]
    public void TagViejoYaMalo_NoMejoraSuCalidad()
    {
        var cache = CacheQueEnvejeceRapido(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        var bad = new TagQuality(QualityMaster.Bad, QualitySubstatus.BadDeviceFailure, QualityLimit.NotLimited);
        cache.Update(TagSource.OpcDa, Sample(999.0, bad, T2));

        EsperarAQueEnvejezca();

        // Degradar nunca mejora: la causa concreta se conserva, no pasa a Uncertain.
        Assert.Equal(bad, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").Quality);
    }

    [Fact]
    public void DegradacionNoPisaElEstadoGuardado()
    {
        // Se lee degradado, pero cuando el DA vuelve la muestra nueva se compara
        // contra la ultima calidad real, no contra la degradacion inventada.
        var cache = CacheQueEnvejeceRapido(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        EsperarAQueEnvejezca();
        Assert.Equal(TagQuality.LastUsableValue, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").Quality);

        cache.Update(TagSource.OpcDa, Sample(7.0, TagQuality.Good, T2));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(TagQuality.Good, state.Quality);
        Assert.Equal(14.0, state.ScaledValue);
        Assert.Equal(T2, state.SourceTimestamp);
    }
}