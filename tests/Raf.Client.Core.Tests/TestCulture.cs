using System.Globalization;
using System.Runtime.CompilerServices;

namespace Raf.Client.Core.Tests;

/// <summary>
/// Los tests comparan mensajes en español: se fija ese idioma para que no dependan del idioma de la máquina.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseSpanish() =>
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo("es");
}
