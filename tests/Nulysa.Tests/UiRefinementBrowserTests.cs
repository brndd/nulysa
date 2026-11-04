using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class UiRefinementBrowserTests : BoardTest
{
    [Fact]
    public async Task Inline_review_debounces_approval_and_rejection_dialog_supports_cancel_escape_and_no_script()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var first = await Verified(commands);
        var second = await Verified(commands, "second@example.test");
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 375, Height = 812 } });
        await context.AddCookiesAsync([new Cookie { Name = "Nulysa.Language", Value = "en", Url = Settings["Oidc:Issuer"]! }]);
        var page = await context.NewPageAsync();
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.GotoAsync(root + "admin/applications");
        var card = page.Locator(".application-card").Filter(new() { HasText = "second@example.test" });
        await Assertions.Expect(card.GetByRole(AriaRole.Img, new() { Name = "Applicant is underage" })).ToBeVisibleAsync();
        var evidence = Environment.GetEnvironmentVariable("NULYSA_UI_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "application-mobile.png"), FullPage = true });
        }
        var extra = card.Locator(".application-extra");
        await Assertions.Expect(extra.Locator("dd").First).Not.ToBeVisibleAsync();
        await extra.Locator("summary").ClickAsync();
        await Assertions.Expect(extra.Locator("dd").First).ToHaveTextAsync("12 Jun 2010");
        await extra.Locator("summary").ClickAsync();
        await card.GetByText("Reject application", new() { Exact = true }).ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByLabel("Message to applicant", new() { Exact = false }).FillAsync("Unsent draft");
        await dialog.PressAsync("Escape");
        await Assertions.Expect(card.GetByText("Reject application", new() { Exact = true })).ToBeFocusedAsync();
        await using var db = Context();
        Assert.Null((await db.Applications.AsNoTracking().SingleAsync(application => application.Id == second)).RejectedAt);
        await card.GetByText("Reject application", new() { Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        Assert.Null((await db.Applications.AsNoTracking().SingleAsync(application => application.Id == second)).RejectedAt);
        var firstCard = page.Locator(".application-card").Filter(new() { HasText = Email });
        await firstCard.GetByRole(AriaRole.Button, new() { Name = "Approve membership", Exact = true }).ClickAsync();
        await Assertions.Expect(firstCard.GetByRole(AriaRole.Button, new() { Name = "Confirm membership", Exact = true })).ToBeDisabledAsync();
        Assert.Empty(await db.Members.ToListAsync());
        await firstCard.GetByRole(AriaRole.Button, new() { Name = "Confirm membership", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Membership approved");
        Assert.Equal(first, (await db.Approvals.SingleAsync()).Id);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true })
            .GetByRole(AriaRole.Link, new() { Name = "Details", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Details", Exact = true })).ToBeFocusedAsync();
        await page.SetViewportSizeAsync(1920, 1080);
        Assert.True(await page.EvaluateAsync<bool>("""
            (() => {
                const fields = [...document.querySelectorAll('.profile-fields-grid > p')].slice(0, 4);
                return fields.length === 4 && fields.every(field => field.getBoundingClientRect().top === fields[0].getBoundingClientRect().top);
            })()
            """));
        var removal = page.GetByText("Remove account", new() { Exact = true });
        await Assertions.Expect(removal).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("danger-action"));
        await page.SetViewportSizeAsync(375, 812);
        await page.GotoAsync(root + "services");
        var tabs = page.GetByRole(AriaRole.Navigation, new() { Name = "Membership and payments", Exact = true });
        await tabs.GetByRole(AriaRole.Link, new() { Name = "Billing details", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Billing details", Exact = true })).ToBeVisibleAsync();
        await tabs.GetByRole(AriaRole.Link, new() { Name = "Account overview", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Account overview", Exact = true })).ToBeVisibleAsync();


        await using var plainContext = await browser.NewContextAsync(new() { JavaScriptEnabled = false, StorageState = await context.StorageStateAsync() });
        var plain = await plainContext.NewPageAsync();
        await plain.GotoAsync(root + "admin/applications");
        await plain.GetByText("Reject application", new() { Exact = true }).ClickAsync();
        await plain.GetByLabel("Message to applicant", new() { Exact = false }).FillAsync("Reviewed rejection");
        await plain.GetByRole(AriaRole.Button, new() { Name = "Reject and email applicant", Exact = true }).ClickAsync();
        await Assertions.Expect(plain.GetByRole(AriaRole.Status)).ToContainTextAsync("Application rejected");
        Assert.NotNull((await db.Applications.AsNoTracking().SingleAsync(application => application.Id == second)).RejectedAt);
        Assert.Single(await db.Members.ToListAsync());
    }
}
