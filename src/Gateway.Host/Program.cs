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
using Gateway.Sql;
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
    // Override local de las credenciales SQL (V2-7, V2-26). Archivo opcional
    // junto al ejecutable, no versionado (.gitignore), cargado despues del
    // JSON para pisar sus claves vacias y antes de las variables de entorno
    // para que estas sigan ganando: son el mecanismo previsto para el
    // servidor de TEST (P10). Reemplaza a user-secrets: ese mecanismo solo
    // carga en DOTNET_ENVIRONMENT=Development y el paquete publicado no fija
    // ninguno, asi que en TEST los secretos nunca se leian sin que nada lo
    // avisara.
    .AddJsonFile("appsettings.Local.json", optional: true)
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

// Los valores reales de User y Password no estan en el JSON versionado: llegan
// por appsettings.Local.json o variables de entorno y pisan las claves vacias
// (V2-7, V2-26).
var sqlOptions = configuration.GetSection("Sql").Get<SqlOptions>() ?? new SqlOptions();

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
    var trustedCertsPath = Path.Combine(pkiRoot, "trusted", "certs");
    // El paquete no trae pki/: crear la carpeta para que el operador pueda mover ahi el certificado del cliente desde rejected\certs.
    Directory.CreateDirectory(trustedCertsPath);
    Log.Information("Validacion de certificados activa. Clientes confiables en {Trusted}",
        trustedCertsPath);
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

// V2-22 eligio aviso en vez de error porque "el aviso alcanza para que se
// corrija". Un aviso que no se loguea no alcanza para nada: la fila sigue
// en servicio, asi que el nivel es Information y no Warning.
foreach (var warning in tagLoadResult.Warnings)
    Log.Information("Tag con aviso, sigue en servicio: {Warning}", warning);

Log.Information("Tags cargados: {Validos} validos, {Invalidos} con error, {ConAviso} con aviso",
    tagLoadResult.Tags.Count, tagLoadResult.Errors.Count, tagLoadResult.Warnings.Count);

// Si la fuente SQL arranca (B1, invariante 8): sin tags SOURCE=SQL en el CSV
// no se exige Sql:*, y con tags SQL declarados la fuente entera queda
// inactiva ante una configuracion invalida en vez de reintentar para siempre
// contra parametros que nunca van a conectar.
var hasSqlTags = tagLoadResult.Tags.Any(t => t.Source == TagSource.Sql);
var sqlActivation = SqlSourceActivation.Decide(hasSqlTags, sqlOptions);

foreach (var error in sqlActivation.Errors)
    Log.Error("Configuracion SQL invalida: {Error}", error);
foreach (var warning in sqlActivation.Warnings)
    Log.Warning("Configuracion SQL: {Warning}", warning);

if (hasSqlTags && !sqlActivation.Active)
    Log.Error("Fuente SQL inactiva: hay tags SOURCE=SQL en el CSV pero Sql:* no paso la validacion " +
              "(ver errores arriba). Esos tags quedan sin actualizar; el resto del gateway sigue.");
else if (!hasSqlTags)
    Log.Information("No hay tags SOURCE=SQL en el CSV; la fuente SQL no se activa y Sql:* no se exige.");

// Lo que la pagina necesita para distinguir "inactiva por configuracion" de
// "sin tags" (B3): las dos causas dejan sqlAcquisition en null. Solo entra la
// primera; una fuente sin tags no se usa y no se muestra. Es texto de la
// pagina, no del log, y por eso lleva tildes.
List<InactiveSource> inactiveSources = [];
if (hasSqlTags && !sqlActivation.Active)
    inactiveSources.Add(new InactiveSource(TagSource.Sql,
        "Configuración inválida en Sql:*. Los tags SQL no se actualizan; el motivo está en el log de arranque."));

// Misma regla para DA (V2-29, simetrica a B1/V2-27 para SQL): sin tags de
// origen OPC DA en el CSV, el driver no se crea. La maquina donde se instale
// el gateway puede no tener servidor DA, y un driver reintentando en loop
// contra algo que el CSV nunca pidio es puro ruido en el log.
var hasDaTags = tagLoadResult.Tags.Any(t => t.Source == TagSource.OpcDa);

if (!hasDaTags)
    Log.Information("No hay tags de origen OPC DA en el CSV; la fuente DA no se activa.");

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
// Los tags SQL no se degradan por antiguedad (V2-11): su calidad sale de la
// columna Q, que ya reporta la perdida del campo (P6), y el polling de decenas
// de segundos degradaria todos los tags entre un ciclo y el siguiente.
//
// NO EJERCITADO todavia: CsvTagLoader pasa Source: TagSource.OpcDa fijo hasta
// que entre V2-5 en la Fase 4, asi que hoy la rama Sql no se cumple nunca.
var tagDefinitions = tagLoadResult.Tags
    .Select(d => d with { StaleAfter = d.Source == TagSource.Sql ? null : staleAfter })
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
// Solo se crea si hay tags DA (V2-29, misma logica que B1/V2-27 para SQL): sin
// esto, el driver reintentaria para siempre contra un servidor que el CSV
// nunca declaro.
var daShutdown = new CancellationTokenSource();
DaAcquisitionService? acquisition = null;
Thread? daThread = null;

if (hasDaTags)
{
    acquisition = new DaAcquisitionService(cache, daOptions);
    daThread = new Thread(() => acquisition.Run(daShutdown.Token))
    {
        IsBackground = true,
        Name = "OPC DA polling"
    };
    daThread.SetApartmentState(ApartmentState.MTA);
    daThread.Start();
}

// Hilo propio para SQL, separado del de DA y del timer de publicacion: es el
// invariante 8, una consulta lenta o una base caida no pueden frenar a la otra
// fuente. Sin SetApartmentState: eso es una exigencia de COM, no de ADO.NET.
// Solo se crea si la fuente esta activa (B1): sin esto, una configuracion
// invalida reconectaria para siempre contra parametros que nunca van a andar.
var sqlShutdown = new CancellationTokenSource();
SqlAcquisitionService? sqlAcquisition = null;
Thread? sqlThread = null;

if (sqlActivation.Active)
{
    sqlAcquisition = new SqlAcquisitionService(cache, sqlOptions);
    sqlThread = new Thread(() => sqlAcquisition.Run(sqlShutdown.Token))
    {
        IsBackground = true,
        Name = "SQL polling"
    };
    sqlThread.Start();
}

Log.Information("Address space listo: {Tags} tags", server.NodeManager?.TagCount ?? 0);

// La rama de diagnostico de una fuente inactiva no la toca el timer, porque no
// hay status que reportar. Se publica una sola vez su estado (B10) para que no
// quede con los valores iniciales del nodo.
if (inactiveSources.Count > 0)
    server.NodeManager?.PublishInactiveSources(inactiveSources);

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
            // Una entrada por fuente activa: si una fuente no arranco (B1/V2-27
            // para SQL, V2-29 para DA), no hay status que reportar por ella y
            // no entra aca. La inactiva por configuracion va en inactiveSources.
            List<SourceLinkStatus> sourceLinks = [];
            if (acquisition is not null) sourceLinks.Add(acquisition.GetStatus());
            if (sqlAcquisition is not null) sourceLinks.Add(sqlAcquisition.GetStatus());

            var snapshot = GatewaySnapshot.Build(
                cache,
                sourceLinks,
                nodeManager.GetServerStatus(),
                startedUtc,
                // La foto de auditoria se toma aca, en el mismo instante que el
                // resto: si se leyera al servirla, la pagina podria mostrar un
                // rechazo que los nodos UA todavia no vieron.
                audit.Snapshot(),
                inactiveSources);

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

// Los dos tokens se cancelan antes de esperar a ningun hilo: asi el apagado
// tarda lo que tarda el mas lento y no la suma de los dos.
await daShutdown.CancelAsync();
await sqlShutdown.CancelAsync();

// El hilo SQL se despierta enseguida del WaitOne, porque la cancelacion senaliza
// el WaitHandle y no hay que esperar el intervalo de polling entero. La
// excepcion es una consulta en curso: ahi el hilo no vuelve hasta que termine o
// venza el CommandTimeout, el Join se agota y el proceso lo mata por ser
// background. Se acepta: estirar el Join volveria lento cada apagado normal
// para cubrir un caso raro, y no hay nada que perder porque el driver es de
// solo lectura y no deja escrituras a medio hacer.
daThread?.Join(TimeSpan.FromSeconds(5));
sqlThread?.Join(TimeSpan.FromSeconds(5));
await application.StopAsync();
Log.Information("Servidor detenido.");
await Log.CloseAndFlushAsync();