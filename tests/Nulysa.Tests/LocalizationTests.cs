using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Localization;

namespace Nulysa.Tests;

public sealed class LocalizationTests : BoardTest
{
    [Fact]
    public async Task Language_selection_is_explicit_safe_and_survives_authentication_with_an_automatic_fallback()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var passwordToken = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, passwordToken, Password));
        await using var web = await StartProvider();
        using var client = Client(language: null);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fi-FI, en;q=0.5");
        var publicPage = await client.GetStringAsync("Account/Login");
        Assert.Contains("lang=\"fi\"", publicPage);
        Assert.Contains("Kirjaudu sisään", WebUtility.HtmlDecode(publicPage));
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        Assert.Contains("lang=\"fi\"", await client.GetStringAsync("profile"));
        var languagePage = await client.GetStringAsync("Language?returnUrl=https://example.test/");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("Language", new FormUrlEncodedContent(new Dictionary<string, string> { ["Language"] = "en" }))).StatusCode);
        var selected = await client.PostAsync("Language", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(languagePage),
            ["Language"] = "en",
            ["ReturnUrl"] = "https://example.test/"
        }));
        Assert.Equal(HttpStatusCode.Redirect, selected.StatusCode);
        Assert.StartsWith("/Language?", selected.Headers.Location!.OriginalString);
        Assert.DoesNotContain("example.test", selected.Headers.Location.OriginalString);
        Assert.Contains("HttpOnly", string.Join(";", selected.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        var english = await client.GetStringAsync("profile");
        Assert.Contains("lang=\"en\"", english);
        Assert.Contains("Member details", english);
        var denied = await client.GetAsync("admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("AccessDenied", denied.Headers.Location!.OriginalString);
        var invalid = await client.PostAsync("Language", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Token(english), ["Language"] = "sv" }));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Choose a listed language", await invalid.Content.ReadAsStringAsync());
        var reset = await client.PostAsync("Language", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Token(english), ["Language"] = "automatic" }));
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("lang=\"fi\"", await client.GetStringAsync("profile"));
    }

    [Fact]
    public async Task Finnish_comma_amount_survives_transfer_review_and_confirmation_without_javascript()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var view = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "bank", "deposit-fi", 5, "EUR", new(2026, 6, 1), view.DepositReference)));
        var passwordToken = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, passwordToken, Password));
        await using var web = await StartProvider();
        using var client = Client(language: "fi");
        await Login(client);
        var html = await client.GetStringAsync("services");
        Assert.Contains("inputmode=\"decimal\"", html);
        var data = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["_handler"] = "transfer",
            ["Transfer.Key"] = Guid.NewGuid().ToString(),
            ["Transfer.Amount"] = "1,25",
            ["Transfer.ToPurchase"] = "false"
        };
        var response = await client.PostAsync("services", new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Siirrä 1,25 EUR", review);
        Assert.Contains("name=\"Transfer.Amount\" value=\"1,25\"", review);
        data["_handler"] = "confirm-transfer";
        data["Transfer.Confirmed"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("services", new FormUrlEncodedContent(data))).StatusCode);
        var updated = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        Assert.Equal(3.75m, updated.Balances.PurchaseBalance);
        Assert.Equal(1.25m, updated.Balances.Held);
        data["_handler"] = "transfer";
        data["Transfer.Key"] = Guid.NewGuid().ToString();
        data["Transfer.Amount"] = "1.234,56";
        data.Remove("Transfer.Confirmed");
        var invalid = await client.PostAsync("services", new FormUrlEncodedContent(data));
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("role=\"alert\"", await invalid.Content.ReadAsStringAsync());
        var unchanged = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        Assert.Equal(updated.Balances, unchanged.Balances);
    }
    [Fact]
    public async Task Finnish_board_price_form_accepts_a_comma_amount_and_an_ISO_date()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        await Board();
        await using var web = await StartProvider();
        using var client = Client(language: "fi");
        await Login(client, "board@example.test");
        var html = await client.GetStringAsync("admin/settings/fees/1");
        var saved = await client.PostAsync("admin/settings/fees/1", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["_handler"] = "price",
            ["Price.Amount"] = "12,34",
            ["Price.EffectiveOn"] = "2026-06-15"
        }));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        await using var db = Context();
        var price = await db.FeePrices.SingleAsync(price => price.FeeDefinitionId == 1 && price.EffectiveOn == new DateOnly(2026, 6, 15));
        Assert.Equal(12.34m, price.MonthlyAmount);
        Assert.Contains("12,34", await client.GetStringAsync("admin/settings/fees/1"));
    }

}
