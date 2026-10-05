using Gateway.Core;

namespace Gateway.Sql;

/// Resultado de mapear un ciclo entero: las muestras mas los contadores de lo
/// que salio raro.
///
/// Contadores y no una lista de mensajes: son del orden de 10.000 filas por
/// ciclo y una lista de strings seria basura por ciclo para algo que el host va
/// a loguear agregado igual. El "una sola vez" que piden V2-18 y V2-19 necesita
/// estado entre ciclos, y eso vive en el host, no aca.
///
/// Anomalies es la excepcion a "contadores y no listas": el aviso del host
/// tiene que filtrar a los tags declarados en el CSV (B6.5) y los contadores
/// son de la tabla entera. Lleva solo los tags anomalos. Normalmente son pocos,
/// pero en el peor caso (toda la tabla anomala) son tantas entradas como
/// filas: un diccionario de 10.000 entradas cada decenas de segundos es
/// aceptable a este ritmo de polling.
///
/// Anomalies es por tag, no por fila. Con un TAG duplicado la ultima fila pisa
/// a la anterior, igual que en Samples, y la suma de flags puede no coincidir
/// con los contadores, que cuentan filas. En la tabla real no puede pasar:
/// TAG es clave primaria (P1).
public sealed record SqlMappingResult(
    IReadOnlyDictionary<string, TagSample> Samples,
    int NullValueCount,
    int NullQualityCount,
    int UnknownSubstatusCount,
    int InvalidTimestampCount,
    IReadOnlyDictionary<string, SqlRowAnomaly> Anomalies);

/// Que salio raro en una fila. Flags porque una misma fila puede traer V y Q
/// en NULL a la vez.
[Flags]
public enum SqlRowAnomaly
{
    None = 0,
    NullValue = 1,
    NullQuality = 2,
    UnknownSubstatus = 4,
    InvalidTimestamp = 8
}

/// Convierte filas crudas de CURRENT_VALUES en muestras del gateway.
///
/// Funcion pura con estado de solo lectura: no abre conexiones, no loguea y no
/// conoce las definiciones del CSV. El filtrado a los tags declarados (R3) lo
/// hace TagCache.Update al descartar lo que no pidio, asi que el mapeo entrega
/// la tabla entera y no necesita saber que tags existen.
public sealed class SqlTagMapper
{
    private readonly TimeZoneInfo _sourceZone;

    /// La zona se resuelve una sola vez (V2-18): FindSystemTimeZoneById recorre
    /// la tabla de zonas del sistema, y hacerlo por fila serian 10.000 busquedas
    /// por ciclo para un dato que no cambia. Ya se valido al arrancar, asi que
    /// aca no vuelve a fallar.
    public SqlTagMapper(SqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _sourceZone = string.IsNullOrWhiteSpace(options.TimeZone)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
    }

    public SqlMappingResult Map(IReadOnlyList<SqlTagRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        // OrdinalIgnoreCase: TAG es clave primaria y SQL Server no distingue
        // mayusculas, asi que la tabla no puede traer dos formas del mismo
        // nombre. Si igual llegaran dos, quedarse con una es lo correcto; con
        // el comparador por defecto entrarian las dos y una pisaria a la otra
        // recien en la cache, mas lejos del origen. Es el mismo criterio que
        // usan las definiciones y el cruce de ausentes
        // (V2-17): un solo lugar decide como se comparan los nombres SQL.
        var samples = new Dictionary<string, TagSample>(rows.Count, TagKeyComparer.ComparerFor(TagSource.Sql));

        var nullValues = 0;
        var nullQualities = 0;
        var unknownSubstatuses = 0;
        var invalidTimestamps = 0;
        var anomalies = new Dictionary<string, SqlRowAnomaly>(TagKeyComparer.ComparerFor(TagSource.Sql));

        foreach (var row in rows)
        {
            // Los flags de la fila salen de comparar los contadores antes y
            // despues, asi los helpers siguen contando sin saber de flags.
            var (nullQualitiesBefore, unknownBefore, invalidBefore) =
                (nullQualities, unknownSubstatuses, invalidTimestamps);

            var quality = MapQuality(row.Q, ref nullQualities, ref unknownSubstatuses);
            var timestamp = MapTimestamp(row.Ts, ref quality, ref invalidTimestamps);

            var anomaly = SqlRowAnomaly.None;
            if (row.V is null) anomaly |= SqlRowAnomaly.NullValue;
            if (nullQualities != nullQualitiesBefore) anomaly |= SqlRowAnomaly.NullQuality;
            if (unknownSubstatuses != unknownBefore) anomaly |= SqlRowAnomaly.UnknownSubstatus;
            if (invalidTimestamps != invalidBefore) anomaly |= SqlRowAnomaly.InvalidTimestamp;

            // Igual que en samples, un duplicado pisa al anterior; si el que
            // queda esta sano, no puede arrastrar la anomalia del otro.
            if (anomaly == SqlRowAnomaly.None) anomalies.Remove(row.Tag);
            else anomalies[row.Tag] = anomaly;

            // V en NULL: no hay medicion. Se publica sin valor y con calidad no
            // mejor que Uncertain; la cache conserva el ultimo valor bueno y su
            // SourceTimestamp, que no avanza porque no hay dato nuevo que fechar
            // (V2-16). El timestamp que va aca igual no se usa en ese caso.
            if (row.V is null)
            {
                nullValues++;
                samples[row.Tag] = new TagSample(null, Downgrade(quality), timestamp);
                continue;
            }

            samples[row.Tag] = new TagSample(row.V.Value, quality, timestamp);
        }

        return new SqlMappingResult(
            samples, nullValues, nullQualities, unknownSubstatuses, invalidTimestamps, anomalies);
    }

    /// Q en NULL: hay medicion pero no hay codigo de calidad que mapear. Es una
    /// duda, no una certeza de error, asi que Uncertain y no Bad (principio 3,
    /// V2-16). El valor y el timestamp se actualizan normalmente, por eso sale
    /// Uncertain sin substatus y no LastUsableValue (V2-35).
    private static TagQuality MapQuality(short? code, ref int nullCount, ref int unknownCount)
    {
        if (code is null)
        {
            nullCount++;
            return TagQuality.QualityNull;
        }

        var quality = TagQuality.FromDaCode(code.Value, out var unknownSubstatus);
        if (unknownSubstatus) unknownCount++;

        return quality;
    }

    /// TS viene en hora local de la aplicacion de origen (P4) y el
    /// SourceTimestamp de UA es UTC por definicion (V2-18).
    private DateTime MapTimestamp(DateTime ts, ref TagQuality quality, ref int invalidCount)
    {
        // Un datetime de SQL Server llega con Kind Unspecified. ToUniversalTime()
        // asumiria la zona de la maquina en silencio, que es exactamente lo que
        // el parametro existe para no hacer.
        var unspecified = DateTime.SpecifyKind(ts, DateTimeKind.Unspecified);

        // Hora inexistente: la que se saltea al adelantar el reloj. Ese instante
        // no existe en la zona, asi que no hay UTC al que convertirlo. Se publica
        // el valor con la calidad degradada -un timestamp imposible es una duda,
        // no un error- en vez de inventar un instante.
        if (_sourceZone.IsInvalidTime(unspecified))
        {
            invalidCount++;
            quality = Downgrade(quality);
            return unspecified;
        }

        // Una hora ambigua -la que se repite al atrasar- no se chequea: la
        // conversion elige el horario estandar por default, que es la regla que
        // ya tomo V2-18.
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, _sourceZone);
    }

    /// Baja una calidad buena a Uncertain conservando el limit, y deja en paz
    /// todo lo que ya era Uncertain o peor. La regla es la misma que usa la
    /// cache al degradar: degradar nunca mejora un StatusCode, y una calidad
    /// que ya explica algo mas especifico no se pisa.
    private static TagQuality Downgrade(TagQuality quality) =>
        quality.Master == QualityMaster.Good
            ? TagQuality.LastUsableValue with { Limit = quality.Limit }
            : quality;
}