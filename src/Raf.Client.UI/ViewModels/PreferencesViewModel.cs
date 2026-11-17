using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Raf.Client.Core.Local;

namespace Raf.Client.ViewModels;

/// <summary>Preferencias de este equipo. Por ahora, el idioma: se guarda al elegirlo y se aplica al reiniciar.</summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly SettingsStore _settings;

    /// <summary>El idioma con el que arrancó la app: si se vuelve a elegir, no hace falta reiniciar.</summary>
    private readonly CultureInfo _running = CultureInfo.CurrentUICulture;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    private LanguageOption _language;

    public PreferencesViewModel(SettingsStore settings)
    {
        _settings = settings;
        _language = Languages.Options.FirstOrDefault(o => o.Code == settings.Current.Language) ?? Languages.Options[0];
    }

    public IReadOnlyList<LanguageOption> LanguageOptions => Languages.Options;

    /// <summary>El idioma elegido no es el que se está usando: se ve recién al volver a abrir RAF.</summary>
    public bool NeedsRestart =>
        Languages.Resolve(Language.Code, Languages.SystemCulture).Name != _running.Name;

    partial void OnLanguageChanged(LanguageOption value)
    {
        // El ComboBox puede limpiar la selección al cerrarse: eso no es elegir un idioma.
        if (value is null) return;
        _settings.Update(s => s.Language = value.Code);
    }
}
