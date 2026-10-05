namespace Gateway.Core;

/// <summary>Nivel general de la calidad DA (bits 7-6).</summary>
public enum QualityMaster
{
    Bad = 0,
    Uncertain = 64,
    Error = 128,   // reservado por la spec, no deberia aparecer nunca
    Good = 192
}

/// <summary>
/// Causa concreta dentro del nivel (bits 5-2). Los valores son los de la
/// especificacion; el substatus ya implica el master.
/// </summary>
public enum QualitySubstatus
{
    Bad = 0,
    BadConfigurationError = 4,
    BadNotConnected = 8,
    BadDeviceFailure = 12,
    BadSensorFailure = 16,
    BadLastKnown = 20,
    BadCommFailure = 24,
    BadOutOfService = 28,
    BadWaitingForInitialData = 32,
    Uncertain = 64,
    UncertainLastUsableValue = 68,
    UncertainSensorNotAccurate = 80,
    UncertainEngineeringUnitsExceeded = 84,
    UncertainSubNormal = 88,
    Good = 192,
    GoodLocalOverride = 216
}

/// <summary>Si el valor esta pegado a un limite (bits 1-0).</summary>
public enum QualityLimit
{
    NotLimited = 0,
    Low = 1,
    High = 2,
    Constant = 3
}

/// <summary>
/// Calidad de una muestra, con la misma estructura de tres campos que define
/// OPC DA. No se aplana a un enum unico porque los tres campos son
/// independientes: aplanarlos dejaria sin lugar al limit status.
/// Los 8 bits de fabricante se descartan (lo indica la spec, Parte 8 A.3.2.3).
/// </summary>
public readonly record struct TagQuality(
    QualityMaster Master,
    QualitySubstatus Substatus,
    QualityLimit Limit)
{
    /// Calidad buena y sin limites, el caso normal.
    public static readonly TagQuality Good =
        new(QualityMaster.Good, QualitySubstatus.Good, QualityLimit.NotLimited);

    /// El gateway todavia no leyo este tag.
    public static readonly TagQuality WaitingForInitialData =
        new(QualityMaster.Bad, QualitySubstatus.BadWaitingForInitialData, QualityLimit.NotLimited);

    /// El servidor DA rechazo el ItemID al darlo de alta: existe en el CSV pero
    /// no del otro lado. Es un error de configuracion, no de comunicacion, y se
    /// distingue para que nadie salga a revisar la red por un tag mal escrito.
    public static readonly TagQuality ItemRejected =
        new(QualityMaster.Bad, QualitySubstatus.BadConfigurationError, QualityLimit.NotLimited);

    /// Se pidio un tag que la cache no conoce.
    public static readonly TagQuality UnknownTag =
        new(QualityMaster.Bad, QualitySubstatus.BadConfigurationError, QualityLimit.NotLimited);

    /// El tag esta declarado en el CSV con origen SQL pero la consulta no lo
    /// trajo: existe de este lado y no en la tabla. Mismo StatusCode que
    /// ItemRejected —es el mismo error de configuracion— con otro nombre,
    /// porque un log que dice "rechazado" manda a buscar un servidor DA que
    /// en este camino no existe.
    public static readonly TagQuality RowMissing =
        new(QualityMaster.Bad, QualitySubstatus.BadConfigurationError, QualityLimit.NotLimited);

    /// Llego un valor pero no convierte al DataType declarado en el CSV.
    public static readonly TagQuality ConversionError =
        new(QualityMaster.Bad, QualitySubstatus.BadConfigurationError, QualityLimit.NotLimited);

    /// El vinculo con el servidor DA no esta disponible y este tag nunca tuvo
    /// un valor bueno. Distinta de WaitingForInitialData: aquella dice "todavia
    /// no leimos", esta dice "leimos y no hay nadie del otro lado".
    public static readonly TagQuality NotConnected =
        new(QualityMaster.Bad, QualitySubstatus.BadNotConnected, QualityLimit.NotLimited);

    /// El valor que se esta devolviendo es el ultimo que se leyo bien, pero ya
    /// no se esta refrescando. El dato todavia sirve para orientarse; no sirve
    /// para decidir. Es exactamente lo que Uncertain significa en OPC.
    public static readonly TagQuality LastUsableValue =
        new(QualityMaster.Uncertain, QualitySubstatus.UncertainLastUsableValue, QualityLimit.NotLimited);

    /// La columna Q de la tabla SQL vino en NULL: hay medicion fresca pero no
    /// hay codigo de calidad que mapear. Uncertain sin substatus y no
    /// LastUsableValue (V2-35): ese substatus le diria al cliente que el valor
    /// ya no se refresca, y aca se refresca en cada ciclo.
    public static readonly TagQuality QualityNull =
        new(QualityMaster.Uncertain, QualitySubstatus.Uncertain, QualityLimit.NotLimited);

    /// <summary>
    /// Descompone un codigo de calidad OPC DA crudo (la columna Q de la tabla
    /// SQL) en los tres campos. El driver DA no la usa: el SDK ya le entrega la
    /// calidad desarmada. Los valores de los enums son los de la spec, asi que
    /// decodificar bits es convertir a esos mismos enums, sin tabla aparte.
    /// <paramref name="unknownSubstatus"/> avisa que el substatus no estaba
    /// previsto; el log es decision de quien llama, Core no tiene logger.
    /// </summary>
    public static TagQuality FromDaCode(int code, out bool unknownSubstatus)
    {
        // Se descartan los 8 bits de fabricante (spec Parte 8 A.3.2.3). De paso
        // cubre el caso de un smallint negativo, que hoy no aparece en la tabla
        // real pero que rompería en silencio cualquier comparacion contra 192.
        var bits = code & 0xFF;

        var master = (QualityMaster)(bits & 0b1100_0000);
        var substatusBits = bits & 0b1111_1100;
        var limit = (QualityLimit)(bits & 0b0000_0011);

        unknownSubstatus = !Enum.IsDefined<QualitySubstatus>((QualitySubstatus)substatusBits);

        // Un substatus no previsto no invalida la muestra: el master, que es lo
        // que decide si el dato sirve, sigue siendo valido. El substatus cae al
        // valor base de su master. Error no tiene substatus propio en la spec:
        // se conserva el master para que la anomalia quede visible.
        var substatus = unknownSubstatus
            ? master == QualityMaster.Error
                ? QualitySubstatus.Bad
                : (QualitySubstatus)(bits & 0b1100_0000)
            : (QualitySubstatus)substatusBits;

        return new TagQuality(master, substatus, limit);
    }

    /// <summary>El valor sirve para transformar (multiplicador y offset).</summary>
    public bool IsUsable =>
        Master is QualityMaster.Good or QualityMaster.Uncertain;
}