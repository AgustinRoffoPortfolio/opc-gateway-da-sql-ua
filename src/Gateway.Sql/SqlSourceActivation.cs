namespace Gateway.Sql;

/// Si la fuente SQL arranca, y por que (B1). Igual que SqlOptionsValidation,
/// dos listas de texto ya redactado: el host decide que loguear y si crea el
/// hilo de polling.
public sealed record SqlActivationResult(
    bool Active,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static readonly SqlActivationResult SinTagsSql = new(false, [], []);
}

/// Cruza si el CSV declaro tags SQL contra si Sql:* es valida, para decidir si
/// el hilo de polling SQL arranca (invariante 8).
///
/// Funcion pura, igual que SqlOptionsValidator: no loguea, no abre conexiones,
/// no crea hilos. Se testea sin base y sin logger.
public static class SqlSourceActivation
{
    /// Sin tags SQL en el CSV no tiene sentido exigir Host, User o Password:
    /// una planta que solo usa OPC DA no tiene por que configurar una base que
    /// no usa, y exigirla igual seria una dependencia inventada. Con tags SQL
    /// declarados, en cambio, la fuente entera queda inactiva ante cualquier
    /// error de configuracion en vez de reintentar para siempre contra
    /// parametros que nunca van a funcionar: es mas barato fallar una vez al
    /// arrancar, con el motivo en el log, que reconectar cada
    /// Sql:ReconnectDelaySeconds sin parar.
    public static SqlActivationResult Decide(bool hasSqlTags, SqlOptions options)
    {
        if (!hasSqlTags) return SqlActivationResult.SinTagsSql;

        var validation = SqlOptionsValidator.Validate(options);
        return new SqlActivationResult(validation.IsValid, validation.Errors, validation.Warnings);
    }
}
