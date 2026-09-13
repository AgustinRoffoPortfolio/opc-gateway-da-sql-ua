using Microsoft.Data.SqlClient;

namespace Gateway.Sql;

/// Arma la cadena de conexion desde SqlOptions (V2-6, V2-20).
///
/// Es el unico lugar donde las partes sueltas del JSON se convierten en una
/// cadena. Se usa SqlConnectionStringBuilder y no concatenacion de texto
/// porque el builder escapa los valores: una password con punto y coma o
/// comillas se reinterpreta correctamente en vez de romper la cadena o, peor,
/// cambiar su significado.
public static class SqlConnectionStringFactory
{
    public static string Build(SqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{options.Host},{options.Port}",
            InitialCatalog = options.Database,
            UserID = options.User,
            Password = options.Password,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,

            // Pooling apagado a proposito (V2-20). Con pooling, una conexion
            // que fallo puede volver al pool y entregarse de nuevo, y el
            // LinkState estaria reportando sobre un objeto cuyo estado real no
            // controla. Apagado, el ciclo de vida de la conexion fisica
            // coincide con lo que el driver cree que pasa, que es lo que hace
            // honesto el diagnostico por fuente de V2-13. El costo es un
            // handshake completo por reconexion, y solo ocurre despues de una
            // falla.
            Pooling = false,

            // Timeout de apertura, distinto del de la consulta. Sin esto el
            // default son 15 s colgado esperando a una base caida, y el loop
            // de polling no puede tardar mas que su propio intervalo sin
            // encimar ciclos (invariante 8).
            ConnectTimeout = options.CommandTimeoutSeconds,

            // Aparece en las sesiones de SQL Server: quien mire la base desde
            // SSMS ve que se conecto el gateway y no "Core .NET SqlClient".
            ApplicationName = "OpcGateway",
        };

        return builder.ConnectionString;
    }
}