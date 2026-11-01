using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Components;
using Nulysa.Integrations;
using Nulysa.Membership;

namespace Nulysa.Tests;

public abstract class BoardTest : MembershipTest
{
    protected async Task<List<long>> RegistryMembers(int count)
    {
        await using var db = Context();
        var users = Enumerable.Range(1, count).Select(number => new User
        {
            Name = "Jäsenrekisterin pitkä suomalainen esimerkkijäsen",
            Email = $"registry-{number}@example.test",
            NormalizedEmail = $"REGISTRY-{number}@EXAMPLE.TEST",
            UserName = $"registry-{number}",
            NormalizedUserName = $"REGISTRY-{number}",
            EmailConfirmed = true,
            Admitted = true,
            DateOfBirth = new(1990, 1, 1),
            Municipality = "Tampere"
        }).ToList();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        db.Members.AddRange(users.Select(user => new Member { UserId = user.Id, JoinedOn = new(2026, 1, 1) }));
        await db.SaveChangesAsync();
        return users.Select(user => user.Id).ToList();
    }

    protected Task<RegistryPage> Registry(WebApplication app, ClaimsPrincipal actor, string? search = null, string state = "all",
        int days = 0, string sort = "attention", int page = 1, int size = 25)
        => Run(app, services => services.GetRequiredService<MembershipQueries>().Registry(actor, search, state, days, sort, page, size));

    protected static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>").Groups[1].Value);
}

public sealed class BoardUiTests : BoardTest
{
    [Fact]
    public async Task Registry_reuses_billing_standing_through_grace_cancellation_inactivity_and_deletion()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        async Task<BillingView> CheckStanding()
        {
            var view = (await Run(app, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
            var member = Assert.Single((await Registry(app, board, id.ToString())).Entries);
            Assert.Equal(view.Membership, member.Standing);
            return view;
        }
        var initial = await CheckStanding();
        Assert.True(initial.Membership.IsRenewalGrace);
        await Run(app, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "membership", 10, "EUR", new(2026, 6, 1), initial.Invoices[0].Reference)));
        var paid = await CheckStanding();
        Assert.True(paid.Membership.Active);
        Assert.False(paid.Membership.IsRenewalGrace);

        Clock.Now = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
        var fallback = await CheckStanding();
        Assert.True(fallback.Membership.IsRenewalGrace);
        await Run(app, services => services.GetRequiredService<BillingCommands>().Scan(initial.AccountId));
        var renewal = await CheckStanding();
        var open = Assert.Single(renewal.Invoices, invoice => invoice.Invoice.ClosedAt is null);
        await Run(app, services => services.GetRequiredService<BillingCommands>().CancelInvoice(board, initial.AccountId, open.Invoice.Id, Guid.NewGuid(), "Reviewed cancellation"));
        var cancelled = await CheckStanding();
        Assert.False(cancelled.Membership.Active);

        Clock.Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        await Run(app, services => services.GetRequiredService<BillingCommands>().Scan(initial.AccountId));
        var inactive = await CheckStanding();
        Assert.False(inactive.Membership.Active);
        Assert.NotNull(inactive.Membership.InactiveSince);
        var longInactive = await Registry(app, board, state: "inactive", days: 60);
        Assert.Equal(id, Assert.Single(longInactive.Entries).Id);
        Assert.Empty((await Registry(app, board, state: "inactive", days: 120)).Entries);
        await Run(app, services => services.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Reviewed termination"));
        await CheckStanding();

        Assert.Empty((await Registry(app, board, state: "inactive")).Entries);
        Assert.Equal(id, Assert.Single((await Registry(app, board, state: "pending-deletion")).Entries).Id);
        await using var db = Context();
        Assert.Equal(10, await db.MoneyMovements.Where(movement => movement.Kind == MovementKind.LinePurchase).SumAsync(movement => movement.Amount));
        Assert.Equal(ReceiptState.Posted, (await db.Receipts.SingleAsync()).State);
    }

