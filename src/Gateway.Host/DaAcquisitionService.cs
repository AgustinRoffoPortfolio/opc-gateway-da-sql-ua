using System.Diagnostics;
using Gateway.Core;
using Gateway.Da;
using Serilog;

namespace Gateway.Host;

/// <summary>
/// Duenio del ciclo de adquisicion DA y del estado del vinculo. Conecta, lee,
/// vuelca en la cache y reconecta solo; ademas lleva la cuenta de lo que pasa,
/// que es lo que despues alimenta el diagnostico.
/// </summary>
/// <remarks>
/// Los contadores viven aca y no en OpcDaTagSource porque el driver no decide
/// cuando reintentar ni cuanto esperar: esa politica es de esta clase, y contar
/// desde otro lado dejaria la mitad de la historia afuera.
///
/// Todo campo mutable se toca desde el hilo DA y se lee desde el hilo que arma
/// el snapshot. Los long van con Interlocked y el resto volatile: en un proceso
/// x86 un DateTime son 8 bytes y se puede leer a medio escribir.
/// </remarks>
public sealed class DaAcquisitionService
{
    private readonly TagCache _cache;
    private readonly DaOptions _options;

    private long _readCycles;
    private long _readFailures;
    private long _connections;
    private long _disconnections;

    private volatile int _reconnectAttempts;
    private volatile string? _lastError;

    // Se escriben como long (ticks) porque volatile no admite DateTime.
    private long _lastSuccessfulCycleTicks;
    private long _cycleStartedTicks;   // 0 = no hay ciclo en curso

    private volatile bool _connected;

    // Metricas de duracion del ciclo, en milisegundos.
    private long _lastCycleMicros;
    private long _totalCycleMicros;

    // Sello de la ultima actualizacion de cache, para medir la latencia
    // cache->cliente. Se publica como nodo UA y el cliente calcula UtcNow menos
    // este valor al recibir la notificacion: como los dos procesos comparten el
    // reloj de la maquina, esa resta da la latencia real de la mitad UA (espera
    // del timer de publicacion + sampling del MonitoredItem).
    private long _lastCacheStampTicks;
    private long _maxCycleMicros;

    public DaAcquisitionService(TagCache cache, DaOptions options)
    {
        _cache = cache;
        _options = options;
    }

    /// <summary>
    /// Foto del vinculo para el diagnostico. Se puede llamar desde cualquier
    /// hilo mientras la adquisicion corre.
    /// </summary>
    /// <remarks>
    /// El estado Stalled sale de comparar contra el instante en que empezo el
    /// ciclo en curso: si una lectura no vuelve, el vinculo COM sigue diciendo
    /// que esta vivo y la pagina mostraria "conectado" con el hilo bloqueado.
    /// Esto no destraba la llamada -- para eso hace falta un timeout sobre COM,
    /// que sigue siendo deuda -- pero evita afirmar una salud que no se sabe.
    /// </remarks>
    public DaLinkStatus GetStatus()
    {
        var cycles = Interlocked.Read(ref _readCycles);
        var startedTicks = Interlocked.Read(ref _cycleStartedTicks);
        var lastGoodTicks = Interlocked.Read(ref _lastSuccessfulCycleTicks);

        var state = DetermineState(startedTicks);

        return new DaLinkStatus(
            State: state,
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
            ConfiguredIntervalMs: _options.UpdateRateMs,
            LastCacheStampUtc: Interlocked.Read(ref _lastCacheStampTicks) is var stamp && stamp == 0
                ? null
                : new DateTime(stamp, DateTimeKind.Utc));
    }

