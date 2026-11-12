using Raf.Client.Core.Execution;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

public sealed class BodyFormatterTests
{
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    [Fact]
    public void Formatea_json()
    {
        var result = BodyFormatter.Format("{\"a\":1,\"b\":[true,null],\"c\":\"ñ\"}", BodyType.Json);
        Assert.Equal(Lines(
            "{",
            "  \"a\": 1,",
            "  \"b\": [",
            "    true,",
            "    null",
            "  ],",
            "  \"c\": \"ñ\"",
            "}"), result.Text);
    }

    [Fact]
    public void Conserva_las_variables_sin_comillas_y_las_de_adentro_de_strings()
    {
        var result = BodyFormatter.Format(
            "{\"id\":{{id}},\"nombre\":\"{{nombre}} \\\"{{x}}\\\"\",\"lista\":[{{ a.b-1 }},2],{{clave}}:true}", BodyType.Json);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Lines(
            "{",
            "  \"id\": {{id}},",
            "  \"nombre\": \"{{nombre}} \\\"{{x}}\\\"\",",
            "  \"lista\": [",
            "    {{ a.b-1 }},",
            "    2",
            "  ],",
            "  {{clave}}: true",
            "}"), result.Text);
    }

    [Fact]
    public void Formatear_dos_veces_no_cambia_nada()
    {
        var once = BodyFormatter.Format("{\"id\":{{id}},\"a\":[1,2]}", BodyType.Json).Text!;
        Assert.Equal(once, BodyFormatter.Format(once, BodyType.Json).Text);
    }

    [Theory]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\": }")]
    [InlineData("{\n\"a\": 1\n\"b\": 2}")]
    [InlineData("{\"a\": {{sin cerrar}")]
    public void Json_invalido_no_se_toca_y_dice_la_linea(string text)
    {
        var result = BodyFormatter.Format(text, BodyType.Json);
        Assert.False(result.Succeeded);
        Assert.Null(result.Text);
        Assert.StartsWith("El JSON no es válido (línea ", result.Error);
    }

    [Fact]
    public void Formatea_xml_y_conserva_la_declaracion_y_las_variables()
    {
        var result = BodyFormatter.Format("<?xml version=\"1.0\" encoding=\"utf-8\"?><a x=\"{{v}}\"><b>{{id}}</b></a>", BodyType.Xml);
        Assert.Equal(Lines(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<a x=\"{{v}}\">",
            "  <b>{{id}}</b>",
            "</a>"), result.Text);
    }

    [Fact]
    public void Xml_invalido_no_se_toca_y_dice_la_linea()
    {
        var result = BodyFormatter.Format("<a>\n<b></a>", BodyType.Xml);
        Assert.False(result.Succeeded);
        Assert.StartsWith("El XML no es válido (línea 2)", result.Error);
    }

    [Theory]
    [InlineData(BodyType.Text)]
    [InlineData(BodyType.FormUrlEncoded)]
    [InlineData(BodyType.None)]
    public void Solo_json_y_xml(BodyType type)
    {
        Assert.False(BodyFormatter.Supports(type));
        Assert.False(BodyFormatter.Format("{\"a\":1}", type).Succeeded);
    }

    [Fact]
    public void Body_vacio_avisa()
    {
        var result = BodyFormatter.Format("   ", BodyType.Json);
        Assert.False(result.Succeeded);
        Assert.Contains("vacío", result.Error);
    }
}
