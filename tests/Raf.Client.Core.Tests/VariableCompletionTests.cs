using Raf.Client.Core.Execution;
using Raf.Contracts;

namespace Raf.Client.Core.Tests;

public sealed class VariableCompletionTests
{
    private static VariableDto Var(string key, string? value, Guid? env = null, bool secret = false) =>
        new(Guid.NewGuid(), env, key, value, secret, 1, 1);

    [Theory]
    [InlineData("{{", 2, 2, 2)]
    [InlineData("https://{{ba", 12, 10, 12)]
    [InlineData("a {{host.url-1", 14, 4, 14)]
    [InlineData("{{base}}/x", 4, 2, 6)] // en medio de un nombre: se reemplaza entero
    [InlineData("{{{", 3, 3, 3)]
    public void Encuentra_el_nombre_que_se_esta_escribiendo(string line, int caret, int start, int end) =>
        Assert.Equal((start, end), VariableCompletion.FindQuery(line, caret));

    [Theory]
    [InlineData("", 0)]
    [InlineData("{", 1)]
    [InlineData("{ {", 3)]
    [InlineData("{{a b", 5)]
    [InlineData("{{a}}", 5)]
    [InlineData("hola", 4)]
    public void Fuera_de_un_doble_llave_abierto_no_hay_consulta(string line, int caret) =>
        Assert.Null(VariableCompletion.FindQuery(line, caret));

    [Fact]
    public void Ofrece_globales_y_del_environment_activo_con_precedencia_y_sin_valores_secretos()
    {
        var env = Guid.NewGuid();
        var otro = Guid.NewGuid();
        var vars = VariableContext.From(
        [
            Var("host", "global.local"),
            Var("host", "env.local", env),
            Var("Token", "valor-secreto", env, secret: true),
            Var("oculta", null, secret: true),
            Var("ajena", "x", otro),
            Var("vacia", ""),
        ], env);

        var suggestions = VariableCompletion.Suggestions(vars);

        Assert.Equal(["host", "oculta", "Token", "vacia"], suggestions.Select(s => s.Key));
        Assert.Equal("Environment activo · env.local", suggestions[0].Description);
        Assert.Equal("Global · secreta, tu rol no puede leerla", suggestions[1].Description);
        Assert.Equal("Environment activo · secreta", suggestions[2].Description);
        Assert.Equal("Global · sin valor", suggestions[3].Description);
        Assert.DoesNotContain(suggestions, s => s.Description.Contains("valor-secreto"));
    }

    [Fact]
    public void El_adelanto_del_valor_va_en_una_linea_y_recortado()
    {
        var vars = VariableContext.From([Var("largo", "a\nb" + new string('x', 100))], null);
        var description = Assert.Single(VariableCompletion.Suggestions(vars)).Description;
        Assert.StartsWith("Global · a b", description);
        Assert.EndsWith("…", description);
    }

    [Fact]
    public void El_tooltip_muestra_origen_y_valor_sin_revelar_secretos_y_avisa_si_no_existe()
    {
        var env = Guid.NewGuid();
        var vars = VariableContext.From(
        [
            Var("host", "global.local"),
            Var("host", "env.local", env),
            Var("puerto", "8080"),
            Var("clave", "valor-secreto", env, secret: true),
            Var("oculta", null, secret: true),
            Var("vacia", ""),
        ], env, new Dictionary<string, string> { ["token"] = "abc.def" });

        Assert.Equal("host · Environment Dev\nenv.local", VariableCompletion.Tooltip(vars, "host", "Dev"));
        Assert.Equal("host · Environment activo\nenv.local", VariableCompletion.Tooltip(vars, "host", null));
        Assert.Equal("puerto · Global\n8080", VariableCompletion.Tooltip(vars, "puerto", "Dev"));
        Assert.Equal("clave · Environment Dev\n•••••••• (secreta)", VariableCompletion.Tooltip(vars, "clave", "Dev"));
        Assert.Equal("oculta · Global\nsecreta, tu rol no puede leerla", VariableCompletion.Tooltip(vars, "oculta", "Dev"));
        Assert.Equal("vacia · Global\nsin valor", VariableCompletion.Tooltip(vars, "vacia", "Dev"));
        Assert.Equal("token · Sesión\n•••••••• (secreta)", VariableCompletion.Tooltip(vars, "token", "Dev"));
        Assert.StartsWith("nada no está definida", VariableCompletion.Tooltip(vars, "nada", "Dev"));
    }

    [Theory]
    [InlineData("", "host}}")]
    [InlineData("/path", "host}}")]
    [InlineData("}}/path", "host")]
    [InlineData(" }}", "host")]
    public void Agrega_el_cierre_solo_si_falta(string after, string expected) =>
        Assert.Equal(expected, VariableCompletion.Insertion("host", after));
}
