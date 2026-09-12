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
    AllowTrailingCommas = true
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

// Cuando vence cada grupo y si su campo esta cortado (P6).
var nextDue = catalog.ScanGroups.ToDictionary(g => g.Name, _ => DateTime.Now);
var fieldLost = catalog.ScanGroups.ToDictionary(g => g.Name, _ => false);

// Ultimo valor bueno de cada tag: es lo que queda congelado al perder el campo.
var lastGoodValue = new Dictionary<string, float>();

Console.WriteLine();
Console.WriteLine("Conectando...");

using var connection = new SqlConnection(connectionString);
connection.Open();
Console.WriteLine("Conexion abierta.");

// La sentencia es siempre la misma, asi que el comando y sus parametros se
// arman una sola vez y solo se les cambia el valor en cada escritura.
using var command = new SqlCommand(
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
var pTag = command.Parameters.Add("@tag", System.Data.SqlDbType.VarChar, 50);
var pTs = command.Parameters.Add("@ts", System.Data.SqlDbType.DateTime);
var pV = command.Parameters.Add("@v", System.Data.SqlDbType.Real);
var pQ = command.Parameters.Add("@q", System.Data.SqlDbType.SmallInt);

Console.WriteLine();
Console.WriteLine("Simulando. Teclas: 1/2/3 cortan o restablecen un grupo, Esc termina.");
Console.WriteLine();

// --- Loop ------------------------------------------------------------------

var running = true;
var writes = 0;

while (running)
{
    // Teclas primero: cortar un grupo tiene que verse en el ciclo siguiente.
    while (Console.KeyAvailable)
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
                fieldLost[name] = !fieldLost[name];
                var estado = fieldLost[name]
                    ? $"CAMPO PERDIDO (Q={catalog.LastKnownValueQuality})"
                    : $"campo restablecido (Q={catalog.GoodQuality})";
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
        var lost = fieldLost[group.Name];

        foreach (var tag in catalog.Tags.Where(t => t.ScanGroup == group.Name))
        {
            float value;
            short quality;

            if (lost)
            {
                // El campo se cayo: el valor queda pegado en el ultimo bueno y la
                // calidad lo declara. El TS sigue avanzando porque la aplicacion de
                // origen sigue viva: la falla se detecta por Q, no por antiguedad (P6).
                value = lastGoodValue.TryGetValue(tag.Name, out var held)
                    ? held
                    : SignalModel.Evaluate(tag, now);
                quality = catalog.LastKnownValueQuality;
            }
            else
            {
                value = SignalModel.Evaluate(tag, now);
                lastGoodValue[tag.Name] = value;
                quality = catalog.GoodQuality;
            }

            pTag.Value = tag.Name;
            pTs.Value = now;          // hora local, igual que la aplicacion de origen (P4)
            pV.Value = value;
            pQ.Value = quality;

            command.ExecuteNonQuery();
            writes++;
        }

        Console.WriteLine($"[{now:HH:mm:ss}] {group.Name,-8} actualizado{(lost ? "  (campo perdido)" : "")}");
    }

    Thread.Sleep(catalog.TickMs);
}

Console.WriteLine();
Console.WriteLine($"Terminado. Escrituras totales: {writes}");
return 0;

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
}