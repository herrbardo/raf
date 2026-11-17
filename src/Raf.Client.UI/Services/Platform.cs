using System.ComponentModel;
using System.Reflection;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using Raf.Client.Core.Local;
using Raf.Client.ViewModels;
using Raf.Contracts;

namespace Raf.Client.Services;

/// <summary>
/// Lo que cambia entre la app de escritorio y la web. Cada una lo implementa en su proyecto y lo asigna en
/// <see cref="Current"/> antes de arrancar Avalonia.
/// </summary>
public abstract class ClientHost
{
    public static ClientHost Current { get; set; } = null!;

    /// <summary>Versión semántica sin metadatos de build ("1.2.0", "1.3.0-beta.1").</summary>
    public string Version { get; } = AppVersion.Display(
        (Assembly.GetEntryAssembly() ?? typeof(ClientHost).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>
    /// Cifrar los secretos de la caché local. En web la caché vive en memoria y el navegador no tiene
    /// <c>AesGcm</c>.
    /// </summary>
    public virtual bool EncryptCache => true;

    public abstract SettingsStore CreateSettings();

    /// <summary>
    /// Registra lo propio de la plataforma: <see cref="ISecureStore"/>, <see cref="IUpdateService"/>,
    /// <see cref="IWindowService"/> y <see cref="Core.Execution.RequestRunner"/>.
    /// </summary>
    public abstract void AddServices(IServiceCollection services);

    /// <summary>Muestra la vista raíz: la ventana principal en desktop, la página en web.</summary>
    public abstract void Show(IApplicationLifetime? lifetime, ShellViewModel shell, IServiceProvider services);
}

/// <summary>Versiones nuevas de la app: Velopack en desktop, recargar la página en web.</summary>
public interface IUpdateService : INotifyPropertyChanged
{
    /// <summary>La app puede actualizarse sola.</summary>
    bool IsSupported { get; }

    /// <summary>Hay una versión descargada lista para instalar.</summary>
    bool IsReady { get; }

    string ReadyText { get; }

    /// <summary>Página para bajar la versión nueva a mano, si se conoce.</summary>
    string? ReleasesPage { get; }

    /// <summary>Busca y descarga la última versión. Devuelve true si quedó una lista para instalar.</summary>
    Task<bool> CheckAndDownloadAsync();

    /// <summary>Instala la versión nueva y vuelve a abrir la app.</summary>
    void ApplyAndRestart();
}
