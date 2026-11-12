using Raf.Client.Core.Execution;

namespace Raf.Client.Core.Tests;

public sealed class ResponseLocatorTests
{
    private const string Json = "{\"usuario\":{\"id\":7,\"nombre\":\"Ana \\\"la\\\" ñ\",\"tags\":[\"a\",\"b\"],\"activo\":true,\"extra\":null},\"mi clave\":1.5,\"lista\":[{\"x\":1},{\"x\":2}]}";

    private static string Pretty(string text, string mediaType) => ResponseFormatter.Format(text, mediaType).Text;

    /// <summary>Offset de la n-ésima aparición de <paramref name="marker"/> (desde 0) más un corrimiento.</summary>
    private static int At(string text, string marker, int shift = 0, int occurrence = 0)
    {
        var index = -1;
        for (var i = 0; i <= occurrence; i++) index = text.IndexOf(marker, index + 1, StringComparison.Ordinal);
        Assert.True(index >= 0, $"No está '{marker}'");
        return index + shift;
    }

    private static ResponseNode FindJson(string text, int offset) =>
        ResponseLocator.Find(text, ResponseFormat.Json, offset) ?? throw new Xunit.Sdk.XunitException("Sin nodo");

    [Fact]
    public void Json_escalar_con_clave_y_ruta()
    {
        var text = Pretty(Json, "application/json");
        var node = FindJson(text, At(text, "\"id\""));
        Assert.Equal(new ResponseNode("id", "7", "$.usuario.id"), node);
    }

    [Fact]
    public void Json_string_sin_comillas_ni_escapes()
    {
        var text = Pretty(Json, "application/json");
        var node = FindJson(text, At(text, "\"nombre\"", shift: 12));
        Assert.Equal("nombre", node.Key);
        Assert.Equal("Ana \"la\" ñ", node.Value);
    }

    [Fact]
    public void Json_clic_en_la_sangria_toma_el_nodo_de_la_linea()
    {
        var text = Pretty(Json, "application/json");
        var line = text.LastIndexOf('\n', At(text, "\"activo\"")) + 1;
        Assert.Equal(new ResponseNode("activo", "true", "$.usuario.activo"), FindJson(text, line));
        Assert.Equal("null", FindJson(text, At(text, "\"extra\"")).Value);
    }

    [Fact]
    public void Json_elemento_de_array_sin_clave()
    {
        var text = Pretty(Json, "application/json");
        var node = FindJson(text, At(text, "\"b\""));
        Assert.Equal(new ResponseNode(null, "b", "$.usuario.tags[1]"), node);
    }

    [Fact]
    public void Json_objeto_devuelve_el_json_indentado()
    {
        var text = Pretty(Json, "application/json");
        var node = FindJson(text, At(text, "\"usuario\""));
        Assert.Equal("usuario", node.Key);
        Assert.Equal("$.usuario", node.Path);
        Assert.StartsWith("{", node.Value);
        Assert.Contains("\"nombre\": \"Ana \\\"la\\\" ñ\"", node.Value);
    }

    [Fact]
    public void Json_linea_de_cierre_toma_el_contenedor()
    {
        var text = Pretty(Json, "application/json");
        // El primer "}" que cierra un objeto de la lista: el de {"x": 1}.
        var node = FindJson(text, At(text, "}", occurrence: 1));
        Assert.Equal("$.lista[0]", node.Path);
    }

    [Fact]
    public void Json_clave_rara_usa_corchetes()
    {
        var text = Pretty(Json, "application/json");
        Assert.Equal("$['mi clave']", FindJson(text, At(text, "\"mi clave\"")).Path);
        Assert.Equal("$.lista[1].x", FindJson(text, At(text, "\"x\"", occurrence: 1)).Path);
        Assert.Equal("$['it\\'s']", ResponseLocator.Member("it's").Insert(0, "$"));
    }

    [Fact]
    public void Json_en_una_sola_linea_toma_el_mas_interno_bajo_el_clic()
    {
        var node = FindJson(Json, At(Json, "\"a\""));
        Assert.Equal(new ResponseNode(null, "a", "$.usuario.tags[0]"), node);
        Assert.Equal("$.usuario.id", FindJson(Json, At(Json, "7")).Path);
    }

    [Fact]
    public void Json_raiz()
    {
        var node = FindJson("[1, 2]", 0);
        Assert.Equal("$", node.Path);
        Assert.Null(node.Key);
    }

    [Fact]
    public void Texto_invalido_o_plano_no_tiene_nodo()
    {
        Assert.Null(ResponseLocator.Find("{\"a\":", ResponseFormat.Json, 2));
        Assert.Null(ResponseLocator.Find("<a><b></a>", ResponseFormat.Xml, 2));
        Assert.Null(ResponseLocator.Find("hola", ResponseFormat.Plain, 1));
    }

    private const string Xml = "<?xml version=\"1.0\"?><pedido id=\"9\"><item cod=\"A\"><nombre>Mate</nombre></item><item cod=\"B\"><nombre>Yerba &amp; más</nombre></item><nota>ok</nota></pedido>";

    private static ResponseNode FindXml(string text, int offset) =>
        ResponseLocator.Find(text, ResponseFormat.Xml, offset) ?? throw new Xunit.Sdk.XunitException("Sin nodo");

    [Fact]
    public void Xml_elemento_con_texto()
    {
        var text = Pretty(Xml, "application/xml");
        var node = FindXml(text, At(text, "Yerba"));
        Assert.Equal(new ResponseNode("nombre", "Yerba & más", "/pedido/item[2]/nombre"), node);
        Assert.Equal("/pedido/nota", FindXml(text, At(text, "<nota>")).Path);
    }

    [Fact]
    public void Xml_atributo_bajo_el_clic()
    {
        var text = Pretty(Xml, "application/xml");
        var node = FindXml(text, At(text, "cod=\"B\"", shift: 5));
        Assert.Equal(new ResponseNode("cod", "B", "/pedido/item[2]/@cod"), node);
        // En el nombre del elemento, el elemento.
        Assert.Equal("/pedido/item[2]", FindXml(text, At(text, "<item", shift: 1, occurrence: 1)).Path);
    }

    [Fact]
    public void Xml_elemento_con_hijos_devuelve_su_xml_y_el_cierre_lo_encuentra()
    {
        var text = Pretty(Xml, "application/xml");
        var node = FindXml(text, At(text, "<item", shift: 1));
        Assert.Equal("item", node.Key);
        Assert.Contains("<nombre>Mate</nombre>", node.Value);
        Assert.Equal("/pedido/item[1]", FindXml(text, At(text, "</item>")).Path);
    }

    [Fact]
    public void Xml_con_namespace_usa_local_name()
    {
        const string text = "<a:raiz xmlns:a=\"urn:x\"><a:hijo>1</a:hijo></a:raiz>";
        var node = FindXml(text, At(text, "1<"));
        Assert.Equal("hijo", node.Key);
        Assert.Equal("/*[local-name()='raiz']/*[local-name()='hijo']", node.Path);
    }
}
