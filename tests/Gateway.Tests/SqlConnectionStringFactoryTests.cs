using Gateway.Sql;

namespace Gateway.Tests;

public class SqlConnectionStringFactoryTests
{
    private static SqlOptions ConfigValida() => new()
    {
        User = "gateway",
        Password = "una-password",
    };

    [Fact]
    public void HostYPuerto_VanJuntosEnDataSource()
    {
        var options = ConfigValida();
        options.Host = "192.168.1.50";
        options.Port = 1433;

        var cadena = SqlConnectionStringFactory.Build(options);

        Assert.Contains("192.168.1.50,1433", cadena);
    }

    // V2-20: es la propiedad que hace honesto el LinkState. Si el pooling
    // volviera a estar activo, una conexion fallida podria reentregarse desde
    // el pool y el driver estaria diagnosticando sobre un objeto que no
    // controla.
    [Fact]
    public void Pooling_QuedaApagado()
    {
        var cadena = SqlConnectionStringFactory.Build(ConfigValida());

        Assert.Contains("Pooling=False", cadena);
    }

    [Fact]
    public void ParametrosDeCifrado_ViajanALaCadena()
    {
        var options = ConfigValida();
        options.Encrypt = true;
        options.TrustServerCertificate = true;

        var cadena = SqlConnectionStringFactory.Build(options);

        Assert.Contains("Encrypt=True", cadena);
        Assert.Contains("Trust Server Certificate=True", cadena);
    }

    // El caso que justifica usar SqlConnectionStringBuilder en vez de pegar
    // texto: el punto y coma separa parametros, asi que una password que lo
    // contenga, concatenada a mano, cortaria la cadena en dos. El builder la
    // entrecomilla y la cadena sigue significando lo mismo. Se verifica
    // releyendola con el propio builder, que es como la lee el driver.
    [Fact]
    public void PasswordConPuntoYComa_NoRompeLaCadena()
    {
        var options = ConfigValida();
        options.Password = "abc;def=ghi\"jkl";

        var cadena = SqlConnectionStringFactory.Build(options);
        var releida = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cadena);

        Assert.Equal("abc;def=ghi\"jkl", releida.Password);
        Assert.Equal("gateway", releida.UserID);
    }

    [Fact]
    public void TimeoutDeApertura_SaleDeCommandTimeout()
    {
        var options = ConfigValida();
        options.CommandTimeoutSeconds = 7;

        var cadena = SqlConnectionStringFactory.Build(options);
        var releida = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cadena);

        Assert.Equal(7, releida.ConnectTimeout);
    }

    [Fact]
    public void BaseDeDatos_VaEnInitialCatalog()
    {
        var options = ConfigValida();
        options.Database = "SCADA_HST";

        var cadena = SqlConnectionStringFactory.Build(options);
        var releida = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cadena);

        Assert.Equal("SCADA_HST", releida.InitialCatalog);
    }
}