using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Membership;

namespace Nulysa.Tests;

public sealed class MemberPortalTests : MembershipTest
{
    [Fact]
    public async Task Newest_first_statement_pages_preserve_chronological_running_balances()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var billing = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        for (var amount = 1; amount <= 3; amount++)
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "association", $"statement-{amount}", amount, "EUR",
                DateOnly.FromDateTime(Clock.Now.DateTime), billing.DepositReference)));

        var newest = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(Actor(id), id, take: 2, newestFirst: true)))!.Statement(BalanceKind.PurchaseBalance);
        Assert.Equal(new decimal[] { 3, 2 }, newest.Lines.Select(line => line.Amount));
        Assert.Equal(new decimal[] { 6, 3 }, newest.Lines.Select(line => line.RunningBalance));
        Assert.Equal(1, newest.OpeningBalance);
        Assert.Equal(6, newest.ClosingBalance);
        Assert.True(newest.HasMore);

        var older = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(Actor(id), id, skip: 2, take: 2, newestFirst: true)))!.Statement(BalanceKind.PurchaseBalance);
        Assert.Equal(1, Assert.Single(older.Lines).RunningBalance);
        Assert.False(older.HasMore);
        Assert.Equal(0, older.OpeningBalance);
        Assert.Equal(1, older.ClosingBalance);
    }

    [Fact]
    public async Task Overview_matches_membership_standing_and_only_lists_own_live_requests()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");
        var billing = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var overview = await Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().Overview(Actor(id)));

        Assert.Equal(billing.Membership, overview.Membership);
        Assert.Equal(billing.Invoices[0].Invoice.Id, Assert.Single(overview.Bills).Id);
        Assert.False(overview.HasMoreBills);
        Assert.Null(overview.PendingEmail);

        await Run(app, sp => sp.GetRequiredService<AccountCommands>().RequestEmailChange(Actor(id), id, "new@example.test", Guid.NewGuid()));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "association", "overview-payment", 10, "EUR",
            DateOnly.FromDateTime(Clock.Now.DateTime), billing.Invoices[0].Reference)));
        overview = await Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().Overview(Actor(id)));
        billing = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;

        Assert.Equal(billing.Membership, overview.Membership);
        Assert.True(overview.Membership!.Active);
        Assert.False(overview.Membership.IsRenewalGrace);
        Assert.Empty(overview.Bills);
        Assert.Equal("new@example.test", overview.PendingEmail);
        Assert.Single((await Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().Overview(Actor(other)))).Bills);

        Clock.Now = Clock.Now.AddMonths(2);
        overview = await Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().Overview(Actor(id)));
        billing = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        Assert.Equal(billing.Membership, overview.Membership);
        Assert.False(overview.Membership!.Active);
    }

    [Fact]
    public async Task Portal_preferences_require_ownership_and_pending_deletion_blocks_overview()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");

        Assert.True((await Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().EmailSettings(Actor(id), id)))!.Enabled);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().EmailSettings(Actor(other), id)));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Test termination"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<MemberPortalQueries>().Overview(Actor(id))));
    }
}
