using System.Text.RegularExpressions;

namespace Nulysa.Components;

public sealed class AppearanceOptions
{
    public string DefaultTheme { get; set; } = "default";
    public string? Stylesheet { get; set; }

    public bool IsValid() => Appearance.IsTheme(DefaultTheme) &&
        (Stylesheet is null || Regex.IsMatch(Stylesheet, @"^/(?!/)[a-zA-Z0-9/_-]+\.css$"));
}

public sealed record Appearance(string Theme, string Mode)
{
    public const string ThemeCookie = "Nulysa.Theme";
    public const string ModeCookie = "Nulysa.Mode";

    public static bool IsTheme(string? value) => value is "default" or "tampere-hacklab";
    public static bool IsMode(string? value) => value is "system" or "light" or "dark";

    public static Appearance Read(HttpContext context, AppearanceOptions options)
    {
        var theme = context.Request.Cookies[ThemeCookie];
        var mode = context.Request.Cookies[ModeCookie];
        return new(IsTheme(theme) ? theme! : options.DefaultTheme, IsMode(mode) ? mode! : "system");
    }
}
