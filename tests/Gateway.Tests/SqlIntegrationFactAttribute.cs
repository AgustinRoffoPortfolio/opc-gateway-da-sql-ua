namespace Gateway.Tests;

/// Marca un test que necesita SQL Server levantado.
///
/// xUnit 2 decide el Skip al descubrir los tests, no al correrlos, asi que no
/// se puede saltear desde adentro del test. El interruptor son las credenciales:
/// como no pueden vivir en el repositorio (V2-7), el test las lee de variables
/// de entorno igual, y si no estan definidas se omite. Asi "dotnet test" sigue
/// pasando en una maquina sin Docker, y la misma pieza que protege el secreto
/// hace el salteo.
///
/// Para correrlos:
///   docker compose up -d
///   $env:GATEWAY_SQL_TEST_USER = "sa"
///   $env:GATEWAY_SQL_TEST_PASSWORD = "la-password-del-.env"
///   dotnet test --filter Category=Integration
public sealed class SqlIntegrationFactAttribute : FactAttribute
{
    public const string UserVariable = "GATEWAY_SQL_TEST_USER";
    public const string PasswordVariable = "GATEWAY_SQL_TEST_PASSWORD";

    public SqlIntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(GetUser()) || string.IsNullOrEmpty(GetPassword()))
        {
            Skip = $"Necesita SQL Server levantado y las variables {UserVariable} y {PasswordVariable}.";
        }
    }

    public static string? GetUser() => Environment.GetEnvironmentVariable(UserVariable);

    public static string? GetPassword() => Environment.GetEnvironmentVariable(PasswordVariable);
}