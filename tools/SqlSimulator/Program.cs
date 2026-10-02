using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

// Simulador de la tabla CURR_DATA. Siembra una fila por tag y despues las
// actualiza al ritmo del grupo de scan de cada uno (P7). El valor sale de un
// modelo determinista, nunca de un numero al azar.
//
// Teclas: 1/2/3 cortan o restablecen el campo de un grupo, Esc termina.

// --- Configuracion ---------------------------------------------------------

var connectionString = Environment.GetEnvironmentVariable("SQLSIM_CONNSTR");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.WriteLine("Falta la variable de entorno SQLSIM_CONNSTR.");
    return 1;
}

var catalogPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "config", "tags.simulator.jsonc");

if (!File.Exists(catalogPath))
{
    Console.WriteLine($"No se encontro el catalogo: {catalogPath}");
    return 1;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    // Sin esto, un enum se lee solo como numero: "condition": "Uncertain" falla.
    // Va en las opciones y no como atributo del enum para que el catalogo se
    // escriba con nombres legibles sin que el tipo tenga que saberlo.
    Converters = { new JsonStringEnumConverter() }
};

var catalog = JsonSerializer.Deserialize<SimulatorCatalog>(File.ReadAllText(catalogPath), jsonOptions);
if (catalog is null || catalog.Tags.Count == 0)
{
    Console.WriteLine("El catalogo esta vacio o no se pudo leer.");
    return 1;
}

var orphanTags = catalog.Tags
    .Where(t => !catalog.ScanGroups.Any(g => g.Name == t.ScanGroup))
    .ToList();
if (orphanTags.Count > 0)
{
    Console.WriteLine($"Tags con grupo de scan inexistente: {string.Join(", ", orphanTags.Select(t => t.Name))}");
    return 1;
}

Console.WriteLine($"Catalogo: {catalog.Tags.Count} tags en {catalog.ScanGroups.Count} grupos de scan.");
for (var i = 0; i < catalog.ScanGroups.Count; i++)
{
    var group = catalog.ScanGroups[i];
    var count = catalog.Tags.Count(t => t.ScanGroup == group.Name);
    Console.WriteLine($"  [{i + 1}] {group.Name,-8} cada {group.PeriodMs,6} ms  -> {count} tags");
}

// --- Estado en memoria -----------------------------------------------------

// Cuando vence cada grupo y en que estado esta su vinculo con el campo (P6).
var nextDue = catalog.ScanGroups.ToDictionary(g => g.Name, _ => DateTime.Now);
var fieldState = catalog.ScanGroups.ToDictionary(g => g.Name, _ => FieldState.Healthy);

// Ultimo valor bueno de cada tag: es lo que queda congelado al perder el campo.
var lastGoodValue = new Dictionary<string, float>();

// Una sola conexion y un solo comando, que se rearman si se cae la base. La
// sentencia es siempre la misma, asi que el comando y sus parametros se arman
// una vez por conexion y solo se les cambia el valor en cada escritura.
SqlConnection? connection = null;
SqlCommand? command = null;
SqlParameter pTag = null!, pTs = null!, pV = null!, pQ = null!;

// Abre la conexion y reintenta con una espera fija hasta lograrlo. Una base que
// se reinicia o todavia no acepta conexiones no tiene que matar al simulador.
void Connect()
{
    while (true)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Conectando...");
        try
        {
            connection = new SqlConnection(connectionString);
            connection.Open();
            command = BuildMergeCommand(connection);
            pTag = command.Parameters["@tag"];
            pTs = command.Parameters["@ts"];
            pV = command.Parameters["@v"];
            pQ = command.Parameters["@q"];
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Conexion abierta.");
            return;
        }
        catch (SqlException ex)
        {
            Disconnect();
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] No se pudo conectar: {ex.Message.Split('\n')[0].Trim()}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Reintento en {catalog.ReconnectMs} ms.");
            Thread.Sleep(catalog.ReconnectMs);
        }
    }
}

void Disconnect()
{
    command?.Dispose();
    connection?.Dispose();
    command = null;
    connection = null;
}

Console.WriteLine();
Connect();

Console.WriteLine();
Console.WriteLine("Simulando. Teclas: 1/2/3 ciclan un grupo (sano -> campo perdido -> falla de comunicacion), Esc termina.");
Console.WriteLine();

// --- Loop ------------------------------------------------------------------

var running = true;
var writes = 0;

while (running)
{
    // Teclas primero: cortar un grupo tiene que verse en el ciclo siguiente.
    // Con la entrada redirigida (corrida en segundo plano) no hay teclado:
    // Console.KeyAvailable tiraria InvalidOperationException.
    while (!Console.IsInputRedirected && Console.KeyAvailable)
    {
        var key = Console.ReadKey(intercept: true).Key;

        if (key == ConsoleKey.Escape)
        {
            running = false;
        }
        else if (key >= ConsoleKey.D1 && key <= ConsoleKey.D9)
        {
            var index = key - ConsoleKey.D1;
            if (index < catalog.ScanGroups.Count)
            {
                var name = catalog.ScanGroups[index].Name;

                // La misma tecla cicla los tres estados del vinculo con el campo.
                fieldState[name] = fieldState[name] switch
                {
                    FieldState.Healthy => FieldState.LastKnown,
                    FieldState.LastKnown => FieldState.CommFailure,
                    _ => FieldState.Healthy
                };

                var estado = fieldState[name] switch
                {
                    FieldState.LastKnown => $"CAMPO PERDIDO (Q={catalog.LastKnownValueQuality})",
                    FieldState.CommFailure => $"FALLA DE COMUNICACION (Q={catalog.CommFailureQuality})",
                    _ => $"campo restablecido (Q={catalog.GoodQuality})"
                };

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Grupo {name}: {estado}");
            }
        }
    }

    var now = DateTime.Now;

    foreach (var group in catalog.ScanGroups)
    {
        if (now < nextDue[group.Name])
        {
            continue;
        }

        nextDue[group.Name] = now.AddMilliseconds(group.PeriodMs);
        var state = fieldState[group.Name];
        var lost = false;

        foreach (var tag in catalog.Tags.Where(t => t.ScanGroup == group.Name))
        {
            // Anulables porque V y Q son columnas nullable y el simulador tiene
            // que poder escribir NULL en las dos (caso de borde del esquema).
            float? value;
            short? quality;

            if (state == FieldState.Healthy)
            {
                value = SignalModel.Evaluate(tag, now);
                quality = catalog.GoodQuality;

                // La condicion propia del tag solo se aplica con el campo sano:
                // el estado del grupo tiene precedencia sobre ella.
                switch (tag.Condition)
                {
                    case TagCondition.LocalOverride:
                        // Un operador forzo el valor a mano: por eso deja de
                        // seguir el modelo. En UA esto se publica como Good.
                        value = (float)tag.OverrideValue;
                        quality = catalog.LocalOverrideQuality;
                        break;

                    case TagCondition.Uncertain:
                        quality = catalog.UncertainQuality;
                        break;

                    case TagCondition.NullValue:
                        value = null;
                        break;

                    case TagCondition.NullQuality:
                        quality = null;
                        break;
                }

                // Se recuerda lo ultimo que se escribio de verdad, que es lo que
                // queda congelado si despues se cae el campo. Un nulo no se recuerda.
                if (value.HasValue)
                {
                    lastGoodValue[tag.Name] = value.Value;
                }
            }
            else
            {
                // El vinculo con el campo se corto: el valor queda pegado en el
                // ultimo bueno y la calidad lo declara. El TS sigue avanzando porque
                // la aplicacion de origen sigue viva: la falla se detecta por Q, no
                // por antiguedad (P6, V2-23).
                value = lastGoodValue.TryGetValue(tag.Name, out var held)
                    ? held
                    : SignalModel.Evaluate(tag, now);
                quality = state == FieldState.CommFailure
                    ? catalog.CommFailureQuality
                    : catalog.LastKnownValueQuality;
            }

            pTag.Value = tag.Name;
            pTs.Value = now;          // hora local, igual que la aplicacion de origen (P4)
            // En ADO.NET un null de C# no significa NULL de SQL: hay que pasar
            // DBNull.Value explicitamente. Npgsql se comporta igual.
            pV.Value = (object?)value ?? DBNull.Value;
            pQ.Value = (object?)quality ?? DBNull.Value;

            // Si la base se cae a mitad del ciclo, el resto del grupo se pierde
            // en esta vuelta y se escribe en la siguiente: el estado del modelo
            // vive en memoria y no depende de la conexion.
            try
            {
                command!.ExecuteNonQuery();
                writes++;
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Se perdio la conexion: {ex.Message.Split('\n')[0].Trim()}");
                Disconnect();
                Thread.Sleep(catalog.ReconnectMs);
                Connect();
                lost = true;
                break;
            }
        }

        if (lost)
        {
            continue;
        }

        var nota = state switch
        {
            FieldState.LastKnown => "  (campo perdido)",
            FieldState.CommFailure => "  (falla de comunicacion)",
            _ => ""
        };

        Console.WriteLine($"[{now:HH:mm:ss}] {group.Name,-8} actualizado{nota}");
    }

    Thread.Sleep(catalog.TickMs);
}

Console.WriteLine();
Console.WriteLine($"Terminado. Escrituras totales: {writes}");
Disconnect();
return 0;

static SqlCommand BuildMergeCommand(SqlConnection connection)
{
    var command = new SqlCommand(
        """
        MERGE dbo.CURR_DATA AS target
        USING (SELECT @tag AS TAG) AS source
        ON target.TAG = source.TAG
        WHEN MATCHED THEN
            UPDATE SET TS = @ts, V = @v, Q = @q
        WHEN NOT MATCHED THEN
            INSERT (TAG, TS, V, Q) VALUES (@tag, @ts, @v, @q);
        """,
        connection);

    // Parametros tipados, nunca concatenacion de texto: asi el valor viaja como
    // numero y la coma decimal de es-AR no puede colarse.
    command.Parameters.Add("@tag", System.Data.SqlDbType.VarChar, 50);
    command.Parameters.Add("@ts", System.Data.SqlDbType.DateTime);
    command.Parameters.Add("@v", System.Data.SqlDbType.Real);
    command.Parameters.Add("@q", System.Data.SqlDbType.SmallInt);
    return command;
}

// --- Modelos y tipos -------------------------------------------------------

// Cada modelo es funcion del tiempo: el mismo instante da siempre el mismo valor.
static class SignalModel
{
    // Momento de arranque del proceso: ancla de las rampas.
    public static readonly DateTime StartedAt = DateTime.Now;

    public static float Evaluate(SimulatorTag tag, DateTime at)
    {
        // Dos anclas distintas. El seno usa los segundos desde medianoche, asi la
        // fase no salta al reiniciar. La rampa usa el arranque del simulador: si
        // acumulara desde medianoche, un totalizador arrancaria en cualquier numero.
        var t = at.TimeOfDay.TotalSeconds;
        var elapsed = (at - StartedAt).TotalSeconds;

        return tag.Model switch
        {
            "Sine" => (float)(tag.BaseValue + tag.Amplitude * Math.Sin(2 * Math.PI * t / tag.PeriodSeconds)),
            "Ramp" => (float)(tag.BaseValue + tag.RatePerSecond * elapsed),
            _ => (float)tag.BaseValue
        };
    }
}

sealed class SimulatorCatalog
{
    public short GoodQuality { get; set; } = 192;
    public short LastKnownValueQuality { get; set; } = 20;
    public short CommFailureQuality { get; set; } = 24;
    public short LocalOverrideQuality { get; set; } = 216;
    public short UncertainQuality { get; set; } = 64;
    public int TickMs { get; set; } = 500;
    public int ReconnectMs { get; set; } = 5000;
    public List<ScanGroup> ScanGroups { get; set; } = [];
    public List<SimulatorTag> Tags { get; set; } = [];
}

sealed class ScanGroup
{
    public string Name { get; set; } = "";
    public int PeriodMs { get; set; }
}

sealed class SimulatorTag
{
    public string Name { get; set; } = "";
    public string ScanGroup { get; set; } = "";
    public string Model { get; set; } = "Steady";
    public double BaseValue { get; set; }
    public double Amplitude { get; set; }
    public double PeriodSeconds { get; set; } = 60;
    public double RatePerSecond { get; set; }

    // Ausente en el JSON = None: un tag sin condicion se comporta normal.
    public TagCondition Condition { get; set; } = TagCondition.None;

    // Valor que dejo el operador al forzar el tag. Solo lo usa LocalOverride.
    public double OverrideValue { get; set; }
}

// Condicion propia del tag, independiente del estado del grupo de scan.
// LocalOverride y Uncertain existen en la tabla real (ver calidad-observada.md);
// los dos casos NULL no, y por eso van declarados a mano en el catalogo.
enum TagCondition
{
    None,
    LocalOverride,
    Uncertain,
    NullValue,
    NullQuality
}

// Estado del vinculo con el campo, por grupo de scan. Tiene precedencia sobre
// la condicion propia de cada tag: si no hay campo, no hay nada que reportar.
enum FieldState
{
    Healthy,
    LastKnown,
    CommFailure
}