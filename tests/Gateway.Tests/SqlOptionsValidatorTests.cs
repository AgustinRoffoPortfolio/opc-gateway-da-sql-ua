using Gateway.Sql;

namespace Gateway.Tests;

public class SqlOptionsValidatorTests
{
    // Configuracion valida de referencia: los defaults de SqlOptions mas las
    // credenciales, que en el JSON versionado van vacias a proposito (V2-7).
    // Cada test parte de esta y rompe una sola cosa.
    private static SqlOptions ConfigValida() => new()
    {
        User = "gateway",
        Password = "una-password",
    };

    [Fact]
    public void ConfigValida_NoTieneErroresNiAdvertencias()
    {
        var result = SqlOptionsValidator.Validate(ConfigValida());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void CredencialesVacias_SonDosErrores()
    {
        var options = ConfigValida();
        options.User = "";
        options.Password = "";

        var result = SqlOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    // El mensaje tiene que nombrar el override local: en una maquina nueva
    // este es el primer error que aparece, y sin esa pista parece un olvido
    // de configuracion en vez del mecanismo de V2-7 funcionando.
    [Fact]
    public void ErrorDeCredenciales_MencionaElOverrideLocal()
    {
        var options = ConfigValida();
        options.Password = "";

        var result = SqlOptionsValidator.Validate(options);

        Assert.Contains(result.Errors, e => e.Contains("Sql__Password"));
    }

    [Fact]
    public void NombreDeTablaInvalido_EsError()
    {
        var options = ConfigValida();
        options.Table = "CURRENT_VALUES; DROP TABLE USUARIOS";

        var result = SqlOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Sql:Table"));
    }

    [Fact]
    public void NombreDeColumnaInvalido_EsError()
    {
        var options = ConfigValida();
        options.Columns.Quality = "Q--";

        var result = SqlOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Sql:Columns:Quality"));
    }

    // El corazon de la decision de los dos rangos: fuera de R4 arranca igual,
    // porque un polling corto es lo que hace falta para demostrar el gateway y
    // para medir deteccion y recuperacion en la Fase 5.
    [Fact]
    public void PollingFueraDelRangoDeR4_EsAdvertenciaYArrancaIgual()
    {
        var options = ConfigValida();
        options.PollingIntervalSeconds = 5;

        var result = SqlOptionsValidator.Validate(options);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("Sql:PollingIntervalSeconds"));
    }

    [Fact]
    public void PollingCeroONegativo_EsErrorDuro()
    {
        var options = ConfigValida();
        options.PollingIntervalSeconds = 0;

        var result = SqlOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Sql:PollingIntervalSeconds"));
    }

    [Fact]
    public void PollingDesmedido_EsErrorDuro()
    {
        var options = ConfigValida();
        options.PollingIntervalSeconds = 7200;

        var result = SqlOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Sql:PollingIntervalSeconds"));
    }

    [Fact]
    public void ReconexionFueraDelRangoDeR5_EsAdvertencia()
    {
        var options = ConfigValida();
        options.ReconnectDelaySeconds = 60;

        var result = SqlOptionsValidator.Validate(options);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Sql:ReconnectDelaySeconds"));
    }

    // Toca el invariante 8: si la consulta puede tardar mas que el intervalo,
    // los ciclos se enciman.
    [Fact]
    public void TimeoutMayorQueElPolling_EsAdvertencia()
    {
        var options = ConfigValida();
        options.PollingIntervalSeconds = 30;
        options.CommandTimeoutSeconds = 30;

        var result = SqlOptionsValidator.Validate(options);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Sql:CommandTimeoutSeconds"));
    }

    [Fact]
    public void CertificadoNoValidado_EsAdvertencia()
    {
        var options = ConfigValida();
        options.TrustServerCertificate = true;

        var result = SqlOptionsValidator.Validate(options);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("TrustServerCertificate"));
    }

    [Fact]
    public void SinCifrado_EsAdvertencia()
    {
        var options = ConfigValida();
        options.Encrypt = false;

        var result = SqlOptionsValidator.Validate(options);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("Sql:Encrypt"));
    }

    // Los errores se juntan y se reportan todos, no de a uno por arranque.
    // Mismo criterio que el validador del CSV.
    [Fact]
    public void VariosErroresJuntos_SeReportanTodos()
    {
        var options = ConfigValida();
        options.Host = "";
        options.User = "";
        options.Table = "1_INVALIDA";
        options.Port = 0;

        var result = SqlOptionsValidator.Validate(options);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);
    }
}