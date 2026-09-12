namespace Gateway.Core;

/// Tipos de dato soportados en el CSV de tags. El nombre coincide con la
/// columna DATA_TYPE para que Enum.Parse los reconozca directo.
public enum TagDataType
{
    Double,
    Boolean,
    Int32,
    String
}

/// Nivel de acceso de un tag en el gateway. Acotado a mostrar u ocultar:
/// el gateway es de solo lectura hasta Fase 8, asi que esto nunca habilita
/// escritura, solo si el tag se publica o no como nodo UA.
public enum TagAccessLevel
{
    Read,
    Hidden
}
/// Una fila del CSV de tags. Los campos nuevos de la version extendida
/// (Fase 3) tienen default para no romper las llamadas existentes que
/// todavia construyen un TagDefinition solo con los cinco campos originales.
public sealed record TagDefinition(
    string OpcUaName,
    string OpcDaName,
    TagDataType DataType,
    double Multiplier,
    double Offset,
    string EngineeringUnit = "",
    int ScanRateMs = 0,
    double Deadband = 0,
    TagAccessLevel AccessLevel = TagAccessLevel.Read,
    string Description = "",
    bool Enabled = true,

    // Cuanto puede pasar sin refresco antes de degradar este tag por
    // antiguedad. null significa no degradar nunca (V2-11): es el caso de los
    // tags SQL, donde la calidad la manda la columna Q y no el reloj.
    // No sale del CSV: lo completa el host segun el origen del tag.
    TimeSpan? StaleAfter = null);
