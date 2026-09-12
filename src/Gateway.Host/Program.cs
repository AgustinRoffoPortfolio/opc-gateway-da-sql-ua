using System.Net;
using System.Runtime.InteropServices;
using Gateway.Core;
using Gateway.Ua;
using Microsoft.Extensions.Configuration;
using Opc.Ua;
using Opc.Ua.Configuration;
using Serilog;
using Gateway.Da;
using Gateway.Host;
using Gateway.Web;

// El driver OPC DA exige un proceso de 32 bits: esto tiene que fallar
// ruidosamente si algun dia alguien saca el PlatformTarget del csproj.
Console.WriteLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Is64BitProcess: {Environment.Is64BitProcess}");

// Prueba manual del driver DA aislado: corta antes de levantar el servidor UA.
if (args.Contains("--da-only"))
{
    using var daSource = new OpcDaTagSource("Matrikon.OPC.Simulation.1");
    daSource.Connect();
    Console.WriteLine($"IsConnected: {daSource.IsConnected}");

    var rejected = daSource.AddItems(
        ["Random.Real8", "Random.Int4", "Random.Boolean", "Random.String", "Tag.Que.No.Existe"]);
    Console.WriteLine($"Rechazados: {string.Join(", ", rejected)}");

    // Dos lecturas: la primera cae antes del primer refresco de la cache del
    // servidor, la segunda ya trae datos buenos.
    for (var pass = 1; pass <= 2; pass++)
    {
        Console.WriteLine($"--- Lectura {pass} ---");
        foreach (var (itemId, sample) in daSource.ReadAll())
            Console.WriteLine(
                $"{itemId,-16} {sample.Value,-24} {sample.Quality.Master}/{sample.Quality.Substatus}" +
                $" usable={sample.Quality.IsUsable} src={sample.SourceTimestamp:O}");

        if (pass == 1) Thread.Sleep(2000);
    }

    return;
}

// Lee appsettings.json desde la carpeta de salida y lo mapea a UaOptions.
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var options = configuration.GetSection("Ua").Get<UaOptions>()
    ?? throw new InvalidOperationException("Falta la seccion 'Ua' en appsettings.json");

var daOptions = configuration.GetSection("Da").Get<DaOptions>()
    ?? throw new InvalidOperationException("Falta la seccion 'Da' en appsettings.json");

// La web es opcional: si falta la seccion se usan los defaults del record en
// vez de tirar el arranque abajo, porque un gateway sin pagina de diagnostico
// sigue siendo un gateway.
var webOptions = configuration.GetSection("Web").Get<WebOptions>() ?? new WebOptions();

// Logger de toda la aplicacion.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // El stack es muy verboso, y firma sus mensajes con el nombre en runtime
    // de la clase que lo hospeda. Subir esto a Information un rato es la forma
    // de auditar el handshake: en ese nivel el arranque imprime "Certificate
    // Domain names", que es la lista contra la que se valida el dominio.
    .MinimumLevel.Override("Opc.Ua", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Gateway.Ua.UaServer", Serilog.Events.LogEventLevel.Warning)
    // El ciclo de vida del host web anuncia su arranque con mensajes de
    // aplicacion web ("Application started", "Hosting environment") que en un
    // gateway OPC confunden: la web es accesorio, no el producto. Si el
    // arranque falla de verdad, eso sale en Error y se sigue viendo.
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Warning)
    // La pagina pide el diagnostico una vez por segundo y ASP.NET Core loguea
    // cuatro lineas por request: sin esto el log del gateway queda 99% ruido HTTP
    // y los WRN del vinculo DA, que son los que importan, quedan sepultados.
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    // Falsa alarma del stack UA, verificada contra el fuente en el tag
    // 1.5.378.156: Subscription.cs:1007 lo emite como Error cuando un
    // PublishRequest queda sin notificaciones, pero SubscriptionManager.cs:1113
    // lo trata como "false alarm or race condition", re-encola el request y lo
    // loguea en Trace. El mismo evento con dos niveles; el alto es el
    // incorrecto. No se pierden datos ni requests. Solo aparece con clientes
    // que mantienen varias suscripciones sobre una sesion: 15 min y 324.000
    // notificaciones con UaLoadClient no lo produjeron ni una vez. Se filtra
    // este mensaje puntual y no la categoria, para no perder errores reales
    // del servidor UA. Detalle en docs/operacion.md.
    .Filter.ByExcluding(logEvent =>
        logEvent.MessageTemplate.Text.StartsWith(
            "Oops! MonitoredItems queued",
            StringComparison.Ordinal))
    .WriteTo.Console()
    .CreateLogger();

// El stack OPC UA no tiene logger propio: usa el que le pasemos por aca.
var telemetry = DefaultTelemetry.Create(builder => builder.AddSerilog(Log.Logger));

// Identidad de la aplicacion ante la red OPC UA.
var application = new ApplicationInstance(telemetry)
{
    ApplicationName = options.ApplicationName,
    ApplicationType = ApplicationType.Server
};

// La PKI vive en la raiz del repo, resuelta por ruta absoluta desde la
// ubicacion del ejecutable (no desde el working directory): si "dotnet run"
// y correr el .exe desde bin/ resolvieran carpetas distintas, el servidor
// regeneraria su certificado en cada modo y romperia la confianza ya
// establecida con los clientes.
var pkiRoot = Path.Combine(ConfigPathResolver.ResolveDataRoot(), options.PkiRoot);

// La API nueva recibe una coleccion porque un servidor puede tener varios
// certificados (RSA, ECC) y ofrecer el que el cliente soporte. Nosotros usamos uno.
var applicationCertificate = new CertificateIdentifier
{
    CertificateType = ObjectTypeIds.RsaSha256ApplicationCertificateType,
    StoreType = CertificateStoreType.Directory,
    StorePath = Path.Combine(pkiRoot, "own"),
    // Un solo DC=, y a proposito. Cuando un cliente entra por 127.0.0.1 el
    // stack NO compara esa IP contra el certificado: la reconoce como loopback
    // y la sustituye por el hostname de la maquina antes de comparar
    // (CertificateValidator.FindDomain). El SAN con "IP=127.0.0.1" nunca se
    // mira, y el dominio que hace falta es el nombre de maquina.
    // El "DC=localhost" no queda literal: SecurityConfiguration.Validate() lo
    // reescribe a DC={hostname} al arrancar, asi que esto es portable y no
    // hardcodea LAPTOP-0JPRBIMI.
    // Un segundo DC no agrega un segundo dominio: GetDomainsFromCertificate
    // concatena todos los DC= con puntos en UNA sola cadena. Agregar
    // "DC=127.0.0.1" producia el dominio "127.0.0.1.LAPTOP-0JPRBIMI", que no
    // matchea nada y dejaba el warning vivo. Verificado contra el fuente del
    // tag 1.5.378.156.
    SubjectName = $"CN={options.ApplicationName}, C=AR, O=Portfolio, DC=localhost"
};

// A que interfaz se expone el endpoint UA tiene que ser una decision de
// configuracion, no un efecto colateral del stack. Con el host escrito como
// "localhost" el stack lo sustituye por el hostname real de la maquina, y un
// listener con nombre (no IP) bindea a todas las interfaces: el endpoint queda
// publicado en la red sin que nadie lo haya decidido. Reescribirlo a 127.0.0.1
// antes de pasarselo evita esa sustitucion y acota el bind a loopback.
var configuredUri = new Uri(options.EndpointUrl);
var endpointHost = configuredUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
    ? "127.0.0.1"
    : configuredUri.Host;
var endpointPort = configuredUri.Port > 0 ? configuredUri.Port : 4840;
var endpointUrl = $"{configuredUri.Scheme}://{endpointHost}:{endpointPort}{configuredUri.AbsolutePath}";

// Configuracion armada en codigo, sin archivo XML.
var serverBuilder = application.Build(
        applicationUri: $"urn:{Dns.GetHostName()}:OpcGatewayDaUa:Server",
        productUri: "https://github.com/AgustinRoffoPortfolio/opc-gateway-da-ua")
    .AsServer(new[] { endpointUrl });

// El endpoint sin seguridad (None - None) es trafico sin firmar ni cifrar y sin
// validacion del certificado del cliente: comodo para desarrollo, pero se
// enciende a conciencia y no viene de arranque.
if (options.EnableUnsecureEndpoint)
    serverBuilder.AddUnsecurePolicyNone();

await serverBuilder
    .AddSignAndEncryptPolicies()      // endpoints firmados y cifrados
    .AddUserTokenPolicy(UserTokenType.Anonymous)
    .AddSecurityConfiguration(
        new CertificateIdentifierCollection { applicationCertificate },
        pkiRoot: pkiRoot)
    .SetAutoAcceptUntrustedCertificates(options.AutoAcceptUntrustedCertificates)
    .CreateAsync();

// Auditoria de conexiones UA. Se crea antes de enganchar nada para que ningun
// evento temprano del stack encuentre la referencia sin inicializar.
var audit = new UaAuditCounters();

// El modo permisivo se avisa como Warning y no como Information a proposito:
// un servidor que acepta cualquier certificado de cliente tiene que ser
// incomodo de ignorar en la consola, no una linea mas entre otras nueve.
if (options.AutoAcceptUntrustedCertificates)
{
    Log.Warning("MODO PERMISIVO: se acepta cualquier certificado de cliente sin validar. " +
                "Solo para desarrollo local. PKI en {PkiRoot}", pkiRoot);
}
else
{
    Log.Information("Validacion de certificados activa. Clientes confiables en {Trusted}",
        Path.Combine(pkiRoot, "trusted", "certs"));
}

