using Gateway.Core;
using Gateway.Sql;

namespace Gateway.Tests;

public class SqlTagMapperTests
{
    // Argentina no aplica horario de verano desde 2009, asi que sirve para el
    // caso normal: offset fijo de -3 y sin saltos.
    private const string ZonaArgentina = "Argentina Standard Time";

    // Para los dos casos de horario de verano hace falta una zona que lo
    // aplique, porque en Argentina no se pueden reproducir.
    private const string ZonaPacifico = "Pacific Standard Time";

    private static SqlTagMapper Mapper(string timeZone = ZonaArgentina) =>
        new(new SqlOptions { TimeZone = timeZone });

    private static SqlTagRow Row(
        string tag = "TIC101.PV",
        DateTime? ts = null,
        float? v = 5.0f,
        short? q = 192) =>
        new(tag, ts ?? new DateTime(2026, 8, 12, 10, 0, 0), v, q);

    [Fact]
    public void FilaNormal_MapeaValorCalidadYTimestamp()
    {
        var result = Mapper().Map([Row()]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(5.0f, sample.Value);
        Assert.Equal(TagQuality.Good, sample.Quality);
        Assert.Equal(new DateTime(2026, 8, 12, 13, 0, 0, DateTimeKind.Utc), sample.SourceTimestamp);
    }

    [Fact]
    public void HoraLocal_SeConvierteAUtcContraLaZonaConfigurada()
    {
        // V2-18. Sin convertir, los tags SQL quedarian tres horas corridos
        // respecto de los DA y un cliente que compare las dos fuentes veria el
        // dato SQL en el pasado sin explicacion.
        var result = Mapper().Map([Row(ts: new DateTime(2026, 1, 15, 23, 30, 0))]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(new DateTime(2026, 1, 16, 2, 30, 0, DateTimeKind.Utc), sample.SourceTimestamp);
        Assert.Equal(DateTimeKind.Utc, sample.SourceTimestamp.Kind);
    }

    [Fact]
    public void ValorNulo_PublicaSinValorYConCalidadNoBuena()
    {
        // V2-16. No hay medicion: la cache conserva el ultimo valor bueno y su
        // SourceTimestamp. Uncertain y no Bad, que borraria el dato al cliente.
        var result = Mapper().Map([Row(v: null)]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Null(sample.Value);
        Assert.Equal(TagQuality.LastUsableValue, sample.Quality);
        Assert.Equal(1, result.NullValueCount);
    }

    [Fact]
    public void CalidadNula_ConservaElValorYPublicaUncertain()
    {
        // V2-16. Hay medicion pero no hay codigo de calidad que mapear: el
        // valor y el timestamp se actualizan normales, solo la calidad duda.
        var result = Mapper().Map([Row(q: null)]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(5.0f, sample.Value);
        Assert.Equal(TagQuality.LastUsableValue, sample.Quality);
        Assert.Equal(1, result.NullQualityCount);
    }

    [Fact]
    public void PerdidaDeCampo_LlegaComoBadLastKnown()
    {
        // Es el caso de P6: la aplicacion de origen marca 20 cuando pierde el
        // campo. Se mapea tal cual, sin degradarlo ni mejorarlo.
        var result = Mapper().Map([Row(q: 20)]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(QualityMaster.Bad, sample.Quality.Master);
        Assert.Equal(QualitySubstatus.BadLastKnown, sample.Quality.Substatus);
    }

    [Fact]
    public void SubstatusNoPrevisto_SeCuentaPeroNoDescartaLaMuestra()
    {
        // V2-19. El master es lo que decide si el dato sirve, asi que un
        // substatus raro no invalida la fila: se cuenta para que el host lo
        // loguee y el valor se publica igual.
        var result = Mapper().Map([Row(q: 196)]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(QualityMaster.Good, sample.Quality.Master);
        Assert.Equal(5.0f, sample.Value);
        Assert.Equal(1, result.UnknownSubstatusCount);
    }

    [Fact]
    public void HoraInexistente_DegradaLaCalidadYSeCuenta()
    {
        // 02:30 del 8/3/2026 no existe en el Pacifico: el reloj salta de 2 a 3.
        // No hay UTC al que convertirlo, asi que se publica como duda en vez de
        // inventar un instante (V2-18).
        var result = Mapper(ZonaPacifico).Map([Row(ts: new DateTime(2026, 3, 8, 2, 30, 0))]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(TagQuality.LastUsableValue, sample.Quality);
        Assert.Equal(5.0f, sample.Value);
        Assert.Equal(1, result.InvalidTimestampCount);
    }

    [Fact]
    public void HoraAmbigua_EligeElHorarioEstandar()
    {
        // 01:30 del 1/11/2026 ocurre dos veces en el Pacifico. Se resuelve con
        // el default de .NET, que elige el horario estandar: UTC-8, no UTC-7.
        var result = Mapper(ZonaPacifico).Map([Row(ts: new DateTime(2026, 11, 1, 1, 30, 0))]);

        var sample = result.Samples["TIC101.PV"];
        Assert.Equal(new DateTime(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc), sample.SourceTimestamp);
        Assert.Equal(0, result.InvalidTimestampCount);
    }

    [Fact]
    public void ZonaVacia_UsaLaDeLaMaquina()
    {
        // El caso normal: base y gateway en la misma maquina, sin configurar
        // nada. Se compara contra la conversion local para no atarlo a la zona
        // de quien corre los tests.
        var ts = new DateTime(2026, 8, 12, 10, 0, 0);
        var esperado = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(ts, DateTimeKind.Unspecified), TimeZoneInfo.Local);

        var result = Mapper(timeZone: "").Map([Row(ts: ts)]);

        Assert.Equal(esperado, result.Samples["TIC101.PV"].SourceTimestamp);
    }

    [Fact]
    public void NombresQueDifierenEnMayusculas_NoEntranDosVeces()
    {
        // TAG es clave primaria y SQL Server no distingue mayusculas, asi que
        // la tabla no puede traer las dos formas. Si igual llegaran, el choque
        // se resuelve aca y no mas lejos, en la cache (V2-17).
        var result = Mapper().Map([Row(tag: "TIC101.PV", v: 1.0f), Row(tag: "tic101.pv", v: 2.0f)]);

        Assert.Single(result.Samples);
        Assert.Equal(2.0f, result.Samples["TIC101.PV"].Value);
    }
}