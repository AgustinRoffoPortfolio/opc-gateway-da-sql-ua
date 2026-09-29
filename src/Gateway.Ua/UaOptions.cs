namespace Gateway.Ua;

/// Configuracion del servidor OPC UA, leida de appsettings.json.
public class UaOptions
{
    public string ApplicationName { get; set; } = "OpcGatewayDaUa";
    /// A que interfaz se expone el endpoint UA. El host de esta URL decide el
    /// bind: una IP literal (127.0.0.1) acota el listener a esa interfaz, un
    /// nombre lo abre a todas. Default loopback: exponer a la red es una
    /// decision explicita, no un default heredado.
    public string EndpointUrl { get; set; } = "opc.tcp://127.0.0.1:4840/GatewayDaUa";
    public string NamespaceUri { get; set; } = "http://opc-gateway-da-ua/";
    public int UpdateIntervalMs { get; set; } = 1000;
    public string TagsCsvPath { get; set; } = "config/tags.example.csv";

    public string PkiRoot { get; set; } = "pki";
    
    /// Aceptar certificados de cliente no confiados. Default false: si la seccion
    /// Ua llega incompleta (por ejemplo, en un paquete con un JSON editado a mano),
    /// el servidor no queda en modo permisivo. appsettings.json ya lo fija en false.
    public bool AutoAcceptUntrustedCertificates { get; set; } = false;

    /// Endpoint sin seguridad (None - None). Util para desarrollo y para el
    /// cliente de carga, pero es trafico sin firmar ni cifrar y sin validacion
    /// de certificado de cliente: apagado por default, se enciende a conciencia.
    public bool EnableUnsecureEndpoint { get; set; } = false;

    /// Nodos de diagnostico del stack UA (sesiones, suscripciones, contadores).
    /// Externalizado para poder medir su costo en memoria: tienen precio y hay
    /// que poder correr con y sin ellos en la misma sesion. Default true para
    /// no cambiar el comportamiento existente.
    public bool DiagnosticsEnabled { get; set; } = true;
}
