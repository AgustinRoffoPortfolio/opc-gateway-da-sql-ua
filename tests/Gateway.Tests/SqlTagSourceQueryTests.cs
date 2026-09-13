using Gateway.Sql;

namespace Gateway.Tests;

// Solo la consulta: se arma en el constructor, asi que se verifica sin base.
// Lo que necesita SQL Server levantado va en el test de integracion aparte.
public class SqlTagSourceQueryTests
{
    private static SqlOptions ConfigValida() => new()
    {
        User = "gateway",
        Password = "una-password",
    };

    // Es la consulta textual de R3, la que paso mi padre. Si alguien le saca
    // el NOLOCK o le agrega un WHERE, deja de cumplir el requisito y este test
    // lo caza.
    [Fact]
    public void ConsultaPorDefecto_EsLaDeR3()
    {
        var source = new SqlTagSource(ConfigValida());

        Assert.Equal(
            "SELECT [TAG], [TS], [V], [Q] FROM [SCADA_HST].[dbo].[CURR_DATA] WITH (NOLOCK)",
            source.Query);
    }

    [Fact]
    public void NombresDeColumnaConfigurados_ViajanALaConsulta()
    {
        var options = ConfigValida();
        options.Columns.TagName = "NOMBRE";
        options.Columns.Timestamp = "FECHA";
        options.Columns.Value = "VALOR";
        options.Columns.Quality = "CALIDAD";

        var source = new SqlTagSource(options);

        Assert.Equal(
            "SELECT [NOMBRE], [FECHA], [VALOR], [CALIDAD] FROM [SCADA_HST].[dbo].[CURR_DATA] WITH (NOLOCK)",
            source.Query);
    }

    [Fact]
    public void TablaConfigurada_ViajaALaConsulta()
    {
        var options = ConfigValida();
        options.Database = "OTRA_BASE";
        options.Schema = "planta";
        options.Table = "VALORES";

        var source = new SqlTagSource(options);

        Assert.Contains("FROM [OTRA_BASE].[planta].[VALORES]", source.Query);
    }

    // Sin WHERE por requisito (R3): el filtrado a los tags del CSV lo hace el
    // gateway, no la base.
    [Fact]
    public void ConsultaNoTieneWhere()
    {
        var source = new SqlTagSource(ConfigValida());

        Assert.DoesNotContain("WHERE", source.Query, StringComparison.OrdinalIgnoreCase);
    }

    // Construir el driver no abre la conexion: eso es Connect(). Importa
    // porque el host lo va a construir al arrancar, con la base posiblemente
    // caida, y no puede explotar ahi.
    [Fact]
    public void ConstruirNoConecta()
    {
        using var source = new SqlTagSource(ConfigValida());

        Assert.False(source.IsConnected);
    }
}