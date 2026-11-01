using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Membership;

namespace Nulysa.Tests;

public sealed class UiRefinementTests : BoardTest
{
    [Fact]
    public async Task Inline_application_decisions_require_confirmation_and_target_only_the_selected_verified_application()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var first = await Verified(commands);
        var second = await Verified(commands, "reject@example.test");
        await Run(commands, services => services.GetRequiredService<MembershipCommands>().Apply(Guid.NewGuid(), Input("unverified@example.test")));
        await using var web = await StartProvider();
        using var administrator = Client();
        await Login(administrator, "board@example.test");
        var html = await administrator.GetStringAsync("admin/applications");
        Assert.Contains("Helsinki", html);
        Assert.Contains("I want to join.", html);
        Assert.Contains("Born", html);
        Assert.DoesNotContain("unverified@example.test", html);
        var input = new Dictionary<string, string>
        {
            ["_handler"] = $"approve-{first}",
            ["__RequestVerificationToken"] = Token(html),
            ["Input.Confirmed"] = "false"
        };
        var invalid = await administrator.PostAsync("admin/applications", new FormUrlEncodedContent(input));
        await using var db = Context();
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Confirm the admission decision", await invalid.Content.ReadAsStringAsync());
        Assert.Empty(await db.Members.ToListAsync());
        input["Input.Confirmed"] = "true";

        var approved = await administrator.PostAsync("admin/applications", new FormUrlEncodedContent(input));

        Assert.Equal(HttpStatusCode.Redirect, approved.StatusCode);
        Assert.Single(await db.Members.ToListAsync());
        Assert.Equal(first, (await db.Approvals.SingleAsync()).Id);
        Assert.Null((await db.Applications.SingleAsync(application => application.Id == second)).RejectedAt);
        input["_handler"] = $"reject-{second}";
        input["Input.Explanation"] = "Please contact the board.";
        input["Input.Rejecting"] = "true";
        var rejected = await administrator.PostAsync("admin/applications", new FormUrlEncodedContent(input));

        Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
        var application = await db.Applications.AsNoTracking().SingleAsync(application => application.Id == second);
        Assert.NotNull(application.RejectedAt);
        Assert.Equal("Please contact the board.", application.RejectionExplanation);
        Assert.Single(await db.Members.ToListAsync());
    }

    [Fact]
    public async Task Members_and_administrators_see_applicable_discounts_and_compact_payment_preferences()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var view = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, services => services.GetRequiredService<BillingCommands>().AddDiscount(board, view.AccountId, 1,
            new(2026, 6, 1), new(2026, 7, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Reviewed concession"));
        var token = await PasswordToken(commands, id);
        await Run(commands, services => services.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        await using var web = await StartProvider();
        using var member = Client();
        await Login(member);
        using var administrator = Client();
        await Login(administrator, "board@example.test");

        foreach (var (client, route) in new[] { (member, "services"), (administrator, $"admin/members/{id}/billing") })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(route));
            Assert.Contains("50% discount", html);
            Assert.Contains($"Applied to outstanding bill #{view.Invoices[0].Invoice.Id}", html);
            Assert.Contains("30 Jun 2026", html);
            Assert.Contains("Email invoice PDFs", html);
            Assert.Contains("class=\"preference-form\"", html);
        }
        var billing = WebUtility.HtmlDecode(await member.GetStringAsync("billing"));
        Assert.Contains("50% discount", billing);
        Assert.DoesNotContain("Email invoice PDFs", billing);
        var profile = WebUtility.HtmlDecode(await member.GetStringAsync("profile"));
        Assert.Contains($"Current email: <strong>{Email}</strong>", profile);
        Assert.DoesNotContain("SSO", profile);
        await using var db = Context();
        Assert.Equal(5, (await db.InvoiceLines.SingleAsync()).Amount);
        Assert.Empty(await db.MoneyMovements.ToListAsync());
    }

    [Fact]
    public async Task Payment_review_reports_specific_source_problems_without_allocating_funds()
    {
        await using var commands = CommandsApp();
        await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "outgoing", -12, "EUR", new(2026, 6, 1), "unknown")));
        await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "currency", 12, "USD", new(2026, 6, 1), "unknown")));
        await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "reference", 12, "EUR", new(2026, 6, 1), "unknown")));

        await using var db = Context();
        var receipts = await db.Receipts.ToDictionaryAsync(receipt => receipt.ExternalId);
        Assert.StartsWith("Outgoing bank transaction", receipts["outgoing"].ReviewReason);
        Assert.StartsWith("Unsupported currency: USD", receipts["currency"].ReviewReason);
        Assert.StartsWith("Unknown payment reference", receipts["reference"].ReviewReason);
        Assert.All(receipts.Values, receipt => Assert.Equal(ReceiptState.Review, receipt.State));
        Assert.Equal(-12, receipts["outgoing"].Amount);
        Assert.Empty(await db.MoneyMovements.ToListAsync());
    }
    [Fact]
    public async Task Account_overview_and_billing_details_share_navigation_and_keep_admin_controls_in_administration()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        await using var db = Context();
        db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<long>
        {
            UserId = id,
            RoleId = await db.Roles.Where(role => role.NormalizedName == "ADMINISTRATOR").Select(role => role.Id).SingleAsync()
        });
        await db.SaveChangesAsync();
        var service = await Run(commands, services => services.GetRequiredService<BillingCommands>().ConfigureService(board, null,
            new("door", "Door access", true, 21, 7, 10, "space")));
        var fee = await db.Fees.SingleAsync(fee => fee.ServiceDefinitionId == service);
        await Run(commands, services => services.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2000, 1, 1), 20));
        var token = await PasswordToken(commands, id);
        await Run(commands, services => services.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        await using var web = await StartProvider();
        using var administrator = Client();
        await Login(administrator);

        foreach (var route in new[] { "services" })
        {
            var html = WebUtility.HtmlDecode(await administrator.GetStringAsync(route));
            Assert.Contains("<h1>Account overview</h1>", html);
            Assert.Contains("aria-label=\"Membership and payments\"", html);
            Assert.Contains("id=\"funds\"", html);
            Assert.True(html.IndexOf("id=\"attention-heading\"", StringComparison.Ordinal) < html.IndexOf("Membership:", StringComparison.Ordinal));
            Assert.Contains("payment-panel payment-attention", html);
            Assert.DoesNotContain("name=\"Action.Grant\"", html);
            Assert.DoesNotContain("id=\"transactions\"", html);
            var settings = html.Split("id=\"billing-settings\"")[1].Split("</section>")[0];
            Assert.Contains("Email invoice PDFs", settings);
            Assert.Contains("Billing intervals", settings);
        }
        var front = await administrator.GetStringAsync("/");
        Assert.Contains("<h1>Member portal</h1>", front);
        Assert.DoesNotContain("id=\"funds\"", front);
        Assert.True(front.IndexOf("attention-heading", StringComparison.Ordinal) < front.IndexOf("standing-heading", StringComparison.Ordinal));
        var billing = WebUtility.HtmlDecode(await administrator.GetStringAsync("billing"));
        Assert.Contains("<h1>Billing details</h1>", billing);
        Assert.Contains("Account overview", billing);
        Assert.Contains("id=\"outstanding-bills\"", billing);
        Assert.Contains("id=\"transactions\"", billing);
        Assert.DoesNotContain("id=\"funds\"", billing);
        var admin = await administrator.GetStringAsync($"admin/members/{id}/billing");
        Assert.Contains("name=\"Action.Grant\"", admin);
        var overview = await administrator.GetStringAsync("services");
        var forged = await administrator.PostAsync("services", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = $"subscribe-{service}",
            ["__RequestVerificationToken"] = Token(overview),
            ["Action.Key"] = Guid.NewGuid().ToString(),
            ["Action.Id"] = service.ToString(),
            ["Action.Name"] = "subscribe",
            ["Action.Months"] = "1",
            ["Action.Grant"] = "true"
        }));
        Assert.Contains("Free-day grants must be made in the administration view", await forged.Content.ReadAsStringAsync());
        Assert.Empty(await db.ServiceEnrollments.ToListAsync());
        var profile = await administrator.GetStringAsync($"admin/members/{id}");
        Assert.Contains("dialog-trigger danger-action", profile);
        Assert.Contains("Account removal and restoration", profile);
        Assert.DoesNotContain("Membership lifecycle", profile);
        Assert.DoesNotContain("Appearance settings", await administrator.GetStringAsync("profile"));
    }

}
