namespace Gateway.Core;

/// Tipos de dato soportados en el CSV de tags. El nombre coincide con la
/// columna DATA_TYPE para que Enum.Parse los reconozca directo.
public enum TagDataType
{
    Double,
    Boolean,
    Int32,
    String,

    // Agregado en la v2 (V2-14). La columna V de CURR_DATA es real: un float de
    // 4 bytes, unos 7 digitos significativos. Publicarlo como Double mostraria
    // digitos que la medicion no tiene. Va al final para no renumerar los que
    // ya existen.
    Float
}

/// De donde sale el valor de un tag. Se declara en la columna SOURCE del CSV
/// y no tiene default: un tag mal clasificado se ve desde OPC UA igual que una
/// fuente caida, asi que es mas barato que falle la carga (V2-5).
public enum TagSource
{
    OpcDa,
    Sql
}

/// Identifica un tag dentro del universo de todas las fuentes. El nombre de
/// origen solo no alcanza: los dos origenes vienen del mismo mundo y es
/// razonable que un ItemID de DA y una fila de CURR_DATA se llamen igual, y sin
/// el origen adentro de la clave una fuente escribiria sobre los tags de la
/// otra (V2-12).
///
/// Es record struct y no tupla porque los miembros se leen por nombre en todos
/// los usos; y struct y no clase porque se construye una vez por muestra por
/// ciclo -miles por ciclo con la tabla entera- y asi no genera basura.
public readonly record struct TagKey(TagSource Source, string SourceTag);

/// Comparador de TagKey que respeta la semantica de nombres de cada fuente
/// (V2-17): SQL Server no distingue mayusculas, OPC DA si.
///
/// Un Dictionary tiene un unico comparador para toda la clave, asi que la
/// decision por fuente tiene que vivir aca adentro y no en dos estructuras
/// separadas. El origen siempre se compara exacto.
public sealed class TagKeyComparer : IEqualityComparer<TagKey>
{
    public static readonly TagKeyComparer Instance = new();

    private TagKeyComparer() { }

    /// Criterio de comparacion de nombres para una fuente. Publico porque el
    /// cruce de declarados contra recibidos (MissingTags) tiene que usar el
    /// mismo, y si cada lugar elige el suyo empiezan a divergir.
    public static StringComparer ComparerFor(TagSource source) =>
        source == TagSource.Sql ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public bool Equals(TagKey x, TagKey y) =>
        x.Source == y.Source && ComparerFor(x.Source).Equals(x.SourceTag, y.SourceTag);

    // El hash tiene que ser consistente con Equals: si dos claves SQL que
    // difieren en mayusculas son iguales, tienen que caer en el mismo bucket.
    public int GetHashCode(TagKey key) =>
        HashCode.Combine(key.Source, ComparerFor(key.Source).GetHashCode(key.SourceTag));
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
    TagSource Source,
    string SourceTag,
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
