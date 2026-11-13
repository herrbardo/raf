namespace Raf.Client.ViewModels;

/// <summary>Datos de la ventana "Acerca de": la versión instalada y quién desarrolla RAF.</summary>
public sealed class AboutViewModel(string version)
{
    public const string DeveloperName = "herrbardo";

    public string Version { get; } = version;

    public string Developer => DeveloperName;
}