    [Fact]
    public async Task Registry_pagination_has_stable_ties_search_and_safe_page_bounds_without_truncating_at_100()
    {
        var board = await Board();
        var ids = await RegistryMembers(103);
        await using var app = CommandsApp();

        var first = await Registry(app, board, sort: "name");
        var last = await Registry(app, board, sort: "name", page: int.MaxValue);
        var all = await Registry(app, board, sort: "name", size: 100);
        var invalid = await Registry(app, board, page: int.MinValue, size: int.MaxValue);
        var search = await Registry(app, board, "REGISTRY-103@EXAMPLE.TEST");

        Assert.Equal(103, first.Total);
        Assert.Equal(5, first.PageCount);
        Assert.Equal(ids.Take(25), first.Entries.Select(member => member.Id));
        Assert.Equal(5, last.Page);
        Assert.Equal(ids.Skip(100), last.Entries.Select(member => member.Id));
        Assert.Equal(100, all.Entries.Count);
        Assert.Equal(1, invalid.Page);
        Assert.Equal(25, invalid.PageSize);
        Assert.Equal(ids[^1], Assert.Single(search.Entries).Id);
        Assert.Empty((await Registry(app, board, state: "inactive", days: 1)).Entries);
        Assert.Equal(103, (await Registry(app, board, state: "inactive")).Total);
        Assert.Empty((await Registry(app, board, "no-such-member")).Entries);
    }

    [Fact]
    public async Task Registry_active_filter_uses_membership_coverage_when_a_combined_bill_has_unpaid_services()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = (await Run(app, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var service = await Run(app, services => services.GetRequiredService<BillingCommands>().ConfigureService(board, null,
            new("door", "Door access", true, 0, 7, 10, "space")));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(fee => fee.ServiceDefinitionId == service);
        await Run(app, services => services.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2000, 1, 1), 20));
        await Run(app, services => services.GetRequiredService<BillingCommands>().Subscribe(board, initial.AccountId, service, 1, false, Guid.NewGuid()));
        var receipt = await Run(app, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "membership-only", 10, "EUR",
            DateOnly.FromDateTime(Clock.Now.DateTime), initial.Invoices[0].Reference)));

        var view = (await Run(app, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var active = await Registry(app, board, state: "active");

        Assert.Equal(id, Assert.Single(active.Entries).Id);
        Assert.Equal(view.Membership, active.Entries[0].Standing);
        Assert.True(view.Membership.Active);
        Assert.False(view.Membership.IsRenewalGrace);
        Assert.True(Assert.Single(view.Invoices).Remaining > 0);
        Assert.Empty((await Registry(app, board, state: "inactive")).Entries);
        Assert.Equal(ReceiptState.Posted, (await db.Receipts.SingleAsync(received => received.Id == receipt)).State);
        Assert.Equal(10, await db.MoneyMovements.Where(movement => movement.Kind == MovementKind.LinePurchase).SumAsync(movement => movement.Amount));
    }

    [Fact]
    public async Task Mail_attention_pages_are_bounded_and_keep_scope_state_and_reconciliation_references()
    {
        var board = await Board();
        await using var app = CommandsApp();
        await using var db = Context();
        for (var index = 0; index < 30; index++)
            db.Deliveries.Add(new()
            {
                Id = Guid.NewGuid(),
                Kind = MailKind.Welcome,
                State = index % 2 == 0 ? DeliveryState.Failed : DeliveryState.Review,
                CreatedAt = Clock.Now.AddMinutes(index),
                Attempts = 1,
                ProviderId = $"synthetic-provider-{index}"
            });
        db.Deliveries.Add(new() { Id = Guid.NewGuid(), Kind = MailKind.Invoice, State = DeliveryState.Failed, CreatedAt = Clock.Now });
        db.Deliveries.Add(new() { Id = Guid.NewGuid(), Kind = MailKind.Welcome, State = DeliveryState.Accepted, CreatedAt = Clock.Now, AcceptedAt = Clock.Now });
        await db.SaveChangesAsync();

        var first = await Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(board));
        var last = await Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(board, page: int.MaxValue));
        var uncertain = await Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(board, state: "review"));
        var all = await Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(board, essentialOnly: false));

        Assert.Equal(30, first.Total);
        Assert.Equal(25, first.Entries.Count);
        Assert.Equal(2, last.Page);
        Assert.Equal(5, last.Entries.Count);
        Assert.Empty(first.Entries.Select(entry => entry.Id).Intersect(last.Entries.Select(entry => entry.Id)));
        Assert.Equal("synthetic-provider-25", last.Entries[0].ProviderId);
        Assert.Equal(15, uncertain.Total);
        Assert.All(uncertain.Entries, entry => Assert.Equal(DeliveryState.Review, entry.State));
        Assert.Equal(31, all.Total);
        await using var web = await StartProvider();
        using var administrator = Client();
        await Login(administrator, "board@example.test");
        var html = WebUtility.HtmlDecode(await administrator.GetStringAsync("admin/mail-attention?state=review&scope=all"));

        Assert.Contains("Uncertain — reconcile with provider", html);
        Assert.Contains("synthetic-provider-1", html);
        Assert.DoesNotContain("synthetic-provider-0<", html);
        Assert.DoesNotContain("<form method=\"post\"", html.Split("<main")[1].Split("</main>")[0]);
    }

    [Fact]
    public async Task Attention_counts_actionable_queues_and_shows_missing_stale_and_failed_bank_feeds()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        var accounts = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString()).ToArray();
        Settings["Bank:Enabled"] = "true";
        Settings["Bank:SecretId"] = "synthetic";
        Settings["Bank:SecretKey"] = "synthetic";
        for (var index = 0; index < accounts.Length; index++)
            Settings[$"Bank:Accounts:{index}"] = accounts[index];
        await using var app = CommandsApp();
        var inactive = await Admit(app, board);
        var pending = await Admit(app, board, "pending@example.test");
        await Run(app, services => services.GetRequiredService<MembershipCommands>().BeginDeletion(board, pending, Guid.NewGuid(), "Reviewed termination"));
        await Verified(app, "verified@example.test");
        await Run(app, services => services.GetRequiredService<MembershipCommands>().Apply(Guid.NewGuid(), Input("unverified@example.test")));
        Clock.Now = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        await using var db = Context();
        db.Receipts.Add(new() { Source = "test", SourceAccount = "bank", ExternalId = "review", Amount = 2, ReceivedOn = new(2026, 10, 1), ImportedAt = Clock.Now, State = ReceiptState.Review });
        db.Receipts.Add(new() { Source = "test", SourceAccount = "bank", ExternalId = "ignored", Amount = 2, ReceivedOn = new(2026, 10, 1), ImportedAt = Clock.Now, State = ReceiptState.Ignored });
        db.Deliveries.Add(new() { Id = Guid.NewGuid(), Kind = MailKind.Welcome, State = DeliveryState.Failed, CreatedAt = Clock.Now });
        db.Deliveries.Add(new() { Id = Guid.NewGuid(), Kind = MailKind.DeletionWarning, State = DeliveryState.Review, CreatedAt = Clock.Now });
        db.Deliveries.Add(new() { Id = Guid.NewGuid(), Kind = MailKind.Invoice, State = DeliveryState.Failed, CreatedAt = Clock.Now });
        db.BankSyncStates.Add(new() { Account = accounts[0], LastSuccess = Clock.Now.AddHours(-27), LastAttempt = Clock.Now.AddHours(-27), Status = "Current" });
        db.BankSyncStates.Add(new() { Account = accounts[1], LastSuccess = Clock.Now.AddHours(-2), LastAttempt = Clock.Now.AddMinutes(-10), Status = "Authorization renewal required" });
        db.BankSyncStates.Add(new() { Account = accounts[2], LastSuccess = Clock.Now.AddHours(-2), LastAttempt = Clock.Now.AddHours(-2), Status = "Current" });
        await db.SaveChangesAsync();
        var operationsBefore = await db.LifecycleActions.CountAsync();

        var attention = await Run(app, services => services.GetRequiredService<BoardQueries>().Attention(board, 90));

        Assert.Equal(1, attention.Applications);
        Assert.Equal(1, attention.PaymentReviews);
        Assert.Equal(1, attention.InactiveMembers);
        Assert.Equal(1, attention.PendingDeletions);
        Assert.Equal(2, attention.EssentialMailProblems);
        Assert.True(attention.BankEnabled);
        Assert.True(attention.BankAccounts.Single(account => account.Account == accounts[0]).Stale);
        Assert.True(attention.BankAccounts.Single(account => account.Account == accounts[1]).NeedsAttention);
        Assert.False(attention.BankAccounts.Single(account => account.Account == accounts[2]).NeedsAttention);
        Assert.Equal("Never synchronized", attention.BankAccounts.Single(account => account.Account == accounts[3]).Status);
        Assert.Equal(operationsBefore, await db.LifecycleActions.CountAsync());
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(user => user.Id == inactive)).PendingDeletionAt);
        Assert.Equal(1, (await Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(board, state: "review"))).Total);
        Assert.Equal(3, (await Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(board, essentialOnly: false))).Total);
    }

    [Fact]
    public async Task Board_queries_recheck_live_authority_and_do_not_grant_directory_access_to_members_or_sysops()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        await using var db = Context();
        var superuser = await CreateUserForSysop();
        foreach (var actor in new[] { Actor(id), superuser, new ClaimsPrincipal() })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, services => services.GetRequiredService<BoardQueries>().Attention(actor, 90)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, services => services.GetRequiredService<MembershipQueries>().MailFailures(actor)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, services => services.GetRequiredService<MembershipQueries>().Subject(actor, id)));
        }
        await db.UserRoles.Where(assignment => assignment.UserId == Authority.Id(board)).ExecuteDeleteAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Registry(app, board));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, services => services.GetRequiredService<BoardQueries>().Attention(board, 90)));
    }

    private async Task<ClaimsPrincipal> CreateUserForSysop()
    {
        await using var scope = Maintenance.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var user = new User { Email = "sysop@example.test", UserName = "sysop@example.test", EmailConfirmed = true, Admitted = true };
        MembershipCommands.Check(await users.CreateAsync(user, Password));
        await Superusers.Set(scope.ServiceProvider.GetRequiredService<Nulysa.Data.AppDbContext>(), users,
            scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole<long>>>(), user.Id, true);
        return Actor(user.Id);
    }

    [Fact]
    public async Task Member_workspace_preserves_filtered_return_and_validates_lifecycle_forms_before_redirecting()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        await using var web = await StartProvider();
        using var administrator = Client();
        await Login(administrator, "board@example.test");
        var listUrl = $"/admin/members?q={id}&state=active&sort=name&pageSize=50&page=1&inactiveDays=0";
        var list = WebUtility.HtmlDecode(await administrator.GetStringAsync(listUrl));
        var memberUrl = Regex.Match(list, "href=\"(/admin/members/" + id + "\\?[^\"]*)\"").Groups[1].Value;
        Assert.NotEmpty(memberUrl);
        var details = WebUtility.HtmlDecode(await administrator.GetStringAsync(memberUrl));
        var billingUrl = Regex.Match(details, "href=\"(/admin/members/" + id + "/billing[^\"]*)\"").Groups[1].Value;
        var billing = WebUtility.HtmlDecode(await administrator.GetStringAsync(billingUrl));

        Assert.Contains($"Member #{id}", details);
        Assert.Contains("You are administering this member's account.", details);
        Assert.Contains("You are administering this member's account.", billing);
        Assert.Contains("href=\"/admin/members?q=" + id, billing);
        Assert.Contains("#lifecycle", details);
        Assert.Contains("#history", details);
        Assert.Equal(HttpStatusCode.NotFound, (await administrator.GetAsync("admin/members/" + long.MaxValue)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await administrator.GetAsync($"admin/members/{long.MaxValue}/billing")).StatusCode);
        var input = new Dictionary<string, string>
        {
            ["_handler"] = "lifecycle",
            ["__RequestVerificationToken"] = Token(details),
            ["Lifecycle.Action"] = "terminate",
            ["Lifecycle.Operation"] = Guid.NewGuid().ToString(),
            ["Lifecycle.Reason"] = "",
            ["Lifecycle.Confirmed"] = "false"
        };
        var invalid = await administrator.PostAsync(memberUrl, new FormUrlEncodedContent(input));

        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("Confirm the membership action.", await invalid.Content.ReadAsStringAsync());
        await using var db = Context();
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PendingDeletionAt);
        input["Lifecycle.Confirmed"] = "true";
        var saved = await administrator.PostAsync(memberUrl, new FormUrlEncodedContent(input));

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal("/admin/members", saved.Headers.Location!.AbsolutePath);
        Assert.Contains("state=active", saved.Headers.Location.Query);
        Assert.Contains("sort=name", saved.Headers.Location.Query);
        Assert.Contains("pageSize=50", saved.Headers.Location.Query);
        var returned = await administrator.GetStringAsync(saved.Headers.Location);
        Assert.Contains("Membership terminated and account deletion started", returned);
        Assert.Contains("No members match these filters", returned);
        Assert.NotNull((await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PendingDeletionAt);
        Assert.Equal("Administrator confirmed account removal", Assert.Single(await db.LifecycleActions.ToListAsync()).Reason);
        var forged = await administrator.GetStringAsync($"admin/members/{id}?returnUrl=https%3A%2F%2Fexample.test");
        Assert.DoesNotContain("href=\"https://example.test", forged);
        Assert.Contains("href=\"/admin/members\"", forged);
    }

    [Fact]
    public async Task New_board_routes_are_private_and_empty_or_invalid_filters_have_clear_states()
    {
        await Board();
        var member = await CreateUser();
        await using var web = await StartProvider();
        using var client = Client();
        Assert.Contains("/Account/Login", (await client.GetAsync("admin")).Headers.Location!.ToString());
        await Login(client);
        foreach (var route in new[] { "admin", "admin/mail-attention", "admin/members" })
            Assert.Equal("/Account/AccessDenied", (await client.GetAsync(route)).Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"admin/members/{member.Id}/billing")).StatusCode);
        using var administrator = Client();
        await Login(administrator, "board@example.test");

        var dashboard = await administrator.GetStringAsync("admin?inactiveDays=invalid");
        var registry = await administrator.GetStringAsync("admin/members?page=invalid&pageSize=-1&inactiveDays=-1&state=unknown");
        var mail = await administrator.GetStringAsync("admin/mail-attention?page=999999999999999&state=unknown");

        Assert.Contains("<h1>Needs attention</h1>", dashboard);
        Assert.Contains("Automatic bank import is disabled", dashboard);
        Assert.Contains("No successful bank synchronization has been recorded", dashboard);
        Assert.Contains("Inactive for at least 30 days", dashboard);
        Assert.Contains("inactiveDays=30", dashboard);
        var defaultDashboard = await administrator.GetStringAsync("admin");
        Assert.Contains("Inactive for at least 30 days", defaultDashboard);
        Assert.Contains("inactiveDays=30", defaultDashboard);
        var customDashboard = await administrator.GetStringAsync("admin?inactiveDays=60");
        Assert.Contains("Inactive for at least 60 days", customDashboard);
        Assert.Contains("inactiveDays=60", customDashboard);
        Assert.Contains("No members match these filters", registry);
        Assert.DoesNotContain("Essential mail needing attention", registry);
        Assert.Contains("No known mail problems match these filters", mail);
        Assert.DoesNotContain("<button>Retry", mail);
        Assert.True((await administrator.GetAsync("admin")).Headers.CacheControl?.NoStore);
    }

}

public sealed class RegistryNavigationTests
{
    [Theory]
    [InlineData("https://example.test")]
    [InlineData("//example.test")]
    [InlineData("/admin/members/12")]
    [InlineData("/admin/members?x=\\evil")]
    [InlineData("/admin/members?x=\r\nLocation:evil")]
    public void Registry_return_navigation_accepts_only_local_registry_list_urls(string value)
    {
        Assert.Equal("/admin/members", RegistryNavigation.ReturnUrl(value));
    }
}
