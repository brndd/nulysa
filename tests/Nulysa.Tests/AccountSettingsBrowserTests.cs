using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Accounts;
using Nulysa.Communications;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class AccountSettingsBrowserTests : BoardTest
{
    [Fact]
    public async Task Front_page_settings_preference_dirty_state_responsive_modes_and_password_change_work_together()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        var transport = new RecordingMail();
        await using var commands = CommandsApp(transport);
        var id = await Admit(commands, board);
        var token = await PasswordToken(commands, id);
        await Run(commands, services => services.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.Context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "Nulysa.Language", Value = "en", Url = Settings["Oidc:Issuer"]! }]);
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your membership", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#funds")).ToHaveCountAsync(0);
        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Member self-service", Exact = true });
        await Assertions.Expect(nav.Locator("a[href='/profile']")).ToHaveCountAsync(1);
        await Assertions.Expect(nav.Locator("a[href='/profile#communications']")).ToHaveCountAsync(0);
        await nav.GetByRole(AriaRole.Link, new() { Name = "Account overview", Exact = true }).ClickAsync();
        foreach (var width in new[] { 1280, 1024, 800, 768, 767, 700, 600, 375, 320 })
        {
            await page.SetViewportSizeAsync(width, 900);
            var cards = page.Locator("#funds .balance-cards > div");
            var first = (await cards.Nth(0).BoundingBoxAsync())!;
            var second = (await cards.Nth(1).BoundingBoxAsync())!;
            Assert.Equal(width >= 768, Math.Abs(first.Y - second.Y) < 1);
            var menu = page.GetByRole(AriaRole.Button, new() { Name = "Menu", Exact = true });
            if (width >= 768)
                await Assertions.Expect(menu).ToBeHiddenAsync();
            else
                await Assertions.Expect(menu).ToBeVisibleAsync();
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        }
        var save = page.GetByRole(AriaRole.Button, new() { Name = "Save preference", Exact = true });
        await Assertions.Expect(save).ToBeDisabledAsync();
        await page.GetByLabel("Email invoice PDFs").UncheckAsync();
        await Assertions.Expect(save).ToBeEnabledAsync();
        await page.GetByLabel("Email invoice PDFs").CheckAsync();
        await Assertions.Expect(save).ToBeDisabledAsync();
        await page.GetByLabel("Email invoice PDFs").UncheckAsync();
        await save.ClickAsync();
        await Assertions.Expect(save).ToBeDisabledAsync();
        await page.GotoAsync(root + "profile#account");
        await page.GetByLabel("Current password", new() { Exact = true }).FillAsync("wrong");
        await page.GetByLabel("New password", new() { Exact = true }).FillAsync("Changed-password-456!");
        await page.GetByLabel("Confirm new password", new() { Exact = true }).FillAsync("Changed-password-456!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Incorrect password");
        await Assertions.Expect(page.GetByLabel("Current password", new() { Exact = true })).ToHaveValueAsync("");
        await page.GetByLabel("Current password", new() { Exact = true }).FillAsync(Password);
        await page.GetByLabel("New password", new() { Exact = true }).FillAsync("Changed-password-456!");
        await page.GetByLabel("Confirm new password", new() { Exact = true }).FillAsync("Changed-password-456!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Change password", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Password changed");
        await using var db = Context();
        var notice = await db.Deliveries.SingleAsync(delivery => delivery.Kind == MailKind.PasswordChanged);
        await Run(commands, services => services.GetRequiredService<MailDispatcher>().Dispatch(notice.Id));
        Assert.Single(transport.Sent);
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Unable to log in with these credentials.", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("Changed-password-456!");
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Member portal", Exact = true })).ToBeVisibleAsync();
    }
}
