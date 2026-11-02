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
    [Trait("Acceptance", "SVC")]
    public async Task Retiring_a_service_keeps_an_issued_renewal_recoverable_by_a_timely_receipt()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(6, 20);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        var renewalInvoice = view.Invoices.Single(x => x.Invoice.ClosedAt is null);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 0, 10, "door", Retired: true)));
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.DoesNotContain(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service);
        await using (var db = Context())
            Assert.Equal(Nulysa.Services.ServiceEndReason.ServiceRetired, (await db.ServiceEnrollments.SingleAsync(x => x.ServiceDefinitionId == service)).EndReason);

        await Pay(app, renewalInvoice.Reference, 40, new(2026, 7, 1));
        view = await View(app, board, id);
        var renewal = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;

        Assert.Equal(LineState.Fulfilled, renewal.State);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        Assert.Contains(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service && x.Standing.Active);
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Pre_due_receipt_after_worker_outage_reopens_service_and_membership()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.False(view.Membership.Active);
        Assert.Empty(view.Subscriptions);
        await Pay(app, view.Groups.Single().PaymentReference, 40, new(2026, 6, 30));
        view = await View(app, board, id);
        var renewals = view.Invoices.SelectMany(x => x.Lines).Where(x => x.Line.Purpose == PurchasePurpose.Renewal).ToList();

        Assert.True(view.Membership.Active);
        Assert.Contains(renewals, x => x.Line.Benefit == Benefit.Membership && x.Line.State == LineState.Fulfilled);
        Assert.Contains(renewals, x => x.Line.Benefit == Benefit.Service && x.Line.State == LineState.Fulfilled);
        Assert.Contains(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service && x.Standing.Active);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Membership && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task Missed_renewal_with_an_early_credit_lot_recovers_after_an_outage()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);

        // Pay early (before the renewal invoice window), which becomes a credit lot.
        await Pay(app, view.Groups.Single().PaymentReference, 40);

        // No scans between 6/1 and 7/2; the worker misses the invoice window.
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Contains(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service && x.Standing.Active);
        var renewal = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;
        Assert.Equal(LineState.Fulfilled, renewal.State);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));

        // The lot should be consumed.
        await using var db = Context();
        var lot = await db.CreditLots.SingleAsync();
        Assert.Equal(0m, lot.RemainingAmount);
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task Delayed_receipt_lot_recovers_an_ended_service_renewal()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);

        On(6, 20);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        Assert.DoesNotContain(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service);

        // A delayed receipt dated before expiry arrives after the service already ended.
        var receiptId = await Pay(app, "not-a-reference", 40, new(2026, 6, 1));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receiptId, view.AccountId, new ReceiptDestination.BillingCredit(), Guid.NewGuid(), "Delayed bank payment"));
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Contains(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service && x.Standing.Active);
        var renewal = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;
        Assert.Equal(LineState.Fulfilled, renewal.State);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Timely_delayed_service_renewal_is_recovered_after_expiry()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 7);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(6, 20);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        var renewalInvoice = view.Invoices.Single(x => x.Invoice.ClosedAt is null);
        On(7, 10);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.DoesNotContain(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service);
        await Pay(app, renewalInvoice.Reference, 40, new(2026, 7, 5));
        view = await View(app, board, id);
        var renewal = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;

        Assert.Equal(LineState.Fulfilled, renewal.State);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        Assert.Contains(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service && x.Standing.Active);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Partial_timely_receipt_reopens_a_service_and_a_second_receipt_completes_it()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(6, 20);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        var renewalInvoice = view.Invoices.Single(x => x.Invoice.ClosedAt is null);
        await Pay(app, renewalInvoice.Reference, 10, new(2026, 6, 20));
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.DoesNotContain(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service);
        await Pay(app, renewalInvoice.Reference, 10, new(2026, 7, 1));
        view = await View(app, board, id);
        var serviceLine = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;

        Assert.Equal(LineState.Expired, serviceLine.State);
        Assert.Equal(new Balances(10, 0, 0), view.Balances);
        await Pay(app, renewalInvoice.Reference, 20, new(2026, 7, 1));
        view = await View(app, board, id);
        serviceLine = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;

        Assert.Equal(LineState.Fulfilled, serviceLine.State);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        Assert.Contains(view.Subscriptions, x => x.Enrollment.ServiceDefinitionId == service && x.Standing.Active);
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task A_late_receipt_does_not_reopen_an_old_enrollment_after_resubscription()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(7, 2);
        await Scan(app, view.AccountId);
        long oldEnrollment;
        await using (var db = Context())
            oldEnrollment = (await db.ServiceEnrollments.SingleAsync(x => x.ServiceDefinitionId == service)).Id;

        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(board, view.AccountId, Guid.NewGuid()));
        view = await View(app, board, id);
        var resumptionBill = view.Invoices.Single(x => x.Invoice.ClosedAt is null);
        await Pay(app, resumptionBill.Reference, resumptionBill.Remaining);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        await Pay(app, view.Groups.Single().PaymentReference, 30, new(2026, 7, 1));
        await using var after = Context();

        Assert.NotNull((await after.ServiceEnrollments.SingleAsync(x => x.Id == oldEnrollment)).EndedAt);
        Assert.Single(await after.ServiceEnrollments.Where(x => x.ServiceDefinitionId == service && x.EndedAt == null).ToListAsync());
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Missed_membership_renewal_without_an_issued_line_sends_one_inactive_notice()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        Settings["Billing:AutomaticMembershipReactivation"] = "false";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        On(7, 2);
        await Scan(app, view.AccountId);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.False(view.Membership.Active);
        await using var db = Context();
        Assert.Equal(1, await db.Deliveries.CountAsync(x => x.UserId == id && x.Kind == MailKind.MembershipInactive));
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Zero_grace_missed_renewal_is_settled_by_a_due_date_receipt_after_worker_outage()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        Settings["Billing:AutomaticMembershipReactivation"] = "false";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        // The worker misses both invoice preparation and the first post-due scan.
        On(7, 2);
        await Scan(app, view.AccountId);
        await Pay(app, view.Invoices[0].Reference, 10, new(2026, 7, 1));
        view = await View(app, board, id);
        var renewal = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Renewal);

        Assert.Equal(LineState.Fulfilled, renewal.Line.State);
        Assert.Equal(new DateOnly(2026, 7, 1), renewal.Line.StartsOn);
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
        Assert.True(view.Membership.Active);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Timely_delayed_renewal_restores_original_period_after_timeout_but_genuine_return_is_prorated()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10);
        On(6, 20);
        await Scan(app, account);
        On(7, 9);
        await Scan(app, account);

        Assert.False((await View(app, board, id)).Membership.Active);
        await Pay(app, reference, 10, new(2026, 7, 7));
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndExclusive == new DateOnly(2026, 8, 1));
        var renewal = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 7, 1)).Line;

        Assert.Equal(new DateOnly(2026, 7, 7), renewal.EffectiveOn);
        Assert.Equal(10m, renewal.Amount);
        On(7, 20);
        await Scan(app, account);
        On(8, 9);
        await Scan(app, account);
        On(9, 15);
        await Pay(app, reference, 10);
        view = await View(app, board, id);

        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 9, 15) && x.EndExclusive == new DateOnly(2026, 10, 1));
        Assert.DoesNotContain(view.Coverage, x => x.StartsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(4.67m, view.Balances.BillingCredit);
    }

}
