using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Billing;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class FinancialWorkflowBrowserTests : BoardTest
{
    [Fact]
    public async Task Mobile_review_searches_members_and_named_adjustments_only_write_after_confirmation()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var account = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var receipt = await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "unknown", 2, "EUR", DateOnly.FromDateTime(Clock.Now.DateTime), "unknown")));
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 375, Height = 812 } });
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.GotoAsync(root + $"admin/payments/{receipt}");
        await page.GetByLabel("Disposition / purpose").SelectOptionAsync("billing");
        await page.GetByLabel("Find member by name, email or member number").FillAsync(Email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Choose purpose / search" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = $"New Member · #{id} · {Email}", Exact = true }).ClickAsync();
        await page.GetByLabel("Reason", new() { Exact = true }).FillAsync("Verified bank facts and member");
        await page.GetByRole(AriaRole.Button, new() { Name = "Review disposition", Exact = true }).ClickAsync();
        await using var db = Context();
        Assert.Equal(ReceiptState.Review, (await db.Receipts.SingleAsync(row => row.Id == receipt)).State);
        await Assertions.Expect(page.GetByLabel("Reason", new() { Exact = true })).ToBeDisabledAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm disposition", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Transaction disposition saved");
        await page.GotoAsync(root + $"admin/members/{id}/actions/discount");
        await page.GetByLabel("Discount value", new() { Exact = true }).FillAsync("50");
        await page.GetByLabel("Reason", new() { Exact = true }).FillAsync("Reviewed concession");
        await page.GetByRole(AriaRole.Button, new() { Name = "Review adjustment", Exact = true }).ClickAsync();
        Assert.Empty(await db.Discounts.ToListAsync());
        await Assertions.Expect(page.GetByLabel("Discount value", new() { Exact = true })).ToBeDisabledAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm adjustment", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Financial action saved");
        Assert.Equal(50, (await db.Discounts.SingleAsync()).Value);
    }
}
