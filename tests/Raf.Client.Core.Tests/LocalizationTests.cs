using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Raf.Client.Core.Localization;

namespace Raf.Client.Core.Tests;

public sealed partial class LocalizationTests
{
    public static TheoryData<string> Resources => ["CoreTexts", "Texts"];

    private static ResourceManager Manager(string name) => name switch
    {
        "CoreTexts" => CoreTexts.ResourceManager,
        _ => Texts.ResourceManager,
    };

    private static Dictionary<string, string> Entries(ResourceManager manager, CultureInfo culture) =>
        manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    [Theory]
    [MemberData(nameof(Resources))]
    public void Cada_texto_tiene_su_traduccion_al_ingles_con_los_mismos_marcadores(string name)
    {
        var manager = Manager(name);
        var spanish = Entries(manager, CultureInfo.InvariantCulture);
        var english = Entries(manager, CultureInfo.GetCultureInfo("en"));

        Assert.NotEmpty(spanish);
        Assert.Empty(spanish.Keys.Except(english.Keys));
        Assert.Empty(english.Keys.Except(spanish.Keys));
        foreach (var (key, es) in spanish)
        {
            Assert.False(string.IsNullOrWhiteSpace(english[key]), $"{name}.{key} está vacío en inglés");
            Assert.Equal(Placeholders(es), Placeholders(english[key]));
        }
    }

    /// <summary>Los {0}, {1}… que usa cada texto, sin repetir y ordenados.</summary>
    private static string Placeholders(string text) =>
        string.Join(",", Placeholder().Matches(text).Select(m => m.Value).Distinct().Order());

    [GeneratedRegex(@"(?<!\{)\{\d+\}(?!\})")]
    private static partial Regex Placeholder();

    [Fact]
    public void Lee_los_textos_en_el_idioma_pedido()
    {
        Assert.Equal("Sin conexión", Texts.ResourceManager.GetString(nameof(Texts.Sync_Offline), CultureInfo.GetCultureInfo("es-AR")));
        Assert.Equal("Offline", Texts.ResourceManager.GetString(nameof(Texts.Sync_Offline), CultureInfo.GetCultureInfo("en-US")));
    }

    [Theory]
    [InlineData("es", "en-US", "es")]
    [InlineData("en", "es-AR", "en")]
    [InlineData(null, "es-AR", "es")]
    [InlineData(null, "es", "es")]
    [InlineData(null, "en-GB", "en")]
    [InlineData(null, "de-DE", "en")]
    [InlineData("fr", "es-MX", "es")]
    public void Resuelve_el_idioma(string? code, string system, string expected) =>
        Assert.Equal(expected, Languages.Resolve(code, CultureInfo.GetCultureInfo(system)).Name);

    [Fact]
    public void Las_opciones_empiezan_por_el_idioma_del_sistema()
    {
        Assert.Equal([null, "es", "en"], Languages.Options.Select(o => o.Code));
    }
}
