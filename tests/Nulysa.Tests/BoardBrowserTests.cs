using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class BoardBrowserTests : BoardTest
{
    [Fact]
    public async Task Board_dashboard_registry_return_and_lifecycle_actions_work_on_mobile_in_all_themes_without_a_circuit()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        await RegistryMembers(27);
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 375, Height = 812 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        var circuitRequests = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        page.Request += (_, request) => { if (request.Url.Contains("/_blazor")) circuitRequests.Add(request.Url); };
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Menu", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Administration", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs attention", Exact = true })).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByText("Automatic bank import is disabled.", new() { Exact = true })).ToBeVisibleAsync();

        foreach (var theme in new[] { "default", "tampere-hacklab" })
        {
            foreach (var mode in new[] { "light", "dark" })
            {
                await page.GotoAsync(root + "Appearance");
                await page.GetByLabel("Theme", new() { Exact = true }).SelectOptionAsync(theme);
                await page.GetByLabel("Colour mode").SelectOptionAsync(mode);
                await page.GetByRole(AriaRole.Button, new() { Name = "Save appearance" }).ClickAsync();
                foreach (var width in new[] { 320, 375, 768, 1280 })
                {
                    await page.SetViewportSizeAsync(width, 900);
                    foreach (var route in new[] { "admin", "admin/members?sort=name&page=2", "admin/mail-attention", $"admin/members/{id}" })
                    {
                        await page.GotoAsync(root + route);
                        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), $"Overflow at {width}px in {theme}/{mode}: {route}");
                    }
                }
            }
        }

        await page.SetViewportSizeAsync(375, 812);
        await page.GotoAsync(root + "admin/members?q=J%C3%A4sen&state=inactive&sort=name&page=2&pageSize=25");
        await Assertions.Expect(page.GetByText("Page 2 of 2", new() { Exact = true })).ToBeVisibleAsync();
        await page.Locator(".registry-member h2 a").First.ClickAsync();
        var memberUrl = page.Url;
        await Assertions.Expect(page.Locator(".member-subject")).ToContainTextAsync("Jäsenrekisterin pitkä suomalainen esimerkkijäsen");
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true })
            .GetByRole(AriaRole.Link, new() { Name = "Billing details" }).ClickAsync();
        await Assertions.Expect(page.Locator(".member-subject")).ToContainTextAsync("Jäsenrekisterin pitkä suomalainen esimerkkijäsen");
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true })
            .GetByRole(AriaRole.Link, new() { Name = "Account overview", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(memberUrl);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Breadcrumb", Exact = true })
            .GetByRole(AriaRole.Link, new() { Name = "Member registry", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Search name, email or member ID")).ToHaveValueAsync("Jäsen");
        await Assertions.Expect(page.GetByLabel("Membership lifecycle")).ToHaveValueAsync("inactive");
        await Assertions.Expect(page.GetByText("Page 2 of 2", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Previous members", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Page 1 of 2", new() { Exact = true })).ToBeVisibleAsync();

        await page.GotoAsync(root + $"admin/members?q={id}&state=active&sort=name&pageSize=50");
        await page.Locator(".registry-member h2 a").ClickAsync();
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Details", Exact = true }).ClickAsync();
        await page.GetByText("Remove account", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await using var db = Context();
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PendingDeletionAt);
        await page.GetByText("Remove account", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Terminate and begin deletion", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Membership terminated");
        await Assertions.Expect(page.GetByLabel("Membership lifecycle")).ToHaveValueAsync("active");
        await Assertions.Expect(page.GetByText("No members match these filters.", new() { Exact = false })).ToBeVisibleAsync();

        await page.GotoAsync(root + $"admin/members?q={id}&state=pending-deletion");
        await page.Locator(".registry-member h2 a").ClickAsync();
        await Assertions.Expect(page.Locator(".member-subject")).ToContainTextAsync($"Member #{id}");
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Member workspace", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Details", Exact = true }).ClickAsync();
        await page.GetByText("Restore account", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("Reason / verified restoration request").FillAsync("Verified restoration request");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel pending deletion", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Pending deletion cancelled");
        await Assertions.Expect(page.GetByLabel("Membership lifecycle")).ToHaveValueAsync("pending-deletion");
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PendingDeletionAt);
        Assert.Empty(errors);
        Assert.Empty(circuitRequests);

        await using var plainContext = await browser.NewContextAsync(new()
        {
            JavaScriptEnabled = false,
            ViewportSize = new() { Width = 320, Height = 812 },
            StorageState = await context.StorageStateAsync()
        });
        var plain = await plainContext.NewPageAsync();
        await plain.GotoAsync(root + "admin");
        await Assertions.Expect(plain.GetByRole(AriaRole.Navigation, new() { Name = "Board administration", Exact = true })).ToBeVisibleAsync();
        await plain.GetByRole(AriaRole.Link, new() { Name = "Member registry", Exact = true }).ClickAsync();
        await plain.GetByLabel("Search name, email or member ID").FillAsync(id.ToString());
        await plain.GetByRole(AriaRole.Button, new() { Name = "Search members", Exact = true }).ClickAsync();
        await plain.GetByRole(AriaRole.Link, new() { Name = $"New Member Member #{id}", Exact = true }).ClickAsync();
        await Assertions.Expect(plain.Locator(".member-subject")).ToContainTextAsync($"Member #{id}");
    }
}
