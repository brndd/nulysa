using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Components;

namespace Nulysa.Tests;

public sealed class AdminStructureTests : BoardTest
{
    private static string MainContent(string html) => Regex.Match(html, "<main[^>]*id=\"main-content\"[^>]*>(.*?)</main>", RegexOptions.Singleline).Groups[1].Value;

    [Fact]
    public async Task Admin_routes_share_one_header_selected_destination_and_the_correct_level_of_tabs()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var view = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var application = await Verified(commands, "applicant@example.test");
        var service = await Run(commands, services => services.GetRequiredService<BillingCommands>().ConfigureService(board, null, new("door", "Door", true, 14, 7, 100, "access")));
        var receipt = await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "review", 5, "EUR", DateOnly.FromDateTime(Clock.Now.DateTime), "unknown")));
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var routes = new (string Route, string Heading, string Selected, int Tabs)[]
        {
            ("/admin/members", "Member registry", "/admin/members", 1),
            ("/admin/applications", "Application review", "/admin/applications", 1),
            ($"/admin/applications/{application}", "Review application", "/admin/applications", 0),
            ($"/admin/members/{id}", "Account overview", "/admin/members", 1),
            ($"/admin/members/{id}/details", "Details", "/admin/members", 1),
            ($"/admin/members/{id}/billing", "Billing details", "/admin/members", 1),
            ($"/admin/members/{id}/billing/invoices/{view.Invoices[0].Invoice.Id}", $"Bill #{view.Invoices[0].Invoice.Id}", "/admin/members", 1),
            ($"/admin/members/{id}/actions", "Financial actions", "/admin/members", 1),
            ($"/admin/members/{id}/actions/discount", "Add discount", "/admin/members", 1),
            ($"/admin/members/{id}/actions/credit", "Apply discount to already paid time", "/admin/members", 1),
            ("/admin/payments", "Payment review", "/admin/payments", 1),
            ("/admin/payments/bank", "Bank connections", "/admin/payments/bank", 1),
            ("/admin/payments/import", "Manual import", "/admin/payments/import", 1),
            ("/ADMIN/PAYMENTS/IMPORT", "Manual import", "/admin/payments/import", 1),
            ("/admin/payments/accounting", "Accounting and exports", "/admin/payments/accounting", 1),
            ("/admin/payments/retained-balances", "Retained balances", "/admin/payments/retained-balances", 1),
            ($"/admin/payments/{receipt}", "Review bank transaction", "/admin/payments", 0),
            ("/admin/communications/drafts", "Campaign drafts", "/admin/communications/drafts", 1),
            ("/admin/communications/history", "Campaign history", "/admin/communications/history", 1),
            ("/admin/communications/templates", "Automatic mail templates", "/admin/communications/templates", 1),
            ("/admin/mail-attention", "Mail problems", "/admin/mail-attention", 1),
            ("/admin/settings/fees", "Fees and prices", "/admin/settings/fees", 1),
            ("/admin/settings/fees/1", "Membership prices", "/admin/settings/fees", 0),
            ("/admin/settings/services", "Services", "/admin/settings/services", 1),
            ("/admin/settings/services/new", "Create service", "/admin/settings/services", 0),
            ($"/admin/settings/services/{service}", "Edit service", "/admin/settings/services", 0)
        };

        foreach (var route in routes)
        {
            var response = await client.GetAsync(route.Route);
            var html = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, route.Route + ": " + html.Split("HEADERS")[0]);
            var main = MainContent(html);
            var sidebar = Regex.Match(html, "<nav aria-label=\"Board administration\">(.*?)</nav>", RegexOptions.Singleline).Value;

            Assert.Contains($"<h1>{route.Heading}</h1>", main);
            Assert.Single(Regex.Matches(main, "<h1>"));
            Assert.Single(Regex.Matches(main, "aria-label=\"Breadcrumb\""));
            Assert.True(main.IndexOf("aria-label=\"Breadcrumb\"", StringComparison.Ordinal) < main.IndexOf("<h1>", StringComparison.Ordinal));
            Assert.Contains("href=\"/admin\">Administration</a>", main);
            Assert.Equal(route.Tabs, Regex.Matches(main, "class=\"section-navigation\"").Count);
            Assert.Single(Regex.Matches(sidebar, "class=\"active\""));
            Assert.Contains($"href=\"{route.Selected}\" class=\"active\"", sidebar);
            Assert.Contains(route.Route.Equals(route.Selected, StringComparison.OrdinalIgnoreCase) ? "aria-current=\"page\"" : "aria-current=\"location\"", sidebar);
        }
        var overview = MainContent(await client.GetStringAsync($"admin/members/{id}"));
        Assert.Contains("Current balances", overview);
        Assert.Contains("Billing settings", overview);
        Assert.DoesNotContain("id=\"transactions\"", overview);
        var billing = MainContent(await client.GetStringAsync($"admin/members/{id}/billing"));
        Assert.Contains("Outstanding bills", billing);
        Assert.Contains("Financial command history", billing);
        Assert.DoesNotContain("id=\"funds\"", billing);
        Assert.DoesNotContain("href=\"/billing\"", billing);
        Assert.DoesNotContain("href=\"/services\"", billing);
    }

    [Fact]
    public async Task Admin_bill_routes_validate_member_context_and_all_record_routes_keep_live_authority_checks()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var other = await Admit(commands, board, "other@example.test");
        var bill = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, other)))!.Invoices[0];
        var token = await PasswordToken(commands, id);
        await Run(commands, services => services.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        await using var web = await StartProvider();
        using var administrator = Client();
        await Login(administrator, "board@example.test");

        var mismatch = await administrator.GetAsync($"admin/members/{id}/billing/invoices/{bill.Invoice.Id}");

        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
        Assert.DoesNotContain(bill.Reference, await mismatch.Content.ReadAsStringAsync());
        foreach (var route in new[] { $"admin/members/{id}/actions/unknown", $"admin/members/{long.MaxValue}/actions", "admin/settings/fees/999999", "admin/settings/services/999999" })
            Assert.Equal(HttpStatusCode.NotFound, (await administrator.GetAsync(route)).StatusCode);
        using var member = Client();
        await Login(member);
        foreach (var route in new[] { $"admin/members/{id}", $"admin/members/{id}/details", $"admin/members/{id}/billing", $"admin/members/{id}/billing/invoices/{bill.Invoice.Id}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(route)).StatusCode);
        foreach (var route in new[] { $"admin/members/{id}/actions", "admin/payments/import", "admin/settings/fees", "admin/settings/services/new",
            $"admin/communications/drafts/{Guid.NewGuid()}/delivery" })
            Assert.Equal("/Account/AccessDenied", (await member.GetAsync(route)).Headers.Location!.AbsolutePath);

        await using var db = Context();
        var administratorRole = await db.Roles.SingleAsync(role => role.NormalizedName == "ADMINISTRATOR");
        db.UserRoles.Add(new() { UserId = id, RoleId = administratorRole.Id });
        await db.SaveChangesAsync();
        await Login(member);
        var self = MainContent(await member.GetStringAsync($"admin/members/{id}/billing"));
        Assert.Contains($"/admin/members/{id}/billing/invoices/", self);
        Assert.DoesNotContain("href=\"/billing\"", self);
        db.UserRoles.Remove(await db.UserRoles.SingleAsync(role => role.UserId == id));
        await db.SaveChangesAsync();
        Assert.Equal("/Account/AccessDenied", (await member.GetAsync($"admin/members/{id}/actions")).Headers.Location!.AbsolutePath);
    }
}

public sealed class AdminReturnNavigationTests
{
    [Theory]
    [InlineData("https://example.test")]
    [InlineData("//example.test")]
    [InlineData("/admin/members/13/billing")]
    [InlineData("/admin/members/12/actions/refund")]
    [InlineData("/admin/members/12/billing/invoices/1/other")]
    [InlineData("/admin/members/12/billing?bad=%0d%0aLocation:evil")]
    public void Financial_action_return_links_are_limited_to_this_members_pages(string value)
    {
        Assert.Equal(RegistryNavigation.MemberUrl(12, null, MemberSection.Actions), RegistryNavigation.BackTo(12, value, null));
    }
}