if (options.EnableUnsecureEndpoint)
{
    Log.Warning("Endpoint sin seguridad HABILITADO (None - None): el trafico no se firma ni se cifra.");
}

// Habilita los nodos de diagnostico del server (ServerDiagnostics). Vienen
// apagados por default en el stack: el address space los expone igual, pero
// no se llenan y EnabledFlag no se deja escribir en runtime. Los necesitamos
// para ver sesiones, suscripciones y contadores del servidor desde un cliente UA.
application.ApplicationConfiguration.ServerConfiguration.DiagnosticsEnabled = options.DiagnosticsEnabled;
Log.Information("Diagnosticos del servidor UA: {Estado}",
    options.DiagnosticsEnabled ? "habilitados" : "deshabilitados");

// Crea el certificado propio del servidor la primera vez que corre.
await application.CheckApplicationInstanceCertificatesAsync(silent: true);

// Huella del certificado propio, para poder descartarlo en la auditoria. Se lee
// recien aca porque antes de esta linea puede no existir todavia.
var ownThumbprint = applicationCertificate.Certificate?.Thumbprint;

// El validador dispara este evento por cada certificado que no pasa la
// validacion. Solo contamos: tocar e.Accept aca cambiaria la politica de
// confianza que decide AutoAcceptUntrustedCertificates, y esa decision tiene
// que vivir en un solo lugar.
application.ApplicationConfiguration.CertificateValidator.CertificateValidation +=
    (_, e) =>
    {
        // El evento tambien salta cuando el stack valida el certificado DEL
        // PROPIO SERVIDOR contra la URL que mando el cliente, y esa validacion
        // falla sin impedir la sesion. Medido: con el bind en 127.0.0.1 cada
        // conexion exitosa dispara un BadCertificateHostNameInvalid sobre
        // nuestro propio certificado. Contarlo reportaria un intento rechazado
        // por cada cliente que entro sin problemas.
        if (ownThumbprint is not null &&
            string.Equals(e.Certificate?.Thumbprint, ownThumbprint, StringComparison.OrdinalIgnoreCase))
            return;

        // Si el modo permisivo ya lo perdono, tampoco fue un intento rechazado:
        // la sesion se establece igual.
        if (e.Accept) return;

        var reason = e.Error is { } error
            ? StatusCodes.GetBrowseName(error.StatusCode.Code)
            : "Unknown";

        audit.RecordRejection(RejectionCategory.Certificate, reason);

        Log.Warning("Intento de conexion rechazado por certificado: {Reason} (subject {Subject})",
            reason, e.Certificate?.Subject ?? "desconocido");
    };

// Arbol de tags: sale del CSV, no hardcodeado. Carga parcial (Fase 3): una
// fila invalida no tira el gateway abajo, queda fuera de servicio y se
// reporta en el log.
var tagLoadResult = TagValidator.LoadAndValidate(options.TagsCsvPath);
foreach (var error in tagLoadResult.Errors)
    Log.Warning("Tag invalido, queda fuera de servicio: {Error}", error.Message);

Log.Information("Tags cargados: {Validos} validos, {Invalidos} con error",
    tagLoadResult.Tags.Count, tagLoadResult.Errors.Count);

// (el calculo de la ventana de antiguedad se movio abajo, antes de tagDefinitions)

// Frontera entre los dos mundos: el driver DA la llena, el node manager la lee.
// La ventana de antiguedad se traduce aca de ciclos a tiempo: el criterio se
// configura en ciclos porque lo que importa es cuantas lecturas nos perdimos,
// pero Gateway.Core no conoce DaOptions y solo recibe una duracion.
var staleAfter = TimeSpan.FromMilliseconds(
    (long)daOptions.UpdateRateMs * daOptions.StaleAfterCycles);

// El umbral deja de ser un parametro de la cache y viaja por tag (V2-11). Hoy
// todos los tags son DA y reciben el mismo valor, calculado desde DaOptions;
// cuando entre la fuente SQL, sus definitions van a llevar null para que la
// calidad la mande la columna Q y no el reloj del gateway.
var tagDefinitions = tagLoadResult.Tags
    .Select(d => d with { StaleAfter = staleAfter })
    .ToList();

var cache = new TagCache(tagDefinitions);

Log.Information("Degradacion por antiguedad: {Cycles} ciclos ({Ms} ms sin refresco)",
    daOptions.StaleAfterCycles, staleAfter.TotalMilliseconds);

var server = new UaServer(options.NamespaceUri, tagDefinitions, cache, audit);
await application.StartAsync(server);

