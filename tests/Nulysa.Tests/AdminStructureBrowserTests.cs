using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Billing;
using Nulysa.Components;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class AdminStructureBrowserTests : BoardTest
{
    [Fact]
    public async Task Admin_navigation_keeps_record_context_through_tabs_bills_actions_and_mobile_or_no_script_navigation()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var account = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var invoiceId = account.Invoices[0].Invoice.Id;
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 375, Height = 812 } });
        var page = await context.NewPageAsync();
        var root = Settings["Oidc:Issuer"]!;
        var errors = new List<string>();
        var circuits = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.Request += (_, request) => { if (request.Url.Contains("/_blazor")) circuits.Add(request.Url); };
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        var registryUrl = $"/admin/members?q={id}&state=active&sort=name&pageSize=50&page=1";
        await page.GotoAsync(root.TrimEnd('/') + registryUrl);
        await page.Locator(".registry-member h2 a").ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Account overview", Exact = true })).ToBeFocusedAsync();
        var tabs = page.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true });
        await Assertions.Expect(tabs.GetByRole(AriaRole.Link)).ToHaveCountAsync(4);
        await tabs.GetByRole(AriaRole.Link, new() { Name = "Details", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Details", Exact = true })).ToBeFocusedAsync();
        await page.GoBackAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Account overview", Exact = true })).ToBeVisibleAsync();
        await page.GoForwardAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Details", Exact = true })).ToBeVisibleAsync();
        var billingUrl = RegistryNavigation.MemberUrl(id, registryUrl, MemberSection.Billing) + "&billsPage=2&transactionsPage=2&balance=allocated";
        await page.GotoAsync(root.TrimEnd('/') + billingUrl);
        await page.GetByRole(AriaRole.Link, new() { Name = "View bill and payment instructions", Exact = true }).First.ClickAsync();
        var billUrl = page.Url;
        Assert.Contains($"admin/members/{id}/billing/invoices/{invoiceId}", billUrl);
        await Assertions.Expect(page.Locator(".workspace-label")).ToHaveTextAsync("Administration");
        await page.GetByRole(AriaRole.Link, new() { Name = "Cancel unfinished bill", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Bill with unfinished lines", new() { Exact = true })).ToHaveValueAsync(invoiceId.ToString());
        await page.GetByLabel("Reason", new() { Exact = true }).FillAsync("Review only");
        await page.GetByRole(AriaRole.Button, new() { Name = "Review adjustment", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Bill with unfinished lines", new() { Exact = true })).ToBeDisabledAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Cancel and return", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(billUrl);
        await page.GetByRole(AriaRole.Link, new() { Name = "Back to billing details", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(root.TrimEnd('/') + billingUrl);
        await tabs.GetByRole(AriaRole.Link, new() { Name = "Financial actions", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("main .section-navigation")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".task-list a")).ToHaveCountAsync(8);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Breadcrumb", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Member registry", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Search name, email or member ID")).ToHaveValueAsync(id.ToString());
        await Assertions.Expect(page.GetByLabel("Membership lifecycle")).ToHaveValueAsync("active");

        foreach (var theme in new[] { "default", "tampere-hacklab" })
        {
            foreach (var mode in new[] { "light", "dark" })
            {
                await page.GotoAsync(root + "Appearance");
                await page.GetByLabel("Theme", new() { Exact = true }).SelectOptionAsync(theme);
                await page.GetByLabel("Colour mode").SelectOptionAsync(mode);
                await page.GetByRole(AriaRole.Button, new() { Name = "Save appearance" }).ClickAsync();
                foreach (var width in new[] { 320, 375, 767, 768, 1280 })
                {
                    await page.SetViewportSizeAsync(width, 900);
                    foreach (var route in new[] { "admin/payments/import", $"admin/members/{id}/billing", "admin/settings/services/new" })
                    {
                        await page.GotoAsync(root + route);
                        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), $"Overflow at {width}px in {theme}/{mode}: {route}");
                        await Assertions.Expect(page.Locator("#portal-navigation nav[aria-label=\"Board administration\"] a.active")).ToHaveCountAsync(1);
                    }
                }
            }
        }
        Assert.Empty(errors);
        Assert.Empty(circuits);

        await using var plainContext = await browser.NewContextAsync(new() { JavaScriptEnabled = false, ViewportSize = new() { Width = 320, Height = 812 }, StorageState = await context.StorageStateAsync() });
        var plain = await plainContext.NewPageAsync();
        await plain.GotoAsync(root + "admin/payments/import");
        await plain.GetByRole(AriaRole.Navigation, new() { Name = "Finances pages", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Accounting and exports", Exact = true }).ClickAsync();
        await Assertions.Expect(plain.GetByRole(AriaRole.Heading, new() { Name = "Accounting and exports", Exact = true })).ToBeVisibleAsync();
        await plain.GetByRole(AriaRole.Navigation, new() { Name = "Board administration", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Member registry", Exact = true }).ClickAsync();
        await plain.GetByLabel("Search name, email or member ID").FillAsync(id.ToString());
        await plain.GetByRole(AriaRole.Button, new() { Name = "Search members", Exact = true }).ClickAsync();
        await plain.Locator(".registry-member h2 a").ClickAsync();
        await plain.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Details", Exact = true }).ClickAsync();
        await Assertions.Expect(plain.GetByRole(AriaRole.Button, new() { Name = "Save details", Exact = true })).ToBeVisibleAsync();
    }
}
