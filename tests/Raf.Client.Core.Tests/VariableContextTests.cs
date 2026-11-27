using Raf.Client.Core.Execution;
using Raf.Client.Core.Localization;
using Raf.Contracts;

namespace Raf.Client.Core.Tests;

/// <summary>Precedencia de las variables, que valen para todas las Apps (Ticket-044).</summary>
public sealed class VariableContextTests
{
    private static readonly Guid Env = Guid.NewGuid();

    private static VariableDto Var(string key, string? value, Guid? env = null, bool secret = false) =>
        new(Guid.NewGuid(), env, key, value, secret, 1, 1);

    private static string? Value(VariableContext vars, string key) => vars.Effective.FirstOrDefault(v => v.Key == key)?.Value;

    // Cada fila agrega una capa más; gana la más alta: globales < environment < sesión.
    [Theory]
    [InlineData(1, "global")]
    [InlineData(2, "env")]
    [InlineData(3, "sesion")]
    public void Gana_la_capa_mas_cercana(int layers, string expected)
    {
        VariableDto[] all = [Var("x", "global"), Var("x", "env", Env)];
        var session = layers == 3 ? new Dictionary<string, string> { ["x"] = "sesion" } : null;

        var vars = VariableContext.From(all.Take(Math.Min(layers, 2)), Env, session);

        Assert.Equal(expected, Value(vars, "x"));
    }

    [Fact]
    public void Las_de_otro_environment_no_cuentan()
    {
        var vars = VariableContext.From([Var("a", "otro", Guid.NewGuid())], Env);
        Assert.Empty(vars.Effective);
        Assert.False(vars.IsDefined("a"));
    }

    [Fact]
    public void Una_variable_vacia_cuenta_como_sin_valor()
    {
        var vars = VariableContext.From([Var("solo", "", Env), Var("host", "global"), Var("host", "", Env)], Env);

        Assert.False(vars.IsDefined("solo")); // en rojo y en "Sin valor"
        Assert.Equal("global", Value(vars, "host")); // la vacía del environment no tapa a la global

        var resolver = vars.CreateResolver();
        Assert.Equal("{{solo}}/global", resolver.Resolve("{{solo}}/{{host}}"));
        Assert.Equal(["solo"], resolver.Missing);
    }

    [Fact]
    public void Una_secreta_que_el_rol_no_puede_leer_existe_aunque_no_tenga_valor()
    {
        var vars = VariableContext.From([Var("token", null, Env, secret: true)], Env);
        Assert.True(vars.IsDefined("token"));
        Assert.True(vars.IsUnreadable("token"));
    }

    [Fact]
    public void El_tooltip_y_el_autocompletar_dicen_de_donde_sale_el_valor()
    {
        var vars = VariableContext.From([Var("a", "1"), Var("c", "3", Env)], Env);

        Assert.StartsWith($"a · {CoreTexts.Completion_Global}", VariableCompletion.Tooltip(vars, "a", "dev"));
        Assert.StartsWith($"c · {string.Format(CoreTexts.Hover_Environment, "dev")}", VariableCompletion.Tooltip(vars, "c", "dev"));

        var scopes = VariableCompletion.Suggestions(vars).Select(s => s.Description.Split(" · ")[0]).ToList();
        Assert.Equal([CoreTexts.Completion_Global, CoreTexts.Completion_Environment], scopes);
    }
}
