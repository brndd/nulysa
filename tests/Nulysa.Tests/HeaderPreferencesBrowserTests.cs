using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class HeaderPreferencesBrowserTests : OidcTest
{
    [Fact]
    public async Task Header_dropdowns_and_sun_moon_switch_work_with_keyboard_system_mode_and_enhanced_navigation()
    {
        await CreateUser();
        Settings["Appearance:DefaultTheme"] = "tampere-hacklab";
        await using var app = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 320, Height = 812 }, ColorScheme = ColorScheme.Dark });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        var sockets = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.WebSocket += (_, socket) => sockets.Add(socket.Url);
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login?ReturnUrl=%2Fbilling");
        var header = page.Locator(".header-preferences");
        var toggle = header.GetByRole(AriaRole.Switch, new() { Name = "Dark mode", Exact = true });
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "true");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "false");
        await toggle.ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "dark");
        Assert.EndsWith("Account/Login?ReturnUrl=%2Fbilling", page.Url);
        await Assertions.Expect(toggle).ToHaveAttributeAsync("aria-checked", "true");
        var theme = header.Locator(".theme-dropdown > summary");
        await theme.FocusAsync();
        await theme.PressAsync("Enter");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        var first = header.GetByRole(AriaRole.Button, new() { Name = "Association default", Exact = true });
        await theme.PressAsync("Tab");
        await Assertions.Expect(first).ToBeFocusedAsync();
        await first.PressAsync("Escape");
        await Assertions.Expect(theme).ToBeFocusedAsync();
        await Assertions.Expect(header.Locator("details[open]")).ToHaveCountAsync(0);
        await theme.ClickAsync();
        await header.GetByRole(AriaRole.Button, new() { Name = "Default", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "default");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "dark");
        await theme.ClickAsync();
        await header.Locator(".language-dropdown > summary").ClickAsync();
        await Assertions.Expect(header.Locator(".theme-dropdown")).Not.ToHaveAttributeAsync("open", "");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await header.GetByRole(AriaRole.Button, new() { Name = "Suomi", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "fi");
        await Assertions.Expect(header.Locator(".language-dropdown > summary")).ToHaveAttributeAsync("title", "Kieli: Suomi");
        await header.GetByRole(AriaRole.Switch, new() { Name = "Tumma tila" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "light");
        await header.Locator(".theme-dropdown > summary").ClickAsync();
        await header.GetByRole(AriaRole.Button, new() { Name = "Käytä laitteen väritilaa" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "system");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Assertions.Expect(header.GetByRole(AriaRole.Switch, new() { Name = "Tumma tila" })).ToHaveAttributeAsync("aria-checked", "true");
        await page.GetByLabel("Sähköposti", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Salasana", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Kirjaudu sisään", Exact = true }).ClickAsync();
        await Assertions.Expect(header.GetByRole(AriaRole.Switch, new() { Name = "Tumma tila" })).ToBeVisibleAsync();
        await page.SetViewportSizeAsync(1280, 800);
        await page.GetByRole(AriaRole.Link, new() { Name = "Asetukset", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Jäsenen tiedot", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(header.GetByRole(AriaRole.Switch, new() { Name = "Tumma tila" })).ToHaveAttributeAsync("aria-checked", "true");
        await header.GetByRole(AriaRole.Switch, new() { Name = "Tumma tila" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "light");
        var evidence = Environment.GetEnvironmentVariable("NULYSA_UI_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "header-desktop.png") });
            await page.SetViewportSizeAsync(320, 812);
            await header.Locator(".theme-dropdown > summary").ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "header-mobile.png") });
        }
        Assert.Empty(errors);
        Assert.Empty(sockets);
    }

    [Fact]
    public async Task Native_header_forms_work_without_javascript_and_keep_preferences_independent()
    {
        await using var app = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 812 }, ColorScheme = ColorScheme.Dark });
        var page = await context.NewPageAsync();
        await page.GotoAsync(Settings["Oidc:Issuer"]! + "Account/Login");
        var header = page.Locator(".header-preferences");
        await header.Locator(".theme-dropdown > summary").ClickAsync();
        await header.GetByRole(AriaRole.Button, new() { Name = "Tampere Hacklab" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "tampere-hacklab");
        await header.GetByRole(AriaRole.Button, new() { Name = "Dark", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "dark");
        await header.Locator(".language-dropdown > summary").ClickAsync();
        await header.GetByRole(AriaRole.Button, new() { Name = "Suomi", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "fi");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-mode", "dark");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "tampere-hacklab");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
    }
}
