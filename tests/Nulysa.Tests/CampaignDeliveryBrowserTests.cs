using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Communications;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class CampaignDeliveryBrowserTests : BoardTest
{
    [Fact]
    public async Task Board_can_test_review_confirm_send_and_retry_only_definite_failures()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var commands = CommandsApp(transport);
        var first = await Admit(commands, board, "first@example.test");
        var second = await Admit(commands, board, "second@example.test");
        var third = await Admit(commands, board, "third@example.test");
        var campaign = Guid.NewGuid();
        await Run(commands, sp => sp.GetRequiredService<Campaigns>().Save(board, campaign, null, "Workshop campaign",
            CampaignKind.Newsletter, [MailMarkdown.Parse("en", "Workshop news", "**Bring tools**")]));
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var root = Settings["Oidc:Issuer"]!;
        await context.AddCookiesAsync([new Cookie { Name = "Nulysa.Language", Value = "en", Url = root }]);
        var page = await context.NewPageAsync();
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();

        await page.GotoAsync(root + $"admin/communications/drafts/{campaign}");
        await page.GetByRole(AriaRole.Link, new() { Name = "Review and send saved draft" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Suomi · 3 recipients" })).ToBeVisibleAsync();
        var send = page.GetByRole(AriaRole.Button, new() { Name = "Queue bulk send", Exact = true });
        await Assertions.Expect(send).ToBeDisabledAsync();
        await page.GetByLabel("Fallback for Suomi").SelectOptionAsync("en");
        await Assertions.Expect(page.GetByText("Outgoing language: English")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Bring tools", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(send).ToBeDisabledAsync();

        await page.GetByLabel("Test email address").FillAsync("review@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Queue test email" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Test email queued.", new() { Exact = true })).ToBeVisibleAsync();
        await using var db = Context();
        var test = await db.Deliveries.AsNoTracking().SingleAsync(x => x.CampaignId == campaign);
        Assert.Equal(MailKind.CampaignTest, test.Kind);
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(test.Id));
        Assert.Equal("review@example.test", Assert.Single(transport.Sent).To);

        // A changed audience invalidates the confirmation already shown by the UI.
        await Run(commands, sp => sp.GetRequiredService<NewsletterPreferences>().Set(Actor(third), false));
        await page.GetByLabel("I reviewed the outgoing messages and confirm sending to 3 recipients.").CheckAsync();
        await send.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("audience or fallbacks changed");
        Assert.Equal(1, await db.Deliveries.CountAsync(x => x.CampaignId == campaign));
        await Run(commands, sp => sp.GetRequiredService<NewsletterPreferences>().Set(Actor(third), true));
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh audience" }).ClickAsync();
        await page.GetByLabel("Fallback for Suomi").SelectOptionAsync("en");
        await page.GetByLabel("I reviewed the outgoing messages and confirm sending to 3 recipients.").CheckAsync();
        await send.ClickAsync();
        var historyUrl = root + $"admin/communications/history/{campaign}";
        await Assertions.Expect(page).ToHaveURLAsync(historyUrl);
        await Assertions.Expect(page.GetByText("Queued · Sent (0/3)", new() { Exact = true })).ToBeVisibleAsync();
        await page.GotoAsync(root + "admin/communications/drafts");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Workshop campaign", Exact = true })).ToHaveCountAsync(0);
        await page.GotoAsync(root + "admin/communications/history");
        await Assertions.Expect(page.GetByText("Queued · Sent (0/3)", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Workshop campaign", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(historyUrl);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Delivery progress", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Bring tools", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByText("View Markdown source", new() { Exact = true }).ClickAsync();
        var source = page.GetByLabel("Message body (Markdown)");
        await Assertions.Expect(source).ToHaveValueAsync("**Bring tools**");
        Assert.True(await source.EvaluateAsync<bool>("el => el.readOnly"));

        var rows = await db.Deliveries.AsNoTracking().Where(x => x.CampaignId == campaign && x.Kind != MailKind.CampaignTest).ToListAsync();
        Assert.Equal(3, rows.Count);
        foreach (var (userId, outcome) in new[] { (first, SendOutcome.Accepted), (second, SendOutcome.Rejected), (third, SendOutcome.Uncertain) })
        {
            transport.Outcome = outcome;
            await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(rows.Single(x => x.UserId == userId).Id));
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh deliveries" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Accepted by provider: 1", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Uncertain: 1", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Complete · Sent (1/3)", new() { Exact = true })).ToBeVisibleAsync();
        await page.GotoAsync(root + "admin/communications/history");
        await Assertions.Expect(page.GetByText("Complete · Sent (1/3)", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Workshop campaign", Exact = true }).ClickAsync();
        var retry = page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^Select delivery") });
        await Assertions.Expect(retry).ToHaveCountAsync(1);
        await retry.CheckAsync();
        await page.GetByLabel("Reason", new() { Exact = true }).FillAsync("Provider confirmed correction");
        await page.GetByLabel("I confirm retrying the selected deliveries (1).").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Queue selected retries" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Retries queued: 1", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Queued · Sent (1/3)", new() { Exact = true })).ToBeVisibleAsync();
        var states = await db.Deliveries.AsNoTracking().Where(x => x.CampaignId == campaign && x.UserId != null).ToDictionaryAsync(x => x.UserId!.Value, x => x.State);
        Assert.Equal(DeliveryState.Accepted, states[first]);
        Assert.Equal(DeliveryState.Pending, states[second]);
        Assert.Equal(DeliveryState.Review, states[third]);

        await page.GetByRole(AriaRole.Button, new() { Name = "Copy to new draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/communications/drafts/[0-9a-f-]+$"));
        await Assertions.Expect(page.GetByLabel("Draft title")).ToHaveValueAsync("Workshop campaign");
        await Assertions.Expect(page.Locator("#markdown-en-body")).ToHaveValueAsync("**Bring tools**");
        Assert.Equal(2, await db.MailCampaigns.CountAsync());
    }
}
