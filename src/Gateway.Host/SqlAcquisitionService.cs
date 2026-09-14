using System.Diagnostics;
using Gateway.Core;
using Gateway.Sql;
using Serilog;

namespace Gateway.Host;

/// <summary>
/// Duenio del ciclo de polling SQL y del estado del vinculo. Hermano de
/// DaAcquisitionService: mismo rol, mismo SourceLinkStatus, otro ritmo y otra
/// fuente.
/// </summary>
/// <remarks>
/// Hilo propio y timeout propio: es el invariante 8. Una consulta que tarda
/// diez segundos no puede frenar la lectura DA ni la publicacion UA, asi que
/// este loop no comparte nada con ellas salvo la cache.
///
/// Misma disciplina de concurrencia que el lado DA: los long con Interlocked y
/// el resto volatile, porque todo campo se escribe desde este hilo y se lee
/// desde el que arma el snapshot.
/// </remarks>
public sealed class SqlAcquisitionService
{
    private readonly TagCache _cache;
    private readonly SqlOptions _options;
    private readonly SqlTagMapper _mapper;

    private long _readCycles;
    private long _readFailures;
    private long _connections;
    private long _disconnections;

    private volatile int _reconnectAttempts;
    private volatile string? _lastError;

    private long _lastSuccessfulCycleTicks;
    private long _cycleStartedTicks;   // 0 = no hay ciclo en curso

    private volatile bool _connected;

    private long _lastCycleMicros;
    private long _totalCycleMicros;
    private long _maxCycleMicros;
    private long _lastCacheStampTicks;

    public SqlAcquisitionService(TagCache cache, SqlOptions options)
    {
        _cache = cache;
        _options = options;
        // Se arma una vez y no por ciclo: resuelve la zona horaria, que es una
        // busqueda en la tabla del sistema.
        _mapper = new SqlTagMapper(options);
    }

    public SourceLinkStatus GetStatus()
    {
        var cycles = Interlocked.Read(ref _readCycles);
        var startedTicks = Interlocked.Read(ref _cycleStartedTicks);
        var lastGoodTicks = Interlocked.Read(ref _lastSuccessfulCycleTicks);

        return new SourceLinkStatus(
            TagSource.Sql,
            State: DetermineState(startedTicks),
            LastSuccessfulCycleUtc: lastGoodTicks == 0
                ? null
                : new DateTime(lastGoodTicks, DateTimeKind.Utc),
            ReconnectAttempts: _reconnectAttempts,
            LastError: _lastError,
            ReadCycles: cycles,
            ReadFailures: Interlocked.Read(ref _readFailures),
            Connections: Interlocked.Read(ref _connections),
            Disconnections: Interlocked.Read(ref _disconnections),
            LastCycleMs: Interlocked.Read(ref _lastCycleMicros) / 1000d,
            AvgCycleMs: cycles == 0 ? 0 : Interlocked.Read(ref _totalCycleMicros) / 1000d / cycles,
            MaxCycleMs: Interlocked.Read(ref _maxCycleMicros) / 1000d,
            ConfiguredIntervalMs: _options.PollingIntervalSeconds * 1000,
            LastCacheStampUtc: Interlocked.Read(ref _lastCacheStampTicks) is var stamp && stamp == 0
                ? null
                : new DateTime(stamp, DateTimeKind.Utc));
    }

    /// <summary>
    /// Cuanto puede tardar un ciclo antes de considerarlo colgado.
    /// </summary>
/// <remarks>
/// Se deriva del CommandTimeout y no del intervalo de polling. La ventana que
/// se mide es la del ciclo —cycleStartedTicks se pone al empezar y se limpia al
/// terminar—, asi que la espera entre ciclos no participa: derivar de 30 s de
/// polling daria 150 s para algo que el timeout corta a los 10.
///
/// El doble del timeout, y no menos, porque una consulta que se pasa del
/// timeout ya cae sola en Reconnecting: avisar antes seria ruido sobre algo que
/// se resuelve solo. Lo que este umbral detecta es lo que el timeout no cubre
/// —el mapeo de 10.000 filas, la transferencia del reader—, que es el unico
/// tramo donde acá puede pasar lo mismo que en DA: colgado sin morir.
///
/// El piso de 20 s protege de un CommandTimeout configurado muy chico, que
/// haria saltar Stalled en ciclos sanos. Medido: con la base caida la deteccion
/// tarda el timeout (~10 s) y no el polling.
///
/// Queda afuera un Connect() colgado: ahi _connected todavia es false y el
/// estado sale Disconnected o Reconnecting, que no es falso pero no distingue
/// "colgado conectando" de "esperando para reintentar".
/// </remarks>
    private TimeSpan StallThreshold =>
        TimeSpan.FromSeconds(Math.Max(_options.CommandTimeoutSeconds * 2, 20));

