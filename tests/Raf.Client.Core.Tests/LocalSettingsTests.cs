using Raf.Client.Core.Local;

namespace Raf.Client.Core.Tests;

public class LocalSettingsTests
{
    [Fact]
    public void Adds_servers_normalized_and_in_order()
    {
        var settings = new LocalSettings();

        var local = settings.SaveServer(null, "  Local ", "http://localhost:5081");
        var remote = settings.SaveServer(null, " ", "example.com/raf");

        Assert.Equal(ServerSaveError.None, local.Error);
        Assert.Equal(new SavedServer("http://localhost:5081/", "Local"), local.Server);
        Assert.Equal(new SavedServer("https://example.com/raf/"), remote.Server);
        Assert.Equal("https://example.com/raf/", remote.Server!.Title);
        Assert.Equal([local.Server!, remote.Server!], settings.Servers);
    }

    [Fact]
    public void Rejects_invalid_and_repeated_urls()
    {
        var settings = new LocalSettings();
        settings.SaveServer(null, null, "http://localhost:5081/");

        Assert.Equal(ServerSaveError.InvalidUrl, settings.SaveServer(null, "x", "ftp://example.com").Error);
        Assert.Equal(ServerSaveError.InvalidUrl, settings.SaveServer(null, "x", "  ").Error);
        Assert.Equal(ServerSaveError.Duplicate, settings.SaveServer(null, "x", "HTTP://LOCALHOST:5081").Error);
        Assert.Single(settings.Servers);
    }

    [Fact]
    public void Edits_a_server_in_place_and_the_choice_follows_it()
    {
        var settings = new LocalSettings();
        var first = settings.SaveServer(null, "A", "https://a.example.com/").Server!;
        var second = settings.SaveServer(null, "B", "https://b.example.com/").Server!;
        settings.ServerUrl = first.Url;

        // Cambiar solo el nombre, con la misma URL, no es un repetido.
        Assert.Equal(ServerSaveError.None, settings.SaveServer(first, "A2", "https://a.example.com/").Error);
        var renamed = settings.Servers[0];
        Assert.Equal(ServerSaveError.Duplicate, settings.SaveServer(renamed, "A2", "https://b.example.com/").Error);

        var moved = settings.SaveServer(renamed, "A3", "https://c.example.com/").Server!;

        Assert.Equal([moved, second], settings.Servers);
        Assert.Equal("https://c.example.com/", settings.ServerUrl);
    }

    [Fact]
    public void Removing_the_chosen_server_clears_the_choice()
    {
        var settings = new LocalSettings();
        var first = settings.SaveServer(null, "A", "https://a.example.com/").Server!;
        var second = settings.SaveServer(null, "B", "https://b.example.com/").Server!;
        settings.ServerUrl = second.Url;

        settings.RemoveServer(first);
        Assert.Equal(second.Url, settings.ServerUrl);

        settings.RemoveServer(second);
        Assert.Empty(settings.Servers);
        Assert.Null(settings.ServerUrl);
    }

    [Fact]
    public void Migrates_the_recent_servers_of_older_versions()
    {
        var storage = new MemorySecureStore();
        storage.Write("settings", """
            { "serverUrl": "https://c.example.com/",
              "recentServers": ["https://a.example.com/", "no es una url://", "HTTPS://A.EXAMPLE.COM", "http://localhost:5081/"] }
            """);

        var store = new SettingsStore(storage, "settings");

        Assert.Equal(
            [new SavedServer("https://a.example.com/"), new SavedServer("http://localhost:5081/"), new SavedServer("https://c.example.com/")],
            store.Current.Servers);
        Assert.Null(store.Current.RecentServers);

        // Una vez guardada, la lista no vuelve a migrarse: lo borrado queda borrado.
        store.Update(s => s.RemoveServer(s.Servers[0]));
        Assert.Equal(2, new SettingsStore(storage, "settings").Current.Servers.Count);
        Assert.DoesNotContain("recentServers", storage.Read("settings"));
    }

    [Fact]
    public void Survives_a_round_trip_to_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raf-settings-{Guid.NewGuid():N}.json");
        try
        {
            new SettingsStore(path).Update(s => s.SaveServer(null, "Raf", "https://example.com/raf/"));

            Assert.Equal([new SavedServer("https://example.com/raf/", "Raf")], new SettingsStore(path).Current.Servers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Survives_a_round_trip_to_a_text_store()
    {
        var storage = new MemorySecureStore(); // como el localStorage del cliente web

        new SettingsStore(storage, "settings").Update(s => s.Language = "en");

        Assert.Equal("en", new SettingsStore(storage, "settings").Current.Language);
        Assert.Null(new SettingsStore(storage, "otra").Current.Language);
    }

    [Fact]
    public void A_broken_text_store_value_starts_with_defaults()
    {
        var storage = new MemorySecureStore();
        storage.Write("settings", "{ no es json");

        Assert.Equal(30, new SettingsStore(storage, "settings").Current.DefaultTimeoutSeconds);
    }
}
