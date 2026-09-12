using Gateway.Core;

namespace Gateway.Tests;

/// <summary>
/// Verifica la decodificacion de un codigo de calidad DA crudo (la columna Q de
/// CURR_DATA) a TagQuality. Es el paso anterior a QualityMapper: aquel traduce
/// TagQuality -> StatusCode UA, este arma el TagQuality desde el entero.
/// </summary>
public class TagQualityTests
{
    // Los cinco codigos que aparecen de verdad en la tabla real, segun el conteo
    // de docs/v2/calidad-observada.md. Cubren el 100% de las 41.042 filas.
    public static TheoryData<int, QualityMaster, QualitySubstatus> CodigosObservados => new()
    {
        { 192, QualityMaster.Good,      QualitySubstatus.Good },
        {  20, QualityMaster.Bad,       QualitySubstatus.BadLastKnown },
        {  24, QualityMaster.Bad,       QualitySubstatus.BadCommFailure },
        { 216, QualityMaster.Good,      QualitySubstatus.GoodLocalOverride },
        {  64, QualityMaster.Uncertain, QualitySubstatus.Uncertain }
    };

    [Theory]
    [MemberData(nameof(CodigosObservados))]
    public void Decodifica_los_codigos_observados_en_la_tabla(
        int code, QualityMaster master, QualitySubstatus substatus)
    {
        var quality = TagQuality.FromDaCode(code, out var unknown);

        Assert.Equal(master, quality.Master);
        Assert.Equal(substatus, quality.Substatus);
        Assert.Equal(QualityLimit.NotLimited, quality.Limit);
        Assert.False(unknown);
    }

    // El mas protector: si manana se agrega un substatus al enum, tiene que
    // poder decodificarse desde su propio codigo sin caer en desconocido.
    [Fact]
    public void Todo_substatus_del_enum_se_decodifica_desde_su_codigo()
    {
        foreach (var substatus in Enum.GetValues<QualitySubstatus>())
        {
            var quality = TagQuality.FromDaCode((int)substatus, out var unknown);

            Assert.False(unknown, $"{substatus} deberia ser un substatus conocido");
            Assert.Equal(substatus, quality.Substatus);
        }
    }

    [Theory]
    [InlineData(193, QualityLimit.Low)]
    [InlineData(194, QualityLimit.High)]
    [InlineData(195, QualityLimit.Constant)]
    public void Los_dos_bits_bajos_son_el_limit(int code, QualityLimit expected)
    {
        var quality = TagQuality.FromDaCode(code, out _);

        // El limit no ensucia el substatus: sigue siendo Good.
        Assert.Equal(QualitySubstatus.Good, quality.Substatus);
        Assert.Equal(expected, quality.Limit);
    }

    // Hoy no pasa (calidad-observada.md no encontro negativos), pero si algun
    // driver escribiera bits de fabricante en el byte alto, el bit 15 vuelve
    // negativo al smallint y una comparacion contra 192 fallaria en silencio.
    [Fact]
    public void Los_bits_de_fabricante_se_descartan_aunque_el_codigo_sea_negativo()
    {
        // 0xC0C0 leido como smallint con signo.
        const int conBitsDeFabricante = unchecked((short)0xC0C0);

        Assert.True(conBitsDeFabricante < 0);

        var quality = TagQuality.FromDaCode(conBitsDeFabricante, out var unknown);

        Assert.Equal(QualityMaster.Good, quality.Master);
        Assert.Equal(QualitySubstatus.Good, quality.Substatus);
        Assert.False(unknown);
    }

    [Theory]
    [InlineData(196, QualityMaster.Good,      QualitySubstatus.Good)]
    [InlineData(36,  QualityMaster.Bad,       QualitySubstatus.Bad)]
    [InlineData(76,  QualityMaster.Uncertain, QualitySubstatus.Uncertain)]
    public void Un_substatus_no_previsto_conserva_el_master(
        int code, QualityMaster master, QualitySubstatus fallback)
    {
        var quality = TagQuality.FromDaCode(code, out var unknown);

        Assert.True(unknown);
        Assert.Equal(master, quality.Master);
        Assert.Equal(fallback, quality.Substatus);
    }

    // Error esta reservado por la spec y no tiene substatus propio. Se conserva
    // el master para que la anomalia quede visible en vez de confundirse con
    // un Bad comun, que es lo que ya hace el driver DA con el mismo caso.
    [Fact]
    public void Master_Error_se_conserva_con_substatus_Bad()
    {
        var quality = TagQuality.FromDaCode(128, out var unknown);

        Assert.True(unknown);
        Assert.Equal(QualityMaster.Error, quality.Master);
        Assert.Equal(QualitySubstatus.Bad, quality.Substatus);
    }
}