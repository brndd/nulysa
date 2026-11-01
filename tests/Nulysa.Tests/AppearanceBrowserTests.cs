using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class AppearanceBrowserTests : OidcTest
{
    [Fact]
    public async Task Themes_modes_mobile_navigation_and_no_script_fallback_work_without_a_circuit()
    {
        await CreateUser();
        await using var app = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 375, Height = 812 }, ColorScheme = ColorScheme.Dark });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Member portal" })).ToBeVisibleAsync();
        var menu = page.GetByRole(AriaRole.Button, new() { Name = "Menu", Exact = true });
        await Assertions.Expect(page.Locator(".site-header .navigation-toggle")).ToBeVisibleAsync();
        await Assertions.Expect(menu.Locator("svg[aria-hidden='true']")).ToHaveCountAsync(1);
        var menuBounds = (await menu.BoundingBoxAsync())!;
        var brandBounds = (await page.Locator(".association-name").BoundingBoxAsync())!;
        Assert.True(menuBounds.Y < brandBounds.Y + brandBounds.Height && brandBounds.Y < menuBounds.Y + menuBounds.Height);
        await menu.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Member self-service" })).ToBeVisibleAsync();
        await menu.PressAsync("Escape");
        await Assertions.Expect(menu).ToBeFocusedAsync();
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");

        foreach (var theme in new[] { "default", "tampere-hacklab" })
        {
            foreach (var mode in new[] { "light", "dark" })
            {
                await page.GotoAsync(root + "Appearance");
                await page.GetByLabel("Theme", new() { Exact = true }).SelectOptionAsync(theme);
                await page.GetByLabel("Colour mode").SelectOptionAsync(mode);
                await page.GetByRole(AriaRole.Button, new() { Name = "Save appearance" }).ClickAsync();
                await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Appearance saved.");
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", mode);
                Assert.Equal(mode, await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).colorScheme"));
                var expectedBackground = (theme, mode) switch
                {
                    ("default", "light") => "rgb(243, 245, 247)",
                    ("default", "dark") => "rgb(17, 24, 32)",
                    ("tampere-hacklab", "light") => "rgb(245, 245, 237)",
                    _ => "rgb(21, 22, 17)"
                };
                Assert.Equal(expectedBackground, await page.EvaluateAsync<string>("getComputedStyle(document.body).backgroundColor"));
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            }
        }

        await page.GetByLabel("Colour mode").SelectOptionAsync("system");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save appearance" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");
        await page.GetByRole(AriaRole.Link, new() { Name = "Return to portal" }).ClickAsync();
        await menu.ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "My details", Exact = true }).First.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Member details", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "false");
        await page.SetViewportSizeAsync(1280, 800);
        await Assertions.Expect(menu).ToBeHiddenAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Navigation, new() { Name = "Member self-service" })).ToBeVisibleAsync();
        Assert.Empty(errors);

        await using var noScript = await browser.NewContextAsync(new() { JavaScriptEnabled = false, ColorScheme = ColorScheme.Dark });
        var plain = await noScript.NewPageAsync();
        await plain.GotoAsync(root + "Appearance");
        await plain.GetByLabel("Theme", new() { Exact = true }).SelectOptionAsync("tampere-hacklab");
        await plain.GetByRole(AriaRole.Button, new() { Name = "Save appearance" }).ClickAsync();
        await Assertions.Expect(plain.GetByRole(AriaRole.Status)).ToHaveTextAsync("Appearance saved.");
        await Assertions.Expect(plain.Locator("html")).ToHaveAttributeAsync("data-theme", "tampere-hacklab");
    }
}
