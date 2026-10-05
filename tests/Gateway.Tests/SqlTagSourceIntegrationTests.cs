using Gateway.Sql;

namespace Gateway.Tests;

/// Verificacion del paso 2 de la Fase 3: el driver conecta contra SQL Server y
/// trae filas. Necesita la base levantada, asi que se omite si no estan las
/// variables de entorno (ver SqlIntegrationFactAttribute).
///
/// Los tests no escriben en CURRENT_VALUES: leen lo que dejo el simulador. Es la
/// situacion real, donde la tabla la escribe otra aplicacion. Hay que haber
/// corrido el simulador al menos una vez.
[Trait("Category", "Integration")]
public class SqlTagSourceIntegrationTests
{
    private static SqlOptions ConfigDeLaBaseLocal() => new()
    {
        Host = "127.0.0.1",
        Port = 1433,
        User = SqlIntegrationFactAttribute.GetUser() ?? "",
        Password = SqlIntegrationFactAttribute.GetPassword() ?? "",

        // El contenedor usa certificado autofirmado, asi que la conexion se
        // cifra pero no se valida el certificado. Es la configuracion que en
        // el gateway real vive en el override local (V2-6), no en el JSON.
        Encrypt = true,
        TrustServerCertificate = true,
    };

    [SqlIntegrationFact]
    public void Connect_AbreLaConexion()
    {
        using var source = new SqlTagSource(ConfigDeLaBaseLocal());

        source.Connect();

        Assert.True(source.IsConnected);
    }

    [SqlIntegrationFact]
    public void ReadRows_TraeFilasDeCurrentValues()
    {
        using var source = new SqlTagSource(ConfigDeLaBaseLocal());
        source.Connect();

        var rows = source.ReadRows();

        Assert.NotEmpty(rows);
    }

    // Que las filas tengan forma, no solo que vengan. Si el simulador no corrio
    // todavia la tabla esta vacia y el test de arriba ya lo dice.
    [SqlIntegrationFact]
    public void FilasLeidas_TienenTagYTimestamp()
    {
        using var source = new SqlTagSource(ConfigDeLaBaseLocal());
        source.Connect();

        var rows = source.ReadRows();
        var row = rows[0];

        Assert.False(string.IsNullOrWhiteSpace(row.Tag));
        Assert.NotEqual(default, row.Ts);
    }

    // La consulta se puede repetir sobre la misma conexion: es el modelo de R5
    // y V2-20, una conexion unica que sobrevive a muchos ciclos de polling.
    [SqlIntegrationFact]
    public void VariasLecturas_SobreLaMismaConexion()
    {
        using var source = new SqlTagSource(ConfigDeLaBaseLocal());
        source.Connect();

        var primera = source.ReadRows();
        var segunda = source.ReadRows();

        Assert.NotEmpty(primera);
        Assert.Equal(primera.Count, segunda.Count);
        Assert.True(source.IsConnected);
    }

    // Leer sin conectar es un error de uso del driver, no una falla de la base.
    [SqlIntegrationFact]
    public void ReadRowsSinConnect_Tira()
    {
        using var source = new SqlTagSource(ConfigDeLaBaseLocal());

        Assert.Throws<InvalidOperationException>(() => source.ReadRows());
    }
}