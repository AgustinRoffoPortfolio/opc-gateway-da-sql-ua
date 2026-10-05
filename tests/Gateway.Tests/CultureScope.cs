using System.Globalization;

namespace Gateway.Tests;

/// <summary>
/// Cambia la cultura del hilo mientras dura el using y la restaura al salir.
/// La maquina de produccion corre en es-AR (coma decimal): los tests que
/// cuidan el formateo invariante tienen que correr en esa cultura, no en la
/// que tenga la maquina que los ejecuta.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
