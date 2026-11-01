using Microsoft.AspNetCore.Http;
using Nulysa.Components;

namespace Nulysa.Tests;

public sealed class AppearanceTests
{
    [Theory]
    [InlineData("default", "light")]
    [InlineData("tampere-hacklab", "dark")]
    [InlineData("default", "system")]
    public void Known_browser_preferences_override_association_default(string theme, string mode)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{Appearance.ThemeCookie}={theme}; {Appearance.ModeCookie}={mode}";

        var result = Appearance.Read(context, new AppearanceOptions { DefaultTheme = "tampere-hacklab" });

        Assert.Equal(new Appearance(theme, mode), result);
    }

    [Fact]
    public void Unknown_cookie_values_fall_back_without_becoming_asset_paths()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{Appearance.ThemeCookie}=https://example.test/evil.css; {Appearance.ModeCookie}=unknown";

        var result = Appearance.Read(context, new AppearanceOptions { DefaultTheme = "tampere-hacklab" });

        Assert.Equal(new Appearance("tampere-hacklab", "system"), result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("/styles/association.css", true)]
    [InlineData("https://example.test/theme.css", false)]
    [InlineData("//example.test/theme.css", false)]
    [InlineData("/../secret.css", false)]
    [InlineData("/styles/theme.css?url=remote", false)]
    public void Only_local_stylesheet_paths_are_supported(string? path, bool valid)
    {
        Assert.Equal(valid, new AppearanceOptions { Stylesheet = path }.IsValid());
        Assert.False(new AppearanceOptions { DefaultTheme = "unknown" }.IsValid());
    }
}
