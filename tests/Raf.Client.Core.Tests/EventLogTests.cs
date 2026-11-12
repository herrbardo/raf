using Raf.Client.Core.Diagnostics;

namespace Raf.Client.Core.Tests;

public class EventLogTests
{
    [Fact]
    public void Guarda_en_orden_y_descarta_los_mas_viejos_al_llenarse()
    {
        var log = new EventLog(TimeProvider.System, capacity: 3);
        for (var i = 1; i <= 5; i++) log.Info(EventCategory.Sync, $"evento {i}");

        Assert.Equal(["evento 3", "evento 4", "evento 5"], log.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void Avisa_cada_evento_nuevo_y_la_limpieza()
    {
        var log = new EventLog();
        var added = new List<AppEvent>();
        var cleared = 0;
        log.Added += added.Add;
        log.Cleared += () => cleared++;

        log.Error(EventCategory.App, "falló", new InvalidOperationException("detalle técnico"));
        log.Clear();

        var e = Assert.Single(added);
        Assert.Equal(EventLevel.Error, e.Level);
        Assert.Contains("detalle técnico", e.Detail);
        Assert.Equal(1, cleared);
        Assert.Empty(log.Snapshot());
    }

    [Theory]
    [InlineData("wss://srv/hubs/changes?access_token=abc.def-123&x=1", "wss://srv/hubs/changes?access_token=[oculto]&x=1")]
    [InlineData("Authorization: Bearer abc123.def", "Authorization: Bearer [oculto]")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.firma_x-y", "token [token oculto]")]
    [InlineData("{\"refreshToken\":\"r3fr3sh\",\"x\":1}", "{\"refreshToken\":\"[oculto]\",\"x\":1}")]
    [InlineData("password=hunter2 y nada más", "password=[oculto] y nada más")]
    [InlineData("No se pudo conectar con el servidor.", "No se pudo conectar con el servidor.")]
    public void Oculta_tokens_y_contrasenas(string input, string expected) =>
        Assert.Equal(expected, EventLog.Redact(input));

    [Fact]
    public void Oculta_tambien_el_detalle()
    {
        var log = new EventLog();
        log.Warning(EventCategory.Connection, "corte", "GET /hubs/changes?access_token=secreto falló");

        Assert.Equal("GET /hubs/changes?access_token=[oculto] falló", Assert.Single(log.Snapshot()).Detail);
    }
}
