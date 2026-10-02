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
    public void MissingTags_DevuelveLosDeclaradosQueNoVinieron()
    {
        // V2-21, primer caso: el tag existe en el CSV y no en la tabla.
        var cache = new TagCache([
            DefOf("PLANTA_01.PRESENTE", TagSource.Sql, "TIC101.PV"),
            DefOf("PLANTA_01.AUSENTE", TagSource.Sql, "TIC999.PV"),
            DefOf("PLANTA_01.OTRA_FUENTE", TagSource.OpcDa, "TIC888.PV")]);

        var missing = cache.MissingTags(TagSource.Sql, SampleOf("TIC101.PV", 42.0));

        // El de DA no entra aunque tampoco vino: se pregunta por una fuente.
        Assert.Equal(["TIC999.PV"], missing);
    }

    [Fact]
    public void MissingTags_IgnoraMayusculas_DelLadoSql()
    {
        // SQL Server no distingue mayusculas (V2-17). Si el CSV declara el tag
        // con otra caja que la tabla, esta igual y no se reporta ausente.
        var cache = new TagCache([DefOf("PLANTA_01.A", TagSource.Sql, "TIC101.PV")]);

        Assert.Empty(cache.MissingTags(TagSource.Sql, SampleOf("tic101.pv", 42.0)));
    }

    [Fact]
    public void MissingTags_ConTodosPresentes_NoDevuelveNada()
    {
        var cache = new TagCache([DefOf("PLANTA_01.A", TagSource.Sql, "TIC101.PV")]);

        Assert.Empty(cache.MissingTags(TagSource.Sql, SampleOf("TIC101.PV", 42.0)));
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

    // --- Tipos nuevos de la v2: Float y Boolean numerico -------------------

    [Fact]
    public void Float_AplicaMultiplicadorYOffsetYPublicaFloat()
    {
        // V2-14. El tipo publicado importa tanto como el numero: un cliente que
        // lee Float sabe cuantos digitos tiene sentido mostrar.
        var cache = CacheWith(Def(TagDataType.Float, multiplier: 2.0, offset: 10.0));

        cache.Update(TagSource.OpcDa, Sample(5.0f, TagQuality.Good, T1));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.IsType<float>(state.ScaledValue);
        Assert.Equal(20.0f, (float)state.ScaledValue!);
    }

    [Fact]
    public void Float_CalculaEnDoubleYCasteaAlFinal()
    {
        // El valor de la columna real llega como float; el escalado se hace en
        // double y recien al publicar baja a float. Calcular en la precision
        // alta evita acumular error en la cuenta.
        var cache = CacheWith(Def(TagDataType.Float, multiplier: 1.0));

        cache.Update(TagSource.OpcDa, Sample(8009.57f, TagQuality.Good, T1));

        Assert.Equal(8009.57f, (float)cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue!);
    }

    [Fact]
    public void BooleanDesdeNumero_CeroEsFalseYElRestoTrue()
    {
        // V2-15. Desde SQL un booleano llega como 0 o 1 en la columna real (P2),
        // no como un bool materializado. Antes esto no actualizaba el tag nunca.
        var cache = CacheWith(Def(TagDataType.Boolean));

        cache.Update(TagSource.OpcDa, Sample(1.0f, TagQuality.Good, T1));
        Assert.Equal(true, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);

        cache.Update(TagSource.OpcDa, Sample(0.0f, TagQuality.Good, T2));
        Assert.Equal(false, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    [Fact]
    public void BooleanDesdeNumero_NoDescartaUnCasiUno()
    {
        // "Distinto de 0" y no "1 exactamente": tolera un valor que llegue como
        // 0,9999 por una conversion intermedia, en vez de tratarlo como imposible.
        var cache = CacheWith(Def(TagDataType.Boolean));

        cache.Update(TagSource.OpcDa, Sample(0.9999f, TagQuality.Good, T1));

        Assert.Equal(true, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    [Fact]
    public void Boolean_NoAplicaMultiplicadorNiOffset()
    {
        // Con offset 10 aplicado, un 0 daria true. No se aplica: escalar un
        // booleano no significa nada y lo daria vuelta.
        var cache = CacheWith(Def(TagDataType.Boolean, multiplier: 2.0, offset: 10.0));

        cache.Update(TagSource.OpcDa, Sample(0.0f, TagQuality.Good, T1));

        Assert.Equal(false, cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA").ScaledValue);
    }

    // --- Muestra con calidad utilizable pero sin valor: V2-16 --------------

    [Fact]
    public void ValorNuloConCalidadBuena_ConservaElValorYDegradaACalidadUsable()
    {
        // Es la columna V en NULL. Sin la rama de V2-16 esto caia en TryScale y
        // salia como ConversionError, que es Bad y borra el ultimo dato bueno.
        var cache = CacheWith(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        cache.Update(TagSource.OpcDa, Sample(null, TagQuality.Good, T2));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(10.0, state.ScaledValue);      // el valor viejo sobrevive
        Assert.Equal(T1, state.SourceTimestamp);    // no avanza: no hubo medicion
        Assert.Equal(TagQuality.LastUsableValue, state.Quality);
    }

    [Fact]
    public void ValorNuloConCalidadUncertain_RespetaLaCalidadQueLlego()
    {
        // El driver SQL manda Uncertain ante un NULL: esa calidad ya dice lo que
        // hay que decir y no se pisa con LastUsableValue.
        var uncertain = new TagQuality(
            QualityMaster.Uncertain, QualitySubstatus.UncertainSensorNotAccurate, QualityLimit.NotLimited);
        var cache = CacheWith(Def(multiplier: 2.0));
        cache.Update(TagSource.OpcDa, Sample(5.0, TagQuality.Good, T1));

        cache.Update(TagSource.OpcDa, Sample(null, uncertain, T2));

        var state = cache.Get("PLANTA_01.MEDICION.PRESION_ENTRADA");
        Assert.Equal(10.0, state.ScaledValue);
        Assert.Equal(T1, state.SourceTimestamp);
        Assert.Equal(uncertain, state.Quality);
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

    // --- Marca de fuente caida (V2-32) ---

    // Tags SQL como los arma el host: sin umbral de antiguedad (V2-11), para que
    // lo unico que pueda cambiar la calidad sea la marca o una muestra.
    private static TagDefinition SqlDef(string uaName, string sourceTag) =>
        new(uaName, TagSource.Sql, sourceTag, TagDataType.Double, 1.0, 0.0, StaleAfter: null);

    private static readonly TagQuality UncertainDeQ =
        new(QualityMaster.Uncertain, QualitySubstatus.UncertainSensorNotAccurate, QualityLimit.NotLimited);

    private static readonly TagQuality BadDeQ =
        new(QualityMaster.Bad, QualitySubstatus.BadDeviceFailure, QualityLimit.NotLimited);

    [Fact]
    public void MarcaDeFuenteCaida_PasaGoodALastUsableValue()
    {
        var cache = new TagCache([SqlDef("PLANTA_01.SQL_A", "TAG_A")]);
        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TAG_A"] = new TagSample(5.0, TagQuality.Good, T1)
        });

        cache.MarkSourceDown(TagSource.Sql);

        Assert.Equal(TagQuality.LastUsableValue, cache.Get("PLANTA_01.SQL_A").Quality);
    }

    [Fact]
    public void MarcaDeFuenteCaida_NoTocaUncertainNiBad()
    {
        // La marca solo degrada: un Uncertain o un Bad ya explican algo mas
        // especifico que "no hay vinculo", y pisarlos perderia la causa.
        var cache = new TagCache([
            SqlDef("PLANTA_01.SQL_UNCERTAIN", "TAG_U"),
            SqlDef("PLANTA_01.SQL_BAD", "TAG_B"),
            SqlDef("PLANTA_01.SQL_AUSENTE", "TAG_M"),
            SqlDef("PLANTA_01.SQL_SIN_DATO", "TAG_W")]);
        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TAG_U"] = new TagSample(1.0, UncertainDeQ, T1),
            ["TAG_B"] = new TagSample(2.0, BadDeQ, T1),
            ["TAG_M"] = TagSample.NoData(TagQuality.RowMissing)
        });

        cache.MarkSourceDown(TagSource.Sql);

        Assert.Equal(UncertainDeQ, cache.Get("PLANTA_01.SQL_UNCERTAIN").Quality);
        Assert.Equal(BadDeQ, cache.Get("PLANTA_01.SQL_BAD").Quality);
        Assert.Equal(TagQuality.RowMissing, cache.Get("PLANTA_01.SQL_AUSENTE").Quality);
        Assert.Equal(TagQuality.WaitingForInitialData, cache.Get("PLANTA_01.SQL_SIN_DATO").Quality);
    }

    [Fact]
    public void MarcaDeFuenteCaida_ConservaValorYSourceTimestamp()
    {
        var cache = new TagCache([SqlDef("PLANTA_01.SQL_A", "TAG_A")]);
        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TAG_A"] = new TagSample(5.0, TagQuality.Good, T1)
        });
        var before = cache.Get("PLANTA_01.SQL_A");

        cache.MarkSourceDown(TagSource.Sql);

        var after = cache.Get("PLANTA_01.SQL_A");
        Assert.Equal(5.0, after.ScaledValue);
        Assert.Equal(T1, after.SourceTimestamp);
        // Tampoco es una muestra: la hora de la ultima incorporacion no avanza.
        Assert.Equal(before.LastUpdateUtc, after.LastUpdateUtc);
    }

    [Fact]
    public void CicloPosteriorALaMarca_VuelveALaCalidadDeQ()
    {
        var cache = new TagCache([
            SqlDef("PLANTA_01.SQL_A", "TAG_A"),
            SqlDef("PLANTA_01.SQL_B", "TAG_B")]);
        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TAG_A"] = new TagSample(5.0, TagQuality.Good, T1),
            ["TAG_B"] = new TagSample(6.0, TagQuality.Good, T1)
        });
        cache.MarkSourceDown(TagSource.Sql);

        // El primer ciclo tras reconectar pisa la marca con lo que traiga Q,
        // sea mejor o peor que la marca.
        cache.Update(TagSource.Sql, new Dictionary<string, TagSample>
        {
            ["TAG_A"] = new TagSample(7.0, TagQuality.Good, T2),
            ["TAG_B"] = new TagSample(8.0, UncertainDeQ, T2)
        });

        var a = cache.Get("PLANTA_01.SQL_A");
        Assert.Equal(TagQuality.Good, a.Quality);
        Assert.Equal(7.0, a.ScaledValue);
        Assert.Equal(T2, a.SourceTimestamp);
        Assert.Equal(UncertainDeQ, cache.Get("PLANTA_01.SQL_B").Quality);
    }

    [Fact]
    public void MarcaDeFuenteCaida_NoAfectaTagsDeLaOtraFuente()
    {
        // Mismo nombre de origen en las dos fuentes a proposito: la marca tiene
        // que filtrar por fuente, no por nombre.
        var cache = new TagCache([
            SqlDef("PLANTA_01.DESDE_SQL", "TIC101.PV"),
            DefOf("PLANTA_01.DESDE_DA", TagSource.OpcDa, "TIC101.PV")]);
        cache.Update(TagSource.Sql, SampleOf("TIC101.PV", 11.0));
        cache.Update(TagSource.OpcDa, SampleOf("TIC101.PV", 22.0));

        cache.MarkSourceDown(TagSource.Sql);

        Assert.Equal(TagQuality.LastUsableValue, cache.Get("PLANTA_01.DESDE_SQL").Quality);
        Assert.Equal(TagQuality.Good, cache.Get("PLANTA_01.DESDE_DA").Quality);
    }
}
