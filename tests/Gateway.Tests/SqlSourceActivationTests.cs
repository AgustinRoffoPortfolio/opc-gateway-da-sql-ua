using Gateway.Sql;

namespace Gateway.Tests;

public class SqlSourceActivationTests
{
    // Misma configuracion de referencia que SqlOptionsValidatorTests: los
    // defaults de SqlOptions mas las credenciales.
    private static SqlOptions ConfigValida() => new()
    {
        User = "gateway",
        Password = "una-password",
    };

    [Fact]
    public void SinTagsSql_NoExigeConfiguracionYQuedaInactiva()
    {
        // Config invalida a proposito (todo vacio): sin tags SQL en el CSV no
        // tiene que importar.
        var result = SqlSourceActivation.Decide(hasSqlTags: false, new SqlOptions());

        Assert.False(result.Active);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ConTagsSqlYConfigValida_Activa()
    {
        var result = SqlSourceActivation.Decide(hasSqlTags: true, ConfigValida());

        Assert.True(result.Active);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ConTagsSqlYConfigInvalida_QuedaInactivaConLosErrores()
    {
        var options = ConfigValida();
        options.Host = "";
        options.User = "";

        var result = SqlSourceActivation.Decide(hasSqlTags: true, options);

        Assert.False(result.Active);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public void SinTagsSqlYConfigValida_QuedaInactivaSinValidar()
    {
        // Config valida a proposito: aun asi, sin tags SQL en el CSV, Decide
        // corta antes de llamar a SqlOptionsValidator. La fuente no se activa
        // porque nunca se le pide que se active, no porque la config falle.
        var result = SqlSourceActivation.Decide(hasSqlTags: false, ConfigValida());

        Assert.False(result.Active);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }
}
