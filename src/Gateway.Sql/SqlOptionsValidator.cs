namespace Gateway.Sql;

/// Resultado de validar SqlOptions. Dos listas de texto ya redactado: el host
/// decide que hacer con cada una. Los errores impiden arrancar, las
/// advertencias van al log y el gateway sigue.
public sealed record SqlOptionsValidation(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// Validacion de la configuracion SQL al arrancar (V2-6, V2-9).
///
/// Es una funcion pura a proposito: no loguea, no tira excepciones y no abre
/// conexiones. Asi se testea sin logger y sin base, igual que
/// TagQuality.FromDaCode. Quien convierte los errores en excepcion y las
/// advertencias en linea de log es el host.
public static class SqlOptionsValidator
{
    /// Rangos esperados de operacion segun R4 y R5. Fuera de estos rangos hay
    /// advertencia, no error: los requisitos dan rangos esperados, no limites,
    /// y un polling corto es justamente lo que hace falta para demostrar el
    /// gateway y para medir deteccion y recuperacion en la Fase 5.
    public const int ExpectedPollingMinSeconds = 20;
    public const int ExpectedPollingMaxSeconds = 60;
    public const int ExpectedReconnectMinSeconds = 10;
    public const int ExpectedReconnectMaxSeconds = 30;

    /// Limites duros de sanidad. Anchos a proposito: no opinan sobre como se
    /// configura el gateway, solo atajan el valor absurdo. Cero o negativo es
    /// un loop que no puede funcionar, y una hora de polling es un error de
    /// tipeo, no una decision.
    public const int MaxSaneSeconds = 3600;

    public static SqlOptionsValidation Validate(SqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var warnings = new List<string>();

        ValidateIdentifier(options.Database, "Sql:Database", errors);
        ValidateIdentifier(options.Schema, "Sql:Schema", errors);
        ValidateIdentifier(options.Table, "Sql:Table", errors);
        ValidateIdentifier(options.Columns.TagName, "Sql:Columns:TagName", errors);
        ValidateIdentifier(options.Columns.Timestamp, "Sql:Columns:Timestamp", errors);
        ValidateIdentifier(options.Columns.Value, "Sql:Columns:Value", errors);
        ValidateIdentifier(options.Columns.Quality, "Sql:Columns:Quality", errors);

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            errors.Add("Sql:Host esta vacio.");
        }

        if (options.Port < 1 || options.Port > 65535)
        {
            errors.Add($"Sql:Port fuera de rango: {options.Port}. Tiene que estar entre 1 y 65535.");
        }

        // Credenciales vacias son error y no advertencia: sin ellas la conexion
        // no abre. El mensaje nombra el override local porque ese es el error
        // esperado en una maquina nueva (V2-7), no un olvido de configuracion.
        if (string.IsNullOrEmpty(options.User))
        {
            errors.Add("Sql:User esta vacio. Los valores reales van en el override local (user-secrets o variable de entorno Sql__User), no en appsettings.json.");
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            errors.Add("Sql:Password esta vacio. Los valores reales van en el override local (user-secrets o variable de entorno Sql__Password), no en appsettings.json.");
        }

        ValidateSeconds(
            options.PollingIntervalSeconds, "Sql:PollingIntervalSeconds",
            ExpectedPollingMinSeconds, ExpectedPollingMaxSeconds, "R4",
            errors, warnings);

        ValidateSeconds(
            options.ReconnectDelaySeconds, "Sql:ReconnectDelaySeconds",
            ExpectedReconnectMinSeconds, ExpectedReconnectMaxSeconds, "R5",
            errors, warnings);

        // El timeout no tiene rango esperado en ningun requisito: es un
        // agregado nuestro (V2-6). Solo se controla que sea sano y que no se
        // coma el ciclo de polling, porque si tarda mas que el intervalo los
        // ciclos se enciman y cae el invariante 8.
        if (options.CommandTimeoutSeconds <= 0 || options.CommandTimeoutSeconds > MaxSaneSeconds)
        {
            errors.Add($"Sql:CommandTimeoutSeconds fuera de todo rango razonable: {options.CommandTimeoutSeconds}. Tiene que estar entre 1 y {MaxSaneSeconds}.");
        }
        else if (options.PollingIntervalSeconds > 0
                 && options.CommandTimeoutSeconds >= options.PollingIntervalSeconds)
        {
            warnings.Add($"Sql:CommandTimeoutSeconds ({options.CommandTimeoutSeconds} s) es mayor o igual que Sql:PollingIntervalSeconds ({options.PollingIntervalSeconds} s). Una consulta lenta encimaria ciclos.");
        }

        ValidateTimeZone(options.TimeZone, errors);

        // Cifrado relajado: no es error, pero tiene que quedar dicho. Es la
        // configuracion de la base local con certificado autofirmado (V2-6), y
        // contra un servidor real seria un agujero silencioso.
        if (options.Encrypt && options.TrustServerCertificate)
        {
            warnings.Add("Sql:TrustServerCertificate esta en true: la conexion se cifra pero no se valida el certificado del servidor. Es lo esperado contra la base local, no contra un servidor real.");
        }

        if (!options.Encrypt)
        {
            warnings.Add("Sql:Encrypt esta en false: la conexion viaja sin cifrar.");
        }

        return new SqlOptionsValidation(errors, warnings);
    }

    /// La zona de V2-18. Vacio es valido: significa la zona de la maquina.
    ///
    /// Se resuelve aca aunque el validador sea puro porque no abre nada ni
    /// loguea: consulta la tabla de zonas del sistema, que es lectura local. Y
    /// hay que resolverla al arrancar: un ID mal escrito explotaria recien en el
    /// primer ciclo de polling, lejos del arranque y con un mensaje que no
    /// nombra el JSON.
    private static void ValidateTimeZone(string value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (TimeZoneNotFoundException)
        {
            errors.Add($"Sql:TimeZone no existe en esta maquina: '{value}'. Dejalo vacio para usar la zona local, o usa un ID valido (por ejemplo 'Argentina Standard Time').");
        }
        catch (InvalidTimeZoneException)
        {
            errors.Add($"Sql:TimeZone existe pero sus datos estan corruptos: '{value}'.");
        }
    }

    private static void ValidateIdentifier(string value, string key, List<string> errors)
    {
        if (!SqlIdentifier.IsValid(value))
        {
            errors.Add($"{key} no es un identificador valido de SQL Server: '{value}'.");
        }
    }

    private static void ValidateSeconds(
        int value, string key, int expectedMin, int expectedMax, string requirement,
        List<string> errors, List<string> warnings)
    {
        if (value <= 0 || value > MaxSaneSeconds)
        {
            errors.Add($"{key} fuera de todo rango razonable: {value}. Tiene que estar entre 1 y {MaxSaneSeconds}.");
            return;
        }

        if (value < expectedMin || value > expectedMax)
        {
            warnings.Add($"{key} = {value} s, fuera del rango esperado por {requirement} ({expectedMin} a {expectedMax} s). El gateway arranca igual.");
        }
    }
}