using Raf.Contracts;

namespace Raf.Client.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.4", "1.2.3")]
    [InlineData("1.2.3+0123abcd", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(" 10.0.1 ", "10.0.1")]
    public void Parses_the_numeric_part(string text, string expected)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    public void Rejects_what_is_not_a_version(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Compares_numerically_not_as_text()
    {
        AppVersion.TryParse("1.10.0", out var newer);
        AppVersion.TryParse("1.9.0", out var older);
        Assert.True(older < newer);
    }

    [Fact]
    public void Display_drops_build_metadata() => Assert.Equal("1.2.3-beta.1", AppVersion.Display("1.2.3-beta.1+abc"));
}
