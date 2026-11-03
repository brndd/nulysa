using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Protocol")]
public sealed class UiTests : OidcTest
{
    [Fact]
    public async Task Appearance_is_shared_by_account_and_portal_and_posts_require_antiforgery()
    {
        await CreateUser();
        Settings["Appearance:DefaultTheme"] = "tampere-hacklab";
        await using var app = await StartProvider();
        using var client = Client();
        var login = await client.GetStringAsync("/Account/Login");
        Assert.Contains("data-theme=\"tampere-hacklab\"", login);
        Assert.Contains("/styles/site.css", login);
        foreach (var asset in new[] { "/styles/site.css", "/scripts/appearance.js", "/vendor/bootstrap/bootstrap.min.css" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(asset)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Appearance", new FormUrlEncodedContent([]))).StatusCode);

        var html = await client.GetStringAsync("/Appearance");
        Assert.Contains("href=\"/Account/Login\"", html);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var invalid = await client.PostAsync("/Appearance", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Theme"] = "https://example.test/evil.css",
            ["Mode"] = "dark",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Contains("Choose a listed theme", await invalid.Content.ReadAsStringAsync());
        var saved = await client.PostAsync("/Appearance", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Theme"] = "default",
            ["Mode"] = "dark",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("data-mode=\"dark\"", await client.GetStringAsync("/Account/Login"));
        await Login(client);
        var portal = await client.GetStringAsync("/");
        Assert.Contains("data-theme=\"default\"", portal);
        Assert.Contains("data-mode=\"dark\"", portal);
        Assert.Contains("Skip to content", portal);
        Assert.DoesNotContain("\"type\":\"server\"", portal);
        html = await client.GetStringAsync("/Appearance");
        Assert.DoesNotContain("href=\"/Account/Login\"", html);
        Assert.Contains("Return to portal", html);
        token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var reset = await client.PostAsync("/Appearance", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Theme"] = "association",
            ["Mode"] = "system",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token)
        }));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        var restored = await client.GetStringAsync("/");
        Assert.Contains("data-theme=\"tampere-hacklab\"", restored);
        Assert.Contains("data-mode=\"system\"", restored);
    }

    [Fact]
    public async Task Portal_is_static_SSR_with_branding_and_an_antiforgery_protected_HTTP_logout()
    {
        await CreateUser();
        Settings["Association:Name"] = "Synthetic Association";
        await using var app = await StartProvider();
        using var client = Client();
        var anonymous = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/Account/Login", anonymous.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        var html = await client.GetStringAsync("/");

        Assert.Contains("<h1>Member portal</h1>", html);
        Assert.Contains("Member portal — Synthetic Association", WebUtility.HtmlDecode(html));
        Assert.Contains("Powered by Nulysa", html);
        Assert.DoesNotContain("\"type\":\"server\"", html);
        Assert.DoesNotContain("\"type\":\"webassembly\"", html);
        var script = await client.GetAsync("_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Blazor", await script.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("unmapped-page")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("Account/Logout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        var token = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>");

        Assert.True(token.Success, html);
        var logout = await client.PostAsync("Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value)
        }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/Account/Login", logout.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }
}

[Trait("Group", "Browser")]
[Trait("Category", "Slow")]
public sealed class InteractiveUiTests : OidcTest
{
    [Fact]
    public async Task Opt_in_server_component_handles_events_reconnects_and_revalidates_account_gates()
    {
        var user = await CreateUser();
        await using var app = Build("web");
        // Test-only component/route: no demonstration widget is shipped in the member portal.
        app.MapGet("/interactive-proof", () => new RazorComponentResult<InteractiveDocument>()).RequireAuthorization();
        app.Urls.Add(Settings["Oidc:Issuer"]!);
        await app.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.Context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "Nulysa.Language", Value = "en", Url = Settings["Oidc:Issuer"]! }]);
        await page.AddInitScriptAsync("""
            window.testSockets = [];
            window.WebSocket = class extends WebSocket {
                constructor(...args) { super(...args); window.testSockets.push(this); }
            };
            """);
        await page.GotoAsync(Settings["Oidc:Issuer"] + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Member portal" })).ToBeVisibleAsync();
        await page.GotoAsync(Settings["Oidc:Issuer"] + "interactive-proof");
        var button = page.GetByRole(AriaRole.Button, new() { Name = "Increment" });
        await button.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("1");
        await Assertions.Expect(page.GetByText("Signed in", new() { Exact = true })).ToBeVisibleAsync();
        await page.EvaluateAsync("() => window.testSockets.forEach(socket => socket.close())");
        await page.WaitForFunctionAsync("() => window.connectionDown === true");

        Assert.True(await page.EvaluateAsync<bool>("() => Blazor.reconnect()"));
        await button.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveTextAsync("2");
        await using (var db = Context())
            await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Admitted, false));
        await Assertions.Expect(page.GetByText("Signed out", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = 45000 });
        await page.GotoAsync(Settings["Oidc:Issuer"]!);

        Assert.Equal("/Account/Login", new Uri(page.Url).AbsolutePath);
    }
}