    private LinkState DetermineState(long cycleStartedTicks)
    {
        if (!_connected)
            return _reconnectAttempts > 0 ? LinkState.Reconnecting : LinkState.Disconnected;

        if (cycleStartedTicks != 0)
        {
            var elapsed = DateTime.UtcNow - new DateTime(cycleStartedTicks, DateTimeKind.Utc);
            if (elapsed > StallThreshold) return LinkState.Stalled;
        }

        return LinkState.Connected;
    }

    /// <summary>
    /// Ciclo de polling: conecta, consulta y vuelca en la cache hasta que se
    /// pida el apagado. Si la consulta falla, reconecta solo (R5).
    /// </summary>
    /// <remarks>
    /// La conexion es unica y persistente, y por eso el SqlTagSource vive
    /// afuera del loop de sesiones y no se recrea: lo que se recrea es la
    /// conexion, adentro, porque Connect() descarta la anterior. Es la
    /// diferencia con DA, donde COM deja el objeto inservible y hay que tirar
    /// el driver entero.
    ///
    /// El fallo de una consulta es el mecanismo real de deteccion (V2-20): una
    /// conexion abierta no se entera de que se cayo la red hasta que falla una
    /// operacion.
    /// </remarks>
    public void Run(CancellationToken token)
    {
        using var source = new SqlTagSource(_options);

        var faultLogged = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                RunSession(source, token);
                faultLogged = false;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Interlocked.Increment(ref _readFailures);

                if (faultLogged)
                    Log.Warning("Sigue caido el vinculo con SQL Server: {Mensaje}", ex.Message);
                else
                    Log.Warning(ex, "Se corto el vinculo con SQL Server");

                faultLogged = true;
            }
            finally
            {
                if (_connected)
                {
                    _connected = false;
                    Interlocked.Increment(ref _disconnections);
                }
                Interlocked.Exchange(ref _cycleStartedTicks, 0);
            }

            if (token.IsCancellationRequested) break;

