using Raf.Client.Core.Execution;
using Raf.Client.Core.Localization;
using Raf.Contracts;

namespace Raf.Client.Core.Tests;

/// <summary>Precedencia con variables por App (Plan 11, V5).</summary>
public sealed class VariableContextTests
{
    private static readonly Guid Env = Guid.NewGuid();
    private static readonly Guid Orders = Guid.NewGuid();
    private static readonly Guid Users = Guid.NewGuid();

    private static VariableDto Var(string key, string value, Guid? env = null, Guid? app = null) =>
        new(Guid.NewGuid(), env, key, value, false, 1, 1, app);

    private static string? Value(VariableContext vars, string key) => vars.Effective.FirstOrDefault(v => v.Key == key)?.Value;

    // Cada fila agrega una capa más; gana la más alta: globales·Todas < globales·App < environment·Todas < environment·App < sesión.
    [Theory]
    [InlineData(1, "global")]
    [InlineData(2, "global-app")]
    [InlineData(3, "env")]
    [InlineData(4, "env-app")]
    [InlineData(5, "sesion")]
    public void Gana_la_capa_mas_cercana(int layers, string expected)
    {
        VariableDto[] all =
        [
            Var("x", "global"), Var("x", "global-app", app: Orders), Var("x", "env", Env), Var("x", "env-app", Env, Orders),
        ];
        var session = layers == 5 ? new Dictionary<string, string> { ["x"] = "sesion" } : null;

        var vars = VariableContext.From(all.Take(Math.Min(layers, 4)), Env, session, Orders);

        Assert.Equal(expected, Value(vars, "x"));
    }

    [Fact]
    public void Una_global_de_la_App_no_le_gana_al_environment_de_todas()
    {
        var vars = VariableContext.From([Var("x", "global-app", app: Orders), Var("x", "env", Env)], Env, null, Orders);
        Assert.Equal("env", Value(vars, "x"));
    }

    [Fact]
    public void Las_variables_de_otra_App_y_las_de_otro_environment_no_cuentan()
    {
        VariableDto[] all = [Var("a", "de-users", app: Users), Var("b", "de-users-env", Env, Users), Var("c", "otro-env", Guid.NewGuid(), Orders)];

        var orders = VariableContext.From(all, Env, null, Orders);
        var shared = VariableContext.From(all, Env);

        Assert.Empty(orders.Effective);
        Assert.Empty(shared.Effective);
    }

    [Fact]
    public void Shared_deja_afuera_las_de_la_App_y_conserva_la_sesion_nueva()
    {
        var vars = VariableContext.From([Var("host", "todas"), Var("host", "orders", app: Orders)], null, null, Orders)
            .WithSession("token", "t");

        Assert.Equal("orders", Value(vars, "host"));
        Assert.Equal("todas", Value(vars.Shared, "host"));
        Assert.True(vars.Shared.IsSession("token"));

        var plain = VariableContext.From([Var("host", "todas")], null);
        Assert.Same(plain, plain.Shared);
    }

    [Fact]
    public void El_tooltip_y_el_autocompletar_dicen_de_donde_sale_el_valor()
    {
        var vars = VariableContext.From(
            [Var("a", "1"), Var("b", "2", app: Orders), Var("c", "3", Env), Var("d", "4", Env, Orders)], Env, null, Orders);

        Assert.StartsWith($"a · {CoreTexts.Completion_Global}", VariableCompletion.Tooltip(vars, "a", "dev", "Orders"));
        Assert.StartsWith($"b · {string.Format(CoreTexts.Hover_AppGlobal, "Orders")}", VariableCompletion.Tooltip(vars, "b", "dev", "Orders"));
        Assert.StartsWith($"c · {string.Format(CoreTexts.Hover_Environment, "dev")}", VariableCompletion.Tooltip(vars, "c", "dev", "Orders"));
        Assert.StartsWith($"d · {string.Format(CoreTexts.Hover_AppScope, "Orders", "dev")}", VariableCompletion.Tooltip(vars, "d", "dev", "Orders"));

        var scopes = VariableCompletion.Suggestions(vars).Select(s => s.Description.Split(" · ")[0]).ToList();
        Assert.Equal(
            [CoreTexts.Completion_Global, CoreTexts.Completion_AppGlobal, CoreTexts.Completion_Environment, CoreTexts.Completion_AppEnvironment],
            scopes);
    }
}
