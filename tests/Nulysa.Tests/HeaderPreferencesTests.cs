using System.Net;

namespace Nulysa.Tests;

public sealed class HeaderPreferencesTests : BoardTest
{
    [Fact]
    public async Task Header_preferences_apply_independently_and_return_to_the_current_local_page()
    {
        await Board();
        await using var web = await StartProvider();
        using var client = Client();
        var html = await client.GetStringAsync("Account/Login?ReturnUrl=%2Fbilling");
        Assert.Contains("class=\"header-preferences\"", html);
        Assert.Contains("/Appearance?handler=Theme", html);
        Assert.Contains("/Appearance?handler=Mode", html);
        Assert.Contains("/Language?handler=Select", html);
        Assert.DoesNotContain("href=\"/Appearance\"", html);
        Assert.DoesNotContain("href=\"/Language\"", html);
        var token = Token(html);
        async Task<HttpResponseMessage> Post(string path, string name, string value, string returnUrl = "/Account/Login?ReturnUrl=%2Fbilling") =>
            await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                [name] = value,
                ["ReturnUrl"] = returnUrl
            }));
        var theme = await Post("Appearance?handler=Theme", "Theme", "tampere-hacklab");
        Assert.Equal(HttpStatusCode.Redirect, theme.StatusCode);
        Assert.Equal("/Account/Login?ReturnUrl=%2Fbilling", theme.Headers.Location!.OriginalString);
        Assert.DoesNotContain("Nulysa.Mode", string.Join(';', theme.Headers.GetValues("Set-Cookie")));
        var mode = await Post("Appearance?handler=Mode", "Mode", "dark");
        Assert.Equal(HttpStatusCode.Redirect, mode.StatusCode);
        Assert.DoesNotContain("Nulysa.Theme", string.Join(';', mode.Headers.GetValues("Set-Cookie")));
        var language = await Post("Language?handler=Select", "Language", "fi");
        Assert.Equal(HttpStatusCode.Redirect, language.StatusCode);
        html = await client.GetStringAsync("Account/Login");
        Assert.Contains("data-theme=\"tampere-hacklab\"", html);
        Assert.Contains("data-mode=\"dark\"", html);
        Assert.Contains("lang=\"fi\"", html);
        Assert.Contains("aria-label=\"Suomi\"", html);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("Appearance?handler=Theme", "Theme", "association")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("Appearance?handler=Mode", "Mode", "system")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post("Language?handler=Select", "Language", "automatic")).StatusCode);
        html = await client.GetStringAsync("Account/Login");
        Assert.Contains("data-theme=\"default\"", html);
        Assert.Contains("data-mode=\"system\"", html);
        Assert.Contains("lang=\"en\"", html);
        await Login(client, "board@example.test");
        var memberHeader = await client.GetStringAsync("admin/members?state=inactive");
        Assert.Contains("class=\"header-preferences\"", memberHeader);
        Assert.Contains("value=\"/admin/members?state=inactive\"", memberHeader);
        Assert.DoesNotContain("\"type\":\"server\"", memberHeader);
    }

    [Theory]
    [InlineData("Appearance?handler=Theme", "Theme", "default")]
    [InlineData("Appearance?handler=Mode", "Mode", "dark")]
    [InlineData("Language?handler=Select", "Language", "fi")]
    public async Task Quick_preferences_validate_choices_antiforgery_and_local_return_urls(string route, string name, string valid)
    {
        await using var web = await StartProvider();
        using var client = Client();
        var html = await client.GetStringAsync("Account/Login");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(route, new FormUrlEncodedContent(new Dictionary<string, string> { [name] = valid }))).StatusCode);
        var data = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(html), [name] = "https://example.test/invalid", ["ReturnUrl"] = "https://example.test/" };
        var invalid = await client.PostAsync(route, new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.False(invalid.Headers.Contains("Set-Cookie"));
        data[name] = valid;
        var saved = await client.PostAsync(route, new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/", saved.Headers.Location!.OriginalString);
    }
}