            _reconnectAttempts++;
            Log.Information("Reintentando conexion SQL en {Segundos} s", _options.ReconnectDelaySeconds);
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds));
        }

        Log.Information("Ciclo de polling SQL detenido");
    }

    /// <summary>
    /// Una sesion: vive mientras la base responda. Si falla, propaga y el
    /// llamador reconecta.
    /// </summary>
    private void RunSession(SqlTagSource source, CancellationToken token)
    {
        source.Connect();

        _connected = true;
        _reconnectAttempts = 0;
        _lastError = null;
        _loggedMissing.Clear();   // "una vez por sesion" de V2-21
        Interlocked.Increment(ref _connections);

        Log.Information("Driver SQL conectado a {Host}:{Puerto}, consultando cada {Segundos} s",
            _options.Host, _options.Port, _options.PollingIntervalSeconds);

        while (!token.IsCancellationRequested)
        {
            var startedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _cycleStartedTicks, startedUtc.Ticks);
            var cycleWatch = Stopwatch.StartNew();

            SqlMappingResult result;
            try
            {
                // El mapeo entra en la medicion del ciclo a proposito: con 10.000
                // filas no es gratis, y lo que interesa medir es cuanto tarda el
                // gateway en tener el dato disponible, no cuanto tarda la base.
                result = _mapper.Map(source.ReadRows());
                _cache.Update(TagSource.Sql, result.Samples);
                PublishMissingRows(result);
            }
            finally
            {
                Interlocked.Exchange(ref _cycleStartedTicks, 0);
            }

            RecordCycle(cycleWatch.Elapsed);
            Interlocked.Exchange(ref _lastCacheStampTicks, DateTime.UtcNow.Ticks);

            LogAnomalies(result);

            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(_options.PollingIntervalSeconds));
        }
    }

    /// <summary>
    /// Publica Bad para los tags declarados con origen SQL que la consulta no
    /// trajo (V2-21, primer caso; P9).
    /// </summary>
    /// <remarks>
    /// El cruce lo hace la cache, que es la unica que conoce las definiciones y
    /// el comparador por fuente; aca queda decidir que calidad se publica y
    /// avisar. Update por si sola no alcanza: solo toca lo que llega, asi que un
    /// tag ausente se quedaria en WaitingForInitialData —Bad, pero por otra
    /// causa y sin aviso—. La consulta trae la tabla entera, asi que "no vino"
    /// alcanza para concluir "no esta": no hace falta el paso intermedio por
    /// NotConnected que si necesita DA en el primer intento.
    /// </remarks>
    private void PublishMissingRows(SqlMappingResult result)
    {
        var missing = _cache.MissingTags(TagSource.Sql, result.Samples);

        if (missing.Count == 0)
            return;

        _cache.Update(TagSource.Sql, missing.ToDictionary(
            tag => tag,
            _ => TagSample.NoData(TagQuality.RowMissing)));

        // Una vez por sesion y no por ciclo: a 20 s de polling, avisar siempre
        // inunda el log. Una reconexion vuelve a avisar, que es correcto.
        foreach (var tag in missing.Where(_loggedMissing.Add))
            Log.Warning("El tag {Tag} esta declarado con origen SQL pero la consulta no lo trajo: revisar el CSV o la tabla", tag);
    }

    private readonly HashSet<string> _loggedMissing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Loguea los contadores del mapeo cuando cambian respecto del ciclo
    /// anterior.
    /// </summary>
    /// <remarks>
    /// "Una sola vez" de V2-18 y V2-19 se cumple asi: el estado entre ciclos
    /// vive aca, que es lo que el mapper explicitamente no hace. Se avisa
    /// cuando la cuenta cambia y no en cada ciclo, porque 10.000 filas con dos
    /// nulos estables serian dos lineas por minuto para siempre.
    /// </remarks>
    private void LogAnomalies(SqlMappingResult result)
    {
        var fingerprint = (result.NullValueCount, result.NullQualityCount,
            result.UnknownSubstatusCount, result.InvalidTimestampCount);

        if (fingerprint == _lastAnomalies) return;
        _lastAnomalies = fingerprint;

        if (fingerprint == default)
        {
            // Va el conteo y no solo "sin anomalias": con cero filas los cuatro
            // contadores tambien dan cero, y una tabla vacia se leeria igual que
            // una tabla sana. Paso de verdad al probar el paso 5.
            Log.Information("Ciclo SQL sin filas anomalas sobre {Filas} filas", result.Samples.Count);
            return;
        }

        Log.Warning(
            "Filas anomalas en el ciclo SQL: {Nulos} sin valor, {SinCalidad} sin calidad, "
            + "{Desconocidas} con substatus desconocido, {Invalidas} con timestamp inexistente",
            result.NullValueCount, result.NullQualityCount,
            result.UnknownSubstatusCount, result.InvalidTimestampCount);
    }

    // Solo lo toca el hilo de polling, asi que no necesita sincronizacion.
    private (int, int, int, int) _lastAnomalies = (-1, -1, -1, -1);

    private void RecordCycle(TimeSpan elapsed)
    {
        var micros = (long)(elapsed.TotalMilliseconds * 1000);

        Interlocked.Exchange(ref _lastCycleMicros, micros);
        Interlocked.Add(ref _totalCycleMicros, micros);
        Interlocked.Increment(ref _readCycles);
        Interlocked.Exchange(ref _lastSuccessfulCycleTicks, DateTime.UtcNow.Ticks);

        long observed;
        while (micros > (observed = Interlocked.Read(ref _maxCycleMicros)))
            Interlocked.CompareExchange(ref _maxCycleMicros, micros, observed);
    }
}