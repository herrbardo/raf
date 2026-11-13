using Raf.Client.Core.Execution;
using Raf.Client.Core.Local;

namespace Raf.Client.Core.Tests;

public class SessionVariableStoreTests
{
    private readonly MemorySecureStore _secure = new();
    private readonly Guid _ws = Guid.NewGuid();
    private readonly Guid _dev = Guid.NewGuid();

    private SessionVariableStore Store(string user = "u1") => new(_secure, user);

    [Fact]
    public void Separa_por_environment_y_persiste_entre_instancias()
    {
        var store = Store();
        store.Set(_ws, _dev, "token", "de-dev");
        store.Set(_ws, null, "token", "sin-env");

        var reopened = Store();
        Assert.Equal("de-dev", reopened.Get(_ws, _dev)["token"]);
        Assert.Equal("sin-env", reopened.Get(_ws, null)["token"]);
        Assert.Empty(reopened.Get(Guid.NewGuid(), _dev));
        Assert.Empty(Store("otro-usuario").Get(_ws, _dev));
    }

    [Fact]
    public void Borrar_una_o_todas_y_avisar_solo_si_cambia()
    {
        var store = Store();
        var changes = 0;
        store.Changed += () => changes++;

        store.Set(_ws, _dev, "a", "1");
        store.Set(_ws, _dev, "a", "1");
        store.Set(_ws, _dev, "b", "2");
        Assert.Equal(2, changes);

        store.Remove(_ws, _dev, "a");
        store.Remove(_ws, _dev, "no-existe");
        Assert.Equal(["b"], store.Get(_ws, _dev).Keys);
        Assert.Equal(3, changes);

        store.Clear(_ws, _dev);
        Assert.Empty(Store().Get(_ws, _dev));
        Assert.Null(_secure.Read($"session-vars-u1-{_ws:N}"));
    }

    [Theory]
    [InlineData("token", true)]
    [InlineData("user.id-2_b", true)]
    [InlineData("con espacio", false)]
    [InlineData("{{x}}", false)]
    [InlineData("", false)]
    public void Valida_el_nombre(string name, bool valid)
    {
        Assert.Equal(valid, SessionVariableStore.IsValidName(name));
        if (!valid) Assert.Throws<ArgumentException>(() => Store().Set(_ws, null, name, "v"));
    }

    [Fact]
    public void Un_registro_danado_empieza_vacio()
    {
        _secure.Write($"session-vars-u1-{_ws:N}", "no es json");
        Assert.Empty(Store().Get(_ws, null));
    }

    [Fact]
    public void Guarda_el_vencimiento_y_un_valor_nuevo_sin_vencimiento_lo_quita()
    {
        var expires = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Store().Set(_ws, _dev, "token", "t-1", expires);

        var reopened = Store();
        Assert.Equal("t-1", reopened.Get(_ws, _dev)["token"]);
        Assert.Equal(expires, reopened.ExpiresAt(_ws, _dev, "token"));
        Assert.Equal([new SessionVariable("token", "t-1", expires)], reopened.List(_ws, _dev));
        Assert.Null(reopened.ExpiresAt(_ws, null, "token"));

        reopened.Set(_ws, _dev, "token", "a-mano");
        Assert.Null(Store().ExpiresAt(_ws, _dev, "token"));
    }

    [Fact]
    public void Lee_los_registros_anteriores_al_vencimiento()
    {
        _secure.Write($"session-vars-u1-{_ws:N}", $"{{\"{_dev:N}\":{{\"token\":\"viejo\"}}}}");

        var store = Store();
        Assert.Equal("viejo", store.Get(_ws, _dev)["token"]);
        Assert.Null(store.ExpiresAt(_ws, _dev, "token"));
    }

    [Fact]
    public void El_autocompletar_marca_las_de_sesion()
    {
        var vars = VariableContext.From([], null, new Dictionary<string, string> { ["token"] = "x" });
        var suggestion = Assert.Single(VariableCompletion.Suggestions(vars));
        Assert.StartsWith("Sesión", suggestion.Description);
        Assert.DoesNotContain("x", suggestion.Description.Replace("Sesión", ""));
    }
}