    /// <summary>
    /// Cuanto puede tardar un ciclo antes de considerarlo colgado. Multiplo del
    /// intervalo configurado y no un numero fijo: con UpdateRate de 100 ms un
    /// segundo ya es anormal, con 5000 ms es lo esperable.
    /// </summary>
    private TimeSpan StallThreshold =>
        TimeSpan.FromMilliseconds(Math.Max(_options.UpdateRateMs * 5, 10_000));

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
    /// Ciclo de adquisicion DA: conecta, lee y vuelca en la cache hasta que se
    /// pida el apagado. Si el vinculo se cae, reconecta solo.
    /// </summary>
    /// <remarks>
    /// La reconexion recrea el driver entero en vez de reintentar la lectura:
    /// cuando el servidor DA muere, COM devuelve 0x800706BA (RPC server
    /// unavailable) y el objeto queda inservible, no en un error transitorio.
    /// Reintentar sobre el mismo grupo falla para siempre.
    ///
    /// Mientras no hay vinculo el gateway no publica nada distinto a proposito:
    /// la cache degrada sola por antiguedad y esa degradacion llega al cliente
    /// UA en el StatusCode. Este loop no toca la cache, solo la alimenta.
    /// </remarks>
    public void Run(CancellationToken token)
    {
        // El stack completo sirve una vez; repetido cada pocos segundos mientras
        // el DA sigue caido tapa el log y esconde lo que si cambia. Se loguea
        // entero al primer fallo y despues solo el mensaje, hasta que una
        // conexion buena reinicie el ciclo.
        var faultLogged = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                RunSession(token);
                faultLogged = false;
            }
            catch (Exception ex)
            {
                // Cualquier fallo del vinculo cae aca: no se distingue por tipo
                // de excepcion porque COM tiene muchas formas de decir lo mismo,
                // y la respuesta es siempre la misma: tirar todo y reconectar.
                _lastError = ex.Message;
                Interlocked.Increment(ref _readFailures);

                if (faultLogged)
                    Log.Warning("Sigue caido el vinculo con el servidor DA: {Mensaje}", ex.Message);
                else
                    Log.Warning(ex, "Se corto el vinculo con el servidor DA");

                faultLogged = true;
            }
            finally
            {
                // Sale del try tanto por error como por apagado: en los dos casos
                // el vinculo dejo de estar vivo y no hay ciclo en curso.
                if (_connected)
                {
                    _connected = false;
                    Interlocked.Increment(ref _disconnections);
                }
                Interlocked.Exchange(ref _cycleStartedTicks, 0);
            }

            if (token.IsCancellationRequested) break;

