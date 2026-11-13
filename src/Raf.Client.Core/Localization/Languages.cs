using System.Globalization;

namespace Raf.Client.Core.Localization;

/// <summary>Un idioma que se puede elegir. <see cref="Code"/> null = el del sistema.</summary>
public sealed record LanguageOption(string? Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Idiomas de la interfaz. Los textos están en <see cref="CoreTexts"/> y <see cref="Texts"/> (español, el neutro) y
/// en sus <c>.en.resx</c>. El idioma se aplica al arrancar: cambiarlo requiere reiniciar la app.
/// </summary>
public static class Languages
{
    public const string Spanish = "es";
    public const string English = "en";

    /// <summary>Los nombres van en su propio idioma, para encontrar el tuyo aunque la app esté en otro.</summary>
    public static IReadOnlyList<LanguageOption> Options { get; } =
    [
        new(null, Texts.Language_System),
        new(Spanish, "Español"),
        new(English, "English"),
    ];

    /// <summary>
    /// El idioma a usar: el elegido si es uno de los disponibles; si no, el del sistema cuando es español y, para
    /// cualquier otro, inglés.
    /// </summary>
    public static CultureInfo Resolve(string? code, CultureInfo system)
    {
        if (code is Spanish or English) return CultureInfo.GetCultureInfo(code);
        return system.TwoLetterISOLanguageName == Spanish
            ? CultureInfo.GetCultureInfo(Spanish)
            : CultureInfo.GetCultureInfo(English);
    }

    /// <summary>El idioma del sistema, tal como estaba antes de <see cref="Apply"/>.</summary>
    public static CultureInfo SystemCulture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>Se llama al arrancar, antes de crear la interfaz. Solo cambia los textos, no el formato de números ni fechas.</summary>
    public static void Apply(string? code)
    {
        SystemCulture = CultureInfo.CurrentUICulture;
        var culture = Resolve(code, SystemCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
