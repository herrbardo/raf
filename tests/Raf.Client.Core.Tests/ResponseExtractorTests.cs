using Raf.Client.Core.Execution;
using Raf.Domain.Requests;

namespace Raf.Client.Core.Tests;

public class ResponseExtractorTests
{
    private const string Json = """
        {"data": {"token": "a\"b", "n": 12.5, "ok": true, "nada": null,
         "items": [{"id": 1}, {"id": 2}], "mi clave": "x", "o'k": "y", "obj": {"a": [1, 2]}}}
        """;

    private const string Xml = """
        <?xml version="1.0"?>
        <respuesta xmlns:s="urn:s">
          <token>abc</token>
          <item cod="1"/><item cod="2"><nombre>Dos</nombre></item>
          <s:firma>f</s:firma>
        </respuesta>
        """;

    private static ExtractionOutcome Body(string body, string path) =>
        ResponseExtractor.Evaluate(new ResponseExtraction("v", ExtractionSource.Body, path), body, []);

    [Theory]
    [InlineData("$.data.token", "a\"b")]
    [InlineData("$.data.n", "12.5")]
    [InlineData("$.data.ok", "true")]
    [InlineData("$.data.items[1].id", "2")]
    [InlineData("$.data['mi clave']", "x")]
    [InlineData("$.data['o\\'k']", "y")]
    [InlineData("$['data'][\"obj\"]", "{\"a\":[1,2]}")]
    [InlineData(" $.data.items[0] ", "{\"id\":1}")]
    public void Json_path_devuelve_el_valor_listo_para_usar(string path, string expected) =>
        Assert.Equal(expected, Body(Json, path).Value);

    [Theory]
    [InlineData("$.data.nada")]
    [InlineData("$.data.falta")]
    [InlineData("$.data.items[5]")]
    [InlineData("$.data.token.mas")]
    [InlineData("$.data[0]")]
    public void Json_path_sin_valor_no_encuentra(string path)
    {
        var outcome = Body(Json, path);
        Assert.False(outcome.Found);
        Assert.Contains("no tiene un valor", outcome.Problem);
    }

    [Theory]
    [InlineData("$.data.", "JSON path no es válido")]
    [InlineData("$.data[x]", "JSON path no es válido")]
    [InlineData("$data", "JSON path no es válido")]
    [InlineData("data.token", "JSON path (empieza con $)")]
    public void Ruta_invalida_explica_el_problema(string path, string problem) =>
        Assert.Contains(problem, Body(Json, path).Problem);

    [Fact]
    public void Json_path_sobre_un_body_que_no_es_json_lo_dice() =>
        Assert.Contains("no es JSON", Body("hola", "$.a").Problem);

    [Theory]
    [InlineData("/respuesta/token", "abc")]
    [InlineData("/respuesta/item[2]/@cod", "2")]
    [InlineData("//item[1]/@cod", "1")]
    [InlineData("/respuesta/item[2]", "<item cod=\"2\"><nombre>Dos</nombre></item>")]
    [InlineData("/respuesta/*[local-name()='firma']", "f")]
    public void XPath_devuelve_el_valor(string path, string expected) =>
        Assert.Equal(expected, Body(Xml, path).Value);

    [Fact]
    public void XPath_sin_resultado_o_invalido()
    {
        Assert.False(Body(Xml, "/respuesta/nada").Found);
        Assert.Contains("XPath no es válido", Body(Xml, "/respuesta[").Problem);
        Assert.Contains("no es XML", Body(Json, "/a").Problem);
    }

    [Fact]
    public void Header_sin_distinguir_mayusculas_y_uniendo_repetidos()
    {
        var headers = new List<KeyValuePair<string, string>> { new("Set-Cookie", "a=1"), new("set-cookie", "b=2"), new("X-Id", "9") };
        ExtractionOutcome Header(string name) =>
            ResponseExtractor.Evaluate(new ResponseExtraction("v", ExtractionSource.Header, name), null, headers);

        Assert.Equal("9", Header("x-id").Value);
        Assert.Equal("a=1, b=2", Header("Set-Cookie").Value);
        Assert.False(Header("Authorization").Found);
    }
}