            _reconnectAttempts++;
            Log.Information("Reintentando conexion DA en {Ms} ms", _options.ReconnectDelayMs);
            token.WaitHandle.WaitOne(_options.ReconnectDelayMs);
        }

        Log.Information("Ciclo de adquisicion DA detenido");
    }

    /// <summary>
    /// Una sesion de adquisicion: vive mientras el vinculo DA funcione. Si falla,
    /// propaga y el llamador se encarga de reconectar.
    /// </summary>
    /// <remarks>
    /// Las altas rechazadas se reintentan cada ItemRetryIntervalMs en vez de
    /// darse por definitivas. Un rechazo tiene dos causas que desde una sola
    /// respuesta no se distinguen: el ItemID no existe (error de configuracion,
    /// permanente) o el servidor DA todavia no tiene su lista lista (transitorio,
    /// tipico cuando COM acaba de relanzarlo o cuando los dos procesos arrancan
    /// juntos al bootear). Reintentar resuelve el segundo caso solo y deja el
    /// primero como estaba. De paso cubre el caso de planta en que aparecen items
    /// nuevos con el gateway ya corriendo, sin obligar a reiniciarlo y sacar de
    /// servicio a los clientes UA.
    /// </remarks>
    private void RunSession(CancellationToken token)
    {
        // El using es lo que libera las referencias COM al salir, tanto por error
        // como por apagado. Sin esto cada reconexion dejaria un servidor colgado.
        using var source = new OpcDaTagSource(_options.ProgId);

        source.Connect(updateRateMs: _options.UpdateRateMs);

        _connected = true;
        _reconnectAttempts = 0;
        _lastError = null;
        Interlocked.Increment(ref _connections);

        var pending = TryAddItems(source, _cache.SourceTags(TagSource.OpcDa));

        Log.Information("Driver DA conectado a {ProgId}, leyendo cada {Ms} ms",
            _options.ProgId, _options.UpdateRateMs);

        var nextItemRetry = DateTime.UtcNow.AddMilliseconds(_options.ItemRetryIntervalMs);

        while (!token.IsCancellationRequested)
        {
            var startedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _cycleStartedTicks, startedUtc.Ticks);
            // La duracion se mide con Stopwatch y no restando DateTime.UtcNow:
            // ese reloj avanza a saltos de ~15,6 ms en Windows, asi que un ciclo
            // corto daria 0 o 15.600 us y nada en el medio. startedUtc se sigue
            // usando, pero para marcar CUANDO arranco el ciclo, no cuanto duro.
            var cycleWatch = Stopwatch.StartNew();
            try
            {
                _cache.Update(TagSource.OpcDa, source.ReadAll());
            }
            finally
            {
                // Se limpia pase lo que pase: si la lectura tira, el ciclo dejo
                // de estar en curso igual, y dejar la marca puesta reportaria un
                // cuelgue donde en realidad hubo un error.
                Interlocked.Exchange(ref _cycleStartedTicks, 0);
            }

            RecordCycle(cycleWatch.Elapsed);
            // Despues de RecordCycle: recien aca la cache tiene el TagState.
            Interlocked.Exchange(ref _lastCacheStampTicks, DateTime.UtcNow.Ticks);

            if (pending.Count > 0 && DateTime.UtcNow >= nextItemRetry)
            {
                var before = pending.Count;
                pending = TryAddItems(source, pending, firstAttempt: false);

                if (pending.Count < before)
                    Log.Information("Se dieron de alta {Count} items que estaban rechazados",
                        before - pending.Count);
                nextItemRetry = DateTime.UtcNow.AddMilliseconds(_options.ItemRetryIntervalMs);
            }

            token.WaitHandle.WaitOne(_options.UpdateRateMs);
        }
    }

    /// <summary>
    /// Anota la duracion de un ciclo terminado bien.
    /// </summary>
    /// <remarks>
    /// En microsegundos y no en milisegundos: con 8000 tags el ciclo ronda las
    /// decenas de ms, pero con pocos tags puede dar menos de 1 ms y un promedio
    /// entero quedaria clavado en cero.
    /// </remarks>
    private void RecordCycle(TimeSpan elapsed)
    {
        var micros = (long)(elapsed.TotalMilliseconds * 1000);

        Interlocked.Exchange(ref _lastCycleMicros, micros);
        Interlocked.Add(ref _totalCycleMicros, micros);
        Interlocked.Increment(ref _readCycles);
        Interlocked.Exchange(ref _lastSuccessfulCycleTicks, DateTime.UtcNow.Ticks);

        // Compare-and-swap en lugar de leer y escribir: entre las dos operaciones
        // otro hilo podria haber subido el maximo y lo estariamos pisando.
        long observed;
        while (micros > (observed = Interlocked.Read(ref _maxCycleMicros)))
            Interlocked.CompareExchange(ref _maxCycleMicros, micros, observed);
    }

    /// <summary>
    /// Intenta dar de alta los items indicados y devuelve los que siguen rechazados.
    /// </summary>
    /// <remarks>
    /// Los rechazados se marcan en la cache y no solo en el log: sin eso el tag
    /// se queda sin muestras y la degradacion por antiguedad lo reporta como
    /// problema de comunicacion, mandando a revisar la red por un ItemID mal
    /// escrito.
    /// </remarks>
    /// <param name="firstAttempt">
    /// Primer alta de la sesion. Un rechazo en este momento no permite concluir
    /// que el ItemID no exista: si el servidor DA acaba de arrancar puede no
    /// tener su lista lista todavia. Se marca como "sin datos" y recien un
    /// rechazo en el reintento posterior lo confirma como error de configuracion.
    /// </param>
    private IReadOnlyList<string> TryAddItems(
        OpcDaTagSource source, IEnumerable<string> itemIds, bool firstAttempt = true)
    {
        var rejected = source.AddItems(itemIds);
        if (rejected.Count == 0) return rejected;

        // El detalle item por item sirve la primera vez; repetido en cada
        // reintento son cientos de lineas por hora diciendo lo mismo. Despues
        // alcanza el conteo.
        if (firstAttempt)
            foreach (var itemId in rejected)
                Log.Warning("El servidor DA rechazo el item {ItemId}: fuera de servicio, se reintenta en {Ms} ms",
                    itemId, _options.ItemRetryIntervalMs);
        else
            Log.Information("Siguen rechazados {Count} items, proximo reintento en {Ms} ms",
                rejected.Count, _options.ItemRetryIntervalMs);

        // En el primer intento el rechazo todavia no distingue "no existe" de
        // "el servidor no termino de levantar", asi que se publica la duda y no
        // una conclusion. Confirmado por el reintento, si pasa a error de
        // configuracion.
        var quality = firstAttempt ? TagQuality.NotConnected : TagQuality.ItemRejected;

        _cache.Update(TagSource.OpcDa, rejected.ToDictionary(
            itemId => itemId,
            _ => TagSample.NoData(quality)));

        return rejected;
    }
}