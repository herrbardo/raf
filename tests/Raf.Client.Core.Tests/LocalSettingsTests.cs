using Raf.Client.Core.Local;

namespace Raf.Client.Core.Tests;

public class LocalSettingsTests
{
    [Fact]
    public void Remembers_servers_most_recent_first_without_duplicates()
    {
        var settings = new LocalSettings();

        settings.RememberServer("http://localhost:5081/");
        settings.RememberServer("https://example.com/raf/");
        settings.RememberServer("HTTP://LOCALHOST:5081/");

        Assert.Equal("HTTP://LOCALHOST:5081/", settings.ServerUrl);
        Assert.Equal(["HTTP://LOCALHOST:5081/", "https://example.com/raf/"], settings.RecentServers);
    }

    [Fact]
    public void Keeps_only_the_latest_servers()
    {
        var settings = new LocalSettings();

        for (var i = 0; i < LocalSettings.MaxRecentServers + 3; i++)
            settings.RememberServer($"https://server{i}.example.com/");

        Assert.Equal(LocalSettings.MaxRecentServers, settings.RecentServers.Count);
        Assert.Equal($"https://server{LocalSettings.MaxRecentServers + 2}.example.com/", settings.RecentServers[0]);
        Assert.DoesNotContain("https://server0.example.com/", settings.RecentServers);
    }

    [Fact]
    public void Survives_a_round_trip_to_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raf-settings-{Guid.NewGuid():N}.json");
        try
        {
            new SettingsStore(path).Update(s => s.RememberServer("https://example.com/raf/"));

            Assert.Equal(["https://example.com/raf/"], new SettingsStore(path).Current.RecentServers);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
