using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Tests;

public sealed partial class BillingTests
{
    [Fact]
    [Trait("Acceptance", "ERASE")]
    public async Task Pending_deletion_receipts_stay_in_review_after_restoration_and_erasure_preserves_the_unspent_claim()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 5);
        await Pay(app, view.DepositReference, 20);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Termination"));
        var receipt = await Pay(app, reference, 10, sourceId: "pending-deletion");
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, Guid.NewGuid(), "Verified request"));
        await Pay(app, reference, 10, sourceId: "pending-deletion");
        await using var db = Context();

        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == receipt)).State);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Second termination"));
        On(6, 15);

        Assert.True(await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(id)));
        var retained = await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == account);

        Assert.Null(retained.UserId);
        Assert.Equal("New Member", retained.ClaimName);
        Assert.Equal(Email, retained.ClaimEmail);
        var financial = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Account(board, account));
        var claims = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Claims(board));
        var claim = Assert.Single(claims);

        Assert.Equal(new Balances(5, 20, 0), financial.Balances);
        Assert.Equal(account, claim.AccountId);
        Assert.Equal(5m, claim.BillingCredit);
        Assert.Equal(20m, claim.PurchaseBalance);
        Assert.Empty(await db.RecurringItems.ToListAsync());
        Assert.Empty(await db.ServiceEnrollments.ToListAsync());
        var rejoined = await Admit(app, board);

        Assert.NotEqual(account, (await View(app, board, rejoined)).AccountId);
        Clock.Now = DateTimeOffset.UtcNow;
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var retainedPage = await client.GetStringAsync("admin/payments/retained-balances/" + account);

        Assert.Contains("Retained invoice evidence", retainedPage);
        Assert.DoesNotContain("/admin/members//billing", retainedPage);
        Assert.DoesNotContain("New Member", retainedPage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "ERASE")]
    public async Task Restoring_paid_membership_resumes_renewal_without_reopening_cancelled_purchases_or_services(bool renewalAlreadyIssued)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        await Pay(app, initial.Invoices[0].Reference, 10);
        var service = await Service(app, board, free: 30);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        if (renewalAlreadyIssued)
        {
            On(6, 20);
            await Scan(app, account);
        }
        else
        {
            On(6, 5);
        }
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Termination"));
        var cancelled = (await View(app, board, id)).Invoices.SelectMany(x => x.Lines).Where(x => x.Line.State == LineState.Cancelled).Select(x => x.Line.Id).ToList();
        On(6, 22);
        var restore = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, restore, "Verified restoration"));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, restore, "Verified restoration"));
        var restored = await View(app, board, id);

        Assert.True(restored.Membership.Active);
        Assert.Empty(restored.Subscriptions);
        Assert.Single(restored.Items);
        Assert.NotEqual(initial.Items.Single().Id, restored.Items[0].Id);
        Assert.DoesNotContain(restored.Invoices.SelectMany(x => x.Lines), x => cancelled.Contains(x.Line.Id) && x.Line.State != LineState.Cancelled);
        await Scan(app, account);
        var renewal = Assert.Single((await View(app, board, id)).Invoices.SelectMany(x => x.Lines), x => x.Line.Benefit == Benefit.Membership && x.Line.State == LineState.Open).Line;

        Assert.Equal(new DateOnly(2026, 7, 1), renewal.StartsOn);
        Assert.Equal(new DateOnly(2026, 8, 1), renewal.EndsOn);
        On(7, 1);

        Assert.True((await View(app, board, id)).Membership.Active);
        On(7, 9);
        await Scan(app, account);

        Assert.False((await View(app, board, id)).Membership.Active);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));

        Assert.Single((await View(app, board, id)).Items);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "ERASE")]
    public async Task Payment_after_expired_coverage_restoration_respects_automatic_return_setting(bool automaticReturn)
    {
        Settings["Billing:AutomaticMembershipReactivation"] = automaticReturn.ToString();
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        var reference = initial.Invoices[0].Reference;
        await Pay(app, reference, 10);
        On(6, 20);
        await Scan(app, account);
        On(6, 25);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Termination"));
        On(7, 9);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, Guid.NewGuid(), "Verified restoration"));
        await Scan(app, account);
        var restored = await View(app, board, id);

        Assert.False(restored.Membership.Active);
        Assert.Empty(restored.Items);
        Assert.Single(restored.Coverage);
        var cancelled = Assert.Single(restored.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Cancelled).Line.Id;
        var receipt = await Pay(app, reference, 10, sourceId: "restored-return");

        Assert.Equal(receipt, await Pay(app, reference, 10, sourceId: "restored-return"));
        var view = await View(app, board, id);

        Assert.Equal(automaticReturn, view.Membership.Active);
        if (!automaticReturn)
        {

            Assert.Empty(view.Items);
            Assert.Single(view.Coverage);
            Assert.Equal(10m, view.Balances.BillingCredit);
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(board, account, Guid.NewGuid()));
        }
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Single(view.Items);
        var returned = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Resumption).Line;

        Assert.Equal(LineState.Fulfilled, returned.State);
        Assert.Equal(7.42m, returned.Amount);
        Assert.Equal(new DateOnly(2026, 7, 9), returned.StartsOn);
        Assert.Equal(new DateOnly(2026, 8, 1), returned.EndsOn);
        Assert.Equal(new DateOnly(2026, 8, 1), view.Items.Single().NextUnissuedPeriodStartsOn);
        Assert.Equal(LineState.Cancelled, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == cancelled).Line.State);
        Assert.Equal(2.58m, view.Balances.BillingCredit);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task Renewal_grace_is_a_typed_state_not_a_display_string()
    {
        On(6, 1);
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        On(6, 20);
        await Scan(app, view.AccountId);
        On(7, 1);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.All(view.Invoices.SelectMany(x => x.Lines), x => Assert.Null(x.Line.GraceNotifiedAt));
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.True(view.Membership.IsMembershipGrace);
        Assert.NotNull(Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open).Line.GraceNotifiedAt);
        await using var db = Context();
        var delivery = await db.Deliveries.SingleAsync(x => x.UserId == id && x.Kind == MailKind.MembershipGrace);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.Single(transport.Sent);
        Assert.Contains("lisäaika", transport.Sent[0].Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Acceptance", "MAIL")]
    public async Task Expired_membership_sends_an_inactive_notice()
    {
        On(6, 1);
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        On(6, 16);
        await Scan(app, view.AccountId);
        await using var db = Context();
        var delivery = await db.Deliveries.SingleAsync(x => x.UserId == id && x.Kind == MailKind.MembershipInactive);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.Single(transport.Sent);
        Assert.Contains("keskeytetty", transport.Sent[0].Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unconsumed_S1_request_keeps_its_original_deadline_without_sending_an_expired_invoice()
    {
        On(6, 20);
        var user = await CreateUser();
        await using var app = CommandsApp();
        await using var db = Context();
        var approved = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        db.Members.Add(new() { UserId = user.Id, JoinedOn = new(2026, 6, 1) });
        var account = new BillingAccount { UserId = user.Id, CreatedAt = approved };
        db.BillingAccounts.Add(account);
        await db.SaveChangesAsync();
        db.InitialMembershipRequests.Add(new() { BillingAccountId = account.Id, Anchor = new(2026, 6, 1), RequestedAt = approved });
        await db.SaveChangesAsync();
        await Scan(app, account.Id);
        await Scan(app, account.Id);
        var view = await View(app, Actor(user.Id), user.Id);

        Assert.False(view.Membership.Active);
        var invoice = Assert.Single(view.Invoices);
        var line = Assert.Single(invoice.Lines).Line;

        Assert.Equal(LineState.Expired, line.State);
        Assert.Equal(new DateOnly(2026, 6, 15), line.ChargeDueOn);
        Assert.Equal(new DateOnly(2026, 6, 16), line.ExpiresOn);
        Assert.NotNull(invoice.Invoice.ClosedAt);
        Assert.DoesNotContain(await db.Deliveries.ToListAsync(), x => x.Kind == MailKind.Invoice);
    }
}