// El ciclo DA corre en su propio hilo y no en el timer de publicacion: COM
// exige MTA, y una lectura DA lenta no tiene por que frenar la publicacion UA.
// El apartment se fija aca de forma explicita en vez de heredarlo del hilo que
// nos toque, que es como venia funcionando de rebote.
var daShutdown = new CancellationTokenSource();
var acquisition = new DaAcquisitionService(cache, daOptions);
var daThread = new Thread(() => acquisition.Run(daShutdown.Token))
{
    IsBackground = true,
    Name = "OPC DA polling"
};
daThread.SetApartmentState(ApartmentState.MTA);
daThread.Start();

Log.Information("Address space listo: {Tags} tags", server.NodeManager?.TagCount ?? 0);

// Cada ciclo: publicar los valores actuales a los nodos suscriptos.
var interval = TimeSpan.FromMilliseconds(options.UpdateIntervalMs);
// Instante de arranque para el uptime del diagnostico. Se toma aca, con todo
// ya levantado: es el momento en que el gateway empieza a prestar servicio.
var startedUtc = DateTime.UtcNow;

// Ultima foto publicada, para que la capa web la sirva sin volver a armarla.
var snapshots = new SnapshotHolder();

using var timer = new Timer(_ =>
{
    try
    {
        server.NodeManager?.UpdateValues();

        // El snapshot se arma aca y no adentro del node manager porque es el
        // unico punto que ve las dos mitades: el estado del vinculo DA lo tiene
        // el servicio de adquisicion, y Gateway.Ua no puede depender del host.
        if (server.NodeManager is { } nodeManager)
        {
            var snapshot = GatewaySnapshot.Build(
                cache,
                acquisition.GetStatus(),
                nodeManager.GetServerStatus(),
                startedUtc,
                // La foto de auditoria se toma aca, en el mismo instante que el
                // resto: si se leyera al servirla, la pagina podria mostrar un
                // rechazo que los nodos UA todavia no vieron.
                audit.Snapshot());

            // Un unico Build por ciclo alimenta las dos vistas: los nodos UA y
            // la pagina sirven el mismo objeto, no dos fotos parecidas.
            nodeManager.PublishDiagnostics(snapshot);
            snapshots.Publish(snapshot);
        }
    }
    catch (Exception ex)
    {
        // Una excepcion sin atrapar dentro de un callback de Timer
        // termina el proceso entero.
        Log.Error(ex, "Fallo el ciclo de actualizacion");
    }
}, null, TimeSpan.Zero, interval);

// El alcance de red se avisa segun a que se expone: loopback es una linea mas,
// pero cualquier direccion alcanzable desde afuera tiene que ser incomoda de
// pasar por alto en la consola.
if (IPAddress.TryParse(endpointHost, out var boundAddress) && IPAddress.IsLoopback(boundAddress))
{
    Log.Information("Servidor OPC UA escuchando en {Endpoint} (solo loopback)", endpointUrl);
}
else
{
    Log.Warning("Servidor OPC UA escuchando en {Endpoint}: EXPUESTO A LA RED, alcanzable desde otras maquinas",
        endpointUrl);
}
Log.Information("Ciclo de actualizacion: {IntervalMs} ms", options.UpdateIntervalMs);

// El diagnostico web es accesorio: si no puede levantar (puerto ocupado, por
// ejemplo) se avisa y se sigue. Tumbar el gateway entero por la pagina que
// mira como esta el gateway seria exactamente al reves de lo que se quiere.
DiagnosticsServer? diagnosticsServer = null;

if (webOptions.Enabled)
{
    try
    {
        diagnosticsServer = new DiagnosticsServer(
            webOptions, snapshots, cache,
            new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger));

        await diagnosticsServer.StartAsync();
        Log.Information("Diagnostico web en {Url}", webOptions.ListenUrl);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "No se pudo levantar el diagnostico web; el gateway sigue sin el");
        diagnosticsServer = null;
    }
}
else
{
    Log.Information("Diagnostico web deshabilitado por configuracion");
}

// Senaliza el apagado desde Ctrl+C o desde el cierre del proceso (por ejemplo,
// el SCM de un servicio de Windows), nunca desde una tecla en una consola que
// en produccion no va a existir. El shutdown ordenado tiene que correr siempre,
// no solo cuando hay alguien interactuando con la terminal.
var shutdownRequested = new TaskCompletionSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownRequested.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownRequested.TrySetResult();

await shutdownRequested.Task;

Log.Information("Deteniendo servidor...");

// Primero la web: deja de aceptar requests antes de que empiecen a
// desaparecer las piezas que consulta.
if (diagnosticsServer is not null)
    await diagnosticsServer.DisposeAsync();

await daShutdown.CancelAsync();
daThread.Join(TimeSpan.FromSeconds(5));
await application.StopAsync();
Log.Information("Servidor detenido.");
await Log.CloseAndFlushAsync();