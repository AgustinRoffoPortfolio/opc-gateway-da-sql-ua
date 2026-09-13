using Microsoft.Data.SqlClient;

namespace Gateway.Sql;

/// Borde con SQL Server (V2-8). Clase pasiva: conecta, lee y libera. Sin
/// hilos, sin temporizadores y sin politica de reintentos adentro, igual que
/// OpcDaTagSource. Quien decide cuando leer y que hacer ante una falla es el
/// hilo dedicado del host (V2-10).
///
/// Ningun tipo de Microsoft.Data.SqlClient sale de esta clase: ReadRows
/// devuelve SqlTagRow, que es nuestro. Lo impone el grafo de referencias entre
/// proyectos, no la disciplina (principio 1).
public sealed class SqlTagSource : IDisposable
{
    private readonly SqlOptions _options;
    private readonly string _connectionString;
    private readonly string _query;
    private SqlConnection? _connection;
    private bool _disposed;

    public SqlTagSource(SqlOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionString = SqlConnectionStringFactory.Build(options);
        _query = BuildQuery(options);
    }

    /// La consulta que se ejecuta cada ciclo, armada una sola vez en el
    /// constructor. Es la de R3 textual: la tabla entera, sin WHERE y con
    /// NOLOCK. El filtrado a los tags del CSV lo hace el gateway, no la base.
    public string Query => _query;

    /// Si hay una conexion abierta. No garantiza que la red siga viva: una
    /// conexion no se entera de que se cayo hasta que falla una operacion
    /// (V2-20). El estado real lo da el resultado de la ultima consulta.
    public bool IsConnected => _connection?.State == System.Data.ConnectionState.Open;

    /// Abre la conexion unica y persistente (R5, V2-20). Si ya habia una, se
    /// descarta antes: una conexion que fallo no se reusa.
    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CloseConnection();

        var connection = new SqlConnection(_connectionString);
        connection.Open();
        _connection = connection;
    }

    /// Ejecuta la consulta y devuelve las filas crudas, sin interpretar.
    ///
    /// Reparto de errores, copiado de OpcDaTagSource.ReadAll: un problema de
    /// conexion o de consulta se deja propagar, para que el host aplique la
    /// politica de reconexion de R5. Una fila rota se saltea y la lectura
    /// sigue, porque una fila no puede tirar a las otras diez mil.
    public IReadOnlyList<SqlTagRow> ReadRows()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is null)
            throw new InvalidOperationException("Hay que llamar a Connect() antes de leer.");

        var rows = new List<SqlTagRow>();

        using var command = new SqlCommand(_query, _connection)
        {
            CommandTimeout = _options.CommandTimeoutSeconds,
        };

        using var reader = command.ExecuteReader();

        // Por indice y no por nombre: el orden lo fija la consulta que armamos
        // nosotros, asi que es estable aunque los nombres de columna sean
        // configurables (R6).
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;

            var tag = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(tag)) continue;

            var ts = reader.GetDateTime(1);
            var v = reader.IsDBNull(2) ? (float?)null : reader.GetFloat(2);
            var q = reader.IsDBNull(3) ? (short?)null : reader.GetInt16(3);

            rows.Add(new SqlTagRow(tag, ts, v, q));
        }

        return rows;
    }

    /// Arma la consulta de R3 con los nombres configurables de R6, escapados
    /// como identificadores (V2-9). Los nombres ya se validaron al arrancar;
    /// el escape es la segunda linea, no la primera.
    private static string BuildQuery(SqlOptions options)
    {
        var table = SqlIdentifier.QualifyTable(options.Database, options.Schema, options.Table);
        var tag = SqlIdentifier.Quote(options.Columns.TagName);
        var ts = SqlIdentifier.Quote(options.Columns.Timestamp);
        var value = SqlIdentifier.Quote(options.Columns.Value);
        var quality = SqlIdentifier.Quote(options.Columns.Quality);

        return $"SELECT {tag}, {ts}, {value}, {quality} FROM {table} WITH (NOLOCK)";
    }

    private void CloseConnection()
    {
        if (_connection is null) return;

        try
        {
            _connection.Dispose();
        }
        catch
        {
            // Cerrar una conexion ya rota puede tirar. No importa por que:
            // el objeto se descarta igual y el proximo Connect() crea uno
            // nuevo. Tragarse esto aca evita que una falla de limpieza tape
            // la falla original que llevo a reconectar.
        }
        finally
        {
            _connection = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        CloseConnection();
        _disposed = true;
    }
}