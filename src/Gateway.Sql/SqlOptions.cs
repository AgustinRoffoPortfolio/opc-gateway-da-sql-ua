namespace Gateway.Sql;

/// Configuracion de la fuente SQL, leida de appsettings.json (V2-6).
/// Los parametros van sueltos y no como connection string: lo pide R6 y es lo
/// que permite sacar del repositorio solo User y Password (V2-7) sin llevarse
/// con ellos el host y la base, que no son secretos y conviene versionar.
public class SqlOptions
{
    /// Host y puerto del servidor SQL Server.
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1433;

    /// Las tres partes del nombre completo de la tabla. Se validan como
    /// identificadores antes de concatenarse (V2-9): un nombre de tabla no
    /// puede ir como parametro de ADO.NET, asi que lo que se controla no es
    /// como se pega sino que se acepta.
    public string Database { get; set; } = "SCADA_HST";
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "CURR_DATA";

    /// Credenciales de SQL Server (P8). Vacias a proposito en el JSON
    /// versionado: los valores reales llegan por user-secrets o por variables
    /// de entorno y pisan estas claves (V2-7).
    public string User { get; set; } = "";
    public string Password { get; set; } = "";

    /// Cada cuanto se consulta la tabla entera (R4). En segundos y no en
    /// milisegundos, contra el patron ...Ms de la v1, porque lo piden R4 y R5
    /// y porque a un 30000 se le escapa un cero sin que nadie lo note.
    public int PollingIntervalSeconds { get; set; } = 30;

    /// Espera entre intentos de reconexion cuando la consulta falla (R5).
    /// Es el mismo criterio que ReconnectDelayMs del lado DA: si la base esta
    /// caida, no vuelve en dos segundos.
    public int ReconnectDelaySeconds { get; set; } = 15;

    /// Timeout de la consulta. Agregado sobre R6: el default del cliente son
    /// 30 s, mas que el polling minimo de R4, asi que una consulta lenta
    /// encimaria ciclos y romperia el invariante 8. Diez segundos sobran para
    /// una tabla de 10.000 filas.
    public int CommandTimeoutSeconds { get; set; } = 10;

    /// Cifrado de la conexion. Los valores versionados son los seguros; el
    /// contenedor local usa certificado autofirmado y necesita
    /// TrustServerCertificate en true, que va en el override local junto con
    /// las credenciales (V2-6).
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; } = false;

    /// Nombres de las cuatro columnas, configurables por R6.
    public SqlColumnOptions Columns { get; set; } = new();
}

/// Nombres de columna de la tabla de origen. Clase aparte porque en el JSON
/// es un objeto anidado y el binder de configuracion necesita un tipo que
/// mapear.
public class SqlColumnOptions
{
    public string TagName { get; set; } = "TAG";
    public string Timestamp { get; set; } = "TS";
    public string Value { get; set; } = "V";
    public string Quality { get; set; } = "Q";
}