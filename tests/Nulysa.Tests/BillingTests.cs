using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Tests;

[Trait("Group", "Billing")]
public sealed class BillingTests : MembershipTest
{
    private void On(int month, int day) => Clock.Now = new DateTimeOffset(2026, month, day, 10, 0, 0, TimeSpan.Zero);
    private Task<BillingView> View(WebApplication app, ClaimsPrincipal actor, long id) => Run(app, async sp => (await sp.GetRequiredService<BillingQueries>().Member(actor, id))!);
    private Task<long> Pay(WebApplication app, string reference, decimal amount, DateOnly? received = null, string? sourceId = null)
        => Run(app, sp => sp.GetRequiredService<BillingCommands>().Import(new("test-bank", "association", sourceId ?? Guid.NewGuid().ToString(), amount, "EUR",
            received ?? DateOnly.FromDateTime(Clock.Now.DateTime), reference, "Payment", "Synthetic payer")));
    private Task Scan(WebApplication app, long account) => Run(app, sp => sp.GetRequiredService<BillingCommands>().Scan(account));
    private async Task<long> Service(WebApplication app, ClaimsPrincipal board, int free = 0, int grace = 7, bool self = false, decimal price = 30)
    {
        var id = await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, null, new("door", "Door", self, free, grace, 10, "door")));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == id);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2000, 1, 1), price));
        return id;
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Retired_services_are_hidden_and_cannot_be_subscribed()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 7, 10, "door", Retired: true)));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 7, 10, "door")));
        var services = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Services(Actor(id)));

        Assert.DoesNotContain(services, x => x.Id == service);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid())));
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Retiring_a_service_stops_renewals_and_keeps_paid_coverage()
    {
        On(6, 1);
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
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 0, 10, "door", Retired: true)));
        view = await View(app, board, id);
        await using (var db = Context())
        {
            var serviceFee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service);
            Assert.DoesNotContain(view.Forecasts, x => x.Description == "Door");
            await Assert.ThrowsAsync<ValidationException>(() =>
                Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, serviceFee.Id, new(2026, 6, 1), 40)));
        }
        On(6, 20);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.DoesNotContain(view.Invoices.SelectMany(x => x.Lines),
            x => x.Line.Benefit == Benefit.Service && x.Line.Purpose is PurchasePurpose.Renewal or PurchasePurpose.Alignment);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.EndsOn == new DateOnly(2026, 7, 1));
        await using (var db = Context())
        {
            Assert.True((await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service)).Retired);
            Assert.False((await db.Fees.SingleAsync(x => x.Id == 1)).Retired);
        }
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.Empty(view.Subscriptions);
        await using var after = Context();
        Assert.Contains(await after.ServiceChanges.ToListAsync(), x => x.Action == "service-retired");
    }

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
            Assert.Equal("service-retired", (await db.ServiceEnrollments.SingleAsync(x => x.ServiceDefinitionId == service)).EndReason);

        await Pay(app, renewalInvoice.Reference, 40, new(2026, 7, 1));
        view = await View(app, board, id);
        var renewal = view.Invoices.SelectMany(x => x.Lines)
            .Single(x => x.Line.Benefit == Benefit.Service && x.Line.Purpose == PurchasePurpose.Renewal).Line;

        Assert.Equal(LineState.Fulfilled, renewal.State);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
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
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Membership && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
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
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
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
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
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
    [Trait("Acceptance", "BILL")]
    public async Task Approval_partial_membership_first_funding_independent_fulfilment_and_cancellation_conserve_every_euro()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, Actor(id), id);
        var account = initial.AccountId;

        Assert.True(initial.Membership.Active);
        Assert.Equal(10m, Assert.Single(initial.Invoices).Remaining);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        var bill = Assert.Single((await View(app, board, id)).Invoices);

        Assert.Equal(40m, bill.Total);
        var receiptId = await Pay(app, bill.Reference, 15m, sourceId: "duplicate-safe");

        Assert.Equal(receiptId, await Pay(app, bill.Reference, 15m, sourceId: "duplicate-safe"));
        var partial = await View(app, board, id);
        bill = Assert.Single(partial.Invoices);

        Assert.Equal(10m, bill.Paid);
        Assert.Equal(5m, bill.Held);
        Assert.Equal(25m, bill.Remaining);
        Assert.Single(partial.Coverage);
        Assert.Equal(Benefit.Membership, partial.Coverage[0].Benefit);
        Assert.Equal(new Balances(0, 0, 5), partial.Balances);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().CancelInvoice(board, account, bill.Invoice.Id, Guid.NewGuid(), "Cancel remaining requests"));
        var cancelled = await View(app, board, id);

        Assert.Equal(new Balances(5, 0, 0), cancelled.Balances);
        Assert.Single(cancelled.Coverage);
        Assert.NotNull(Assert.Single(cancelled.Invoices).Invoice.ClosedAt);
        await using var db = Context();

        Assert.Equal(15m, await db.Receipts.Where(x => x.State == ReceiptState.Posted).SumAsync(x => x.Amount));
        Assert.Equal(10m, await db.MoneyMovements.Where(x => x.Kind == MovementKind.LinePurchase).SumAsync(x => x.Amount));
        Assert.Equal(5m, await db.MoneyMovements.Where(x => x.Kind == MovementKind.LineToBilling).SumAsync(x => x.Amount));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [Trait("Acceptance", "DATE")]
    public async Task Initial_and_return_membership_due_dates_are_inclusive_including_delayed_imports(bool returning, bool late, bool delayed)
    {
        On(6, 1);
        Settings["Billing:AutomaticMembershipReactivation"] = "false";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        if (returning)
        {
            On(6, 20);
            await Scan(app, view.AccountId);
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(board, view.AccountId, Guid.NewGuid()));
            view = await View(app, board, id);
        }
        var invoice = Assert.Single(view.Invoices, x => x.Invoice.ClosedAt is null);
        var original = Assert.Single(invoice.Lines).Line;
        var receivedOn = original.ChargeDueOn.AddDays(late ? 1 : 0);
        var importedOn = receivedOn.AddDays(delayed ? 1 : 0);
        Clock.Now = new DateTimeOffset(importedOn.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

        await Scan(app, view.AccountId);
        var receipt = await Pay(app, invoice.Reference, 10, receivedOn);
        view = await View(app, board, id);
        var settled = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == original.Id).Line;
        await using var db = Context();

        Assert.Equal(original.ChargeDueOn.AddDays(1), settled.ExpiresOn);
        Assert.Equal(late ? LineState.Expired : LineState.Fulfilled, settled.State);
        Assert.Equal(ReceiptState.Posted, (await db.Receipts.SingleAsync(x => x.Id == receipt)).State);
        Assert.Equal(late ? 10m : 10m - settled.Amount, view.Balances.BillingCredit);
        Assert.Equal(0m, view.Balances.Held);
        if (!late)
        {
            Assert.Equal(returning ? receivedOn : new DateOnly(2026, 6, 1), settled.StartsOn);
            Assert.Contains(view.Coverage, x => x.InvoiceLineId == original.Id && x.StartsOn == settled.StartsOn && x.EndsOn == settled.EndsOn);
        }
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [Trait("Acceptance", "DATE")]
    public async Task Initial_service_due_date_accepts_timely_receipts_but_not_the_following_day(bool late, bool delayed)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var invoice = Assert.Single(view.Invoices, x => x.Invoice.ClosedAt is null);
        var original = Assert.Single(invoice.Lines).Line;
        var receivedOn = original.ChargeDueOn.AddDays(late ? 1 : 0);
        var importedOn = receivedOn.AddDays(delayed ? 1 : 0);
        Clock.Now = new DateTimeOffset(importedOn.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

        await Scan(app, view.AccountId);
        await Pay(app, invoice.Reference, 30, receivedOn);
        view = await View(app, board, id);
        var settled = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == original.Id).Line;

        Assert.Equal(original.ChargeDueOn.AddDays(1), settled.ExpiresOn);
        Assert.Equal(late ? LineState.Expired : LineState.Fulfilled, settled.State);
        Assert.Equal(late ? 30m : 30m - settled.Amount, view.Balances.BillingCredit);
        Assert.Equal(0m, view.Balances.Held);
        if (!late)
        {
            Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
            Assert.Contains(view.Coverage, x => x.InvoiceLineId == original.Id && x.StartsOn == receivedOn && x.EndsOn == new DateOnly(2026, 7, 1));
        }
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Acceptance", "DATE")]
    public async Task Zero_grace_renewals_accept_due_day_payment_even_when_the_worker_missed_invoice_preparation(bool service, bool prepared)
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        if (service)
        {
            var serviceId = await Service(app, board, grace: 0);
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, serviceId, 1, false, Guid.NewGuid()));
        }
        var amount = service ? 40m : 10m;
        await Pay(app, view.Invoices[0].Reference, amount);
        if (prepared)
        {
            On(6, 20);
            await Scan(app, view.AccountId);
        }
        On(7, 1);
        var due = await View(app, board, id);

        Assert.True(due.Membership.Active);
        Assert.Equal("Due today", due.Membership.Description);
        Assert.All(due.Subscriptions, x => Assert.True(x.Standing.Active));
        Assert.All(due.Subscriptions, x => Assert.Equal("Due today", x.Standing.Description));
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        var renewals = view.Invoices.SelectMany(x => x.Lines).Where(x => x.Line.Purpose == PurchasePurpose.Renewal).ToList();

        Assert.Equal(service ? 2 : 1, renewals.Count);
        Assert.All(renewals, x => Assert.Equal(new DateOnly(2026, 7, 2), x.Line.ExpiresOn));
        Assert.All(renewals, x => Assert.Null(x.Line.GraceNotifiedAt));
        await Pay(app, view.Invoices.Single(x => x.Invoice.ClosedAt is null).Reference, amount);
        view = await View(app, board, id);

        Assert.All(view.Invoices.SelectMany(x => x.Lines), x => Assert.Equal(LineState.Fulfilled, x.Line.State));
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Zero_grace_renewals_reject_a_payment_after_the_due_date()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "0";
        Settings["Billing:AutomaticMembershipReactivation"] = "false";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        On(6, 20);
        await Scan(app, view.AccountId);
        var renewalInvoice = (await View(app, board, id)).Invoices.Single(x => x.Invoice.ClosedAt is null);
        On(7, 2);
        await Scan(app, view.AccountId);
        await Pay(app, renewalInvoice.Reference, 10, new(2026, 7, 2));
        view = await View(app, board, id);
        var renewal = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Renewal);

        Assert.Equal(LineState.Expired, renewal.Line.State);
        Assert.Equal(10m, view.Balances.BillingCredit);
        Assert.False(view.Membership.Active);
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
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
        Assert.True(view.Membership.Active);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task One_day_renewal_grace_notices_and_accepts_the_following_day()
    {
        On(6, 1);
        Settings["Billing:MembershipRenewalGraceDays"] = "1";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        On(6, 20);
        await Scan(app, view.AccountId);
        var renewalInvoice = (await View(app, board, id)).Invoices.Single(x => x.Invoice.ClosedAt is null);
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);
        var renewal = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Renewal);

        Assert.Equal(new DateOnly(2026, 7, 3), renewal.Line.ExpiresOn);
        Assert.NotNull(renewal.Line.GraceNotifiedAt);
        Assert.True(view.Membership.Active);
        await Pay(app, renewalInvoice.Reference, 10, new(2026, 7, 2));
        view = await View(app, board, id);
        var settled = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == renewal.Line.Id).Line;

        Assert.Equal(LineState.Fulfilled, settled.State);
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
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
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
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

        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 9, 15) && x.EndsOn == new DateOnly(2026, 10, 1));
        Assert.DoesNotContain(view.Coverage, x => x.StartsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(4.67m, view.Balances.BillingCredit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "DATE")]
    public async Task Delayed_return_payment_before_its_quote_buys_from_receipt_date_without_repricing_quoted_days(bool changePrice)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        var reference = initial.Invoices[0].Reference;
        On(6, 20);
        await Scan(app, account);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));
        if (changePrice)
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, 1, new(2026, 6, 20), 20));
        On(6, 21);
        await Pay(app, reference, 10, new(2026, 6, 18));
        var view = await View(app, board, id);
        var line = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Resumption).Line;

        Assert.Equal(LineState.Fulfilled, line.State);
        Assert.Equal(4.33m, line.Amount);
        Assert.Equal(new DateOnly(2026, 6, 18), line.StartsOn);
        Assert.Equal(new DateOnly(2026, 7, 1), line.EndsOn);
        Assert.Equal(new DateOnly(2026, 6, 18), line.EffectiveOn);
        Assert.Equal(5.67m, view.Balances.BillingCredit);
        Assert.Equal(new DateOnly(2026, 6, 18), view.Coverage.Min(x => x.StartsOn));
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "DATE")]
    public async Task Backdated_return_completion_resumes_billing_at_the_purchased_boundary(bool splitPayment)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        var reference = initial.Invoices[0].Reference;
        On(7, 2);
        await Scan(app, account);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));
        if (splitPayment)
        {
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().ChangeInterval(Actor(id), account, initial.Items.Single().Id, 3, Guid.NewGuid()));
            await Pay(app, reference, 1, new(2026, 6, 25));
        }
        On(7, 3);
        await Pay(app, reference, splitPayment ? 9 : 10, new(2026, 6, splitPayment ? 24 : 25));
        var view = await View(app, board, id);
        var returned = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Resumption).Line;

        Assert.Equal(LineState.Fulfilled, returned.State);
        Assert.Equal(2m, returned.Amount);
        Assert.Equal(new DateOnly(2026, 6, 25), returned.EffectiveOn);
        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(view.Items).NextOn);
        await Scan(app, account);
        await Scan(app, account);
        view = await View(app, board, id);
        var renewal = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);

        Assert.Equal(new DateOnly(2026, 7, 1), renewal.Line.StartsOn);
        Assert.Equal(new DateOnly(2026, splitPayment ? 9 : 8, 1), renewal.Line.EndsOn);
        Assert.Equal(8m, renewal.Held);
        await Pay(app, view.Invoices.Single(x => x.Invoice.Id == renewal.Line.InvoiceId).Reference, renewal.Remaining);
        On(7, 8);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Equal(renewal.Line.EndsOn, view.Items.Single().NextOn);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false, false, 383)]
    [InlineData(true, false, 417)]
    [InlineData(false, true, 300)]
    [Trait("Acceptance", "ADJ")]
    public async Task Prequote_return_days_keep_discounts_until_the_association_local_withdrawal_date(bool withdrawBeforeQuote, bool replace, int expectedCents)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        var discount = await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1,
            new(2026, 6, 1), new(2026, 8, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Approved discount"));
        async Task Withdraw(int utcDay)
        {
            Clock.Now = new DateTimeOffset(2026, 6, utcDay, 22, 30, 0, TimeSpan.Zero);
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, discount!.Value, Guid.NewGuid(), "Discount ended"));
        }
        if (withdrawBeforeQuote)
            await Withdraw(18);
        On(6, 20);
        await Scan(app, account);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));
        if (!withdrawBeforeQuote)
            await Withdraw(20);
        if (replace)
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1,
            new(2026, 6, 21), new(2026, 8, 1), DiscountKind.Percentage, 25, Guid.NewGuid(), "Replacement discount"));
        On(6, 21);
        await Pay(app, initial.Invoices[0].Reference, 10, new(2026, 6, 18));
        var view = await View(app, board, id);
        var line = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Resumption).Line;

        Assert.Equal(LineState.Fulfilled, line.State);
        Assert.Equal(expectedCents / 100m, line.Amount);
        Assert.Equal(new DateOnly(2026, 6, 18), line.StartsOn);
        Assert.Equal(new DateOnly(2026, 7, 1), line.EndsOn);
        Assert.Equal(10m - line.Amount, view.Balances.BillingCredit);
        var history = view.Discounts.Single(x => x.Id == discount);

        Assert.NotNull(history.WithdrawnAt);
        Assert.Equal(new DateOnly(2026, 8, 1), history.EndsOn);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Initial_service_uses_last_required_receipt_date_including_out_of_order_and_never_reprices_on_a_timer()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        On(6, 6);
        await Pay(app, reference, 5);
        On(6, 10);
        await Scan(app, account);
        var before = (await View(app, board, id)).Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service);

        Assert.Equal(30m, before.Line.Amount);
        Assert.Equal(5m, before.Held);
        Assert.Equal(LineState.Open, before.Line.State);
        await Pay(app, reference, 20, new(2026, 6, 4));
        view = await View(app, board, id);
        var line = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service).Line;

        Assert.Equal(25m, line.Amount);
        Assert.Equal(new DateOnly(2026, 6, 6), line.StartsOn);
        Assert.Equal(new DateOnly(2026, 6, 6), line.EffectiveOn);
        Assert.Equal(LineState.Fulfilled, line.State);
        Assert.Equal(new Balances(0, 0, 0), view.Balances);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(10, 1550)]
    [Trait("Acceptance", "ADJ")]
    public async Task Delayed_initial_service_payment_keeps_approved_discounts_across_rollover_and_future_free_coverage(int freeDays, int expectedCents)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        var reference = initial.Invoices[0].Reference;
        await Pay(app, reference, 10);
        var service = await Service(app, board, free: freeDays);
        On(6, 20);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service);
        On(6, 21);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, fee.Id,
            new(2026, 6, 21), new(2026, 8, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Approved service discount"));
        On(7, 2);
        await Scan(app, account);
        await Pay(app, reference, 50, new(2026, 6, 25));
        var view = await View(app, board, id);
        var line = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Benefit == Benefit.Service).Line;

        Assert.Equal(LineState.Fulfilled, line.State);
        Assert.Equal(expectedCents / 100m, line.Amount);
        Assert.Equal(new DateOnly(2026, 6, freeDays == 0 ? 25 : 30), line.StartsOn);
        Assert.Equal(new DateOnly(2026, 6, 25), line.EffectiveOn);
        Assert.Contains(await db.LineAdjustments.AsNoTracking().ToListAsync(), x => x.InvoiceLineId == line.Id);
        Assert.Equal(line.EndsOn, view.Items.Single(x => x.ServiceEnrollmentId is not null).NextOn);
        await Scan(app, account);
        await Scan(app, account);
        On(7, 8);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.EndsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Free_days_are_once_only_and_independent_from_payment_window_and_stopping_preserves_coverage()
    {
        On(3, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, free: 20);
        On(3, 10);
        var key = Guid.NewGuid();
        var enrollment = await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, key));

        Assert.Equal(enrollment, await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, key)));
        view = await View(app, board, id);
        var grant = Assert.Single(view.Coverage, x => x.Source == CoverageSource.InitialFree);

        Assert.Equal(new DateOnly(2026, 3, 30), grant.EndsOn);
        var serviceLine = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service).Line;

        Assert.Equal(new DateOnly(2026, 3, 30), serviceLine.StartsOn);
        Assert.Equal(new DateOnly(2026, 5, 1), serviceLine.EndsOn);
        Assert.Equal(new DateOnly(2026, 3, 25), serviceLine.ExpiresOn);
        On(3, 25);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Equal(LineState.Expired, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == serviceLine.Id).Line.State);
        On(3, 30);
        await Scan(app, account);

        Assert.Empty((await View(app, board, id)).Subscriptions);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);

        Assert.Single(view.Coverage, x => x.Source == CoverageSource.InitialFree);
        Assert.False(Assert.Single(view.Subscriptions).Standing.Active);
        var current = view.Subscriptions[0].Enrollment.Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(board, account, current, true, Guid.NewGuid(), "Explicit removal"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, true, Guid.NewGuid()));
        view = await View(app, board, id);

        Assert.Single(view.Coverage, x => x.Source == CoverageSource.AdministratorGrant);
        current = view.Subscriptions[0].Enrollment.Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(Actor(id), account, current, false, Guid.NewGuid(), "Stop renewal"));
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.All(view.Invoices.SelectMany(x => x.Lines).Where(x => x.Line.Benefit == Benefit.Service), x => Assert.NotEqual(LineState.Open, x.Line.State));
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Discount_grant_and_honorary_coverage_release_only_unfinished_funds_without_fictitious_money()
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
        await Pay(app, reference, 5);
        var grantKey = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().GrantMembershipMonths(board, account, 1, grantKey, "Volunteer compensation"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().GrantMembershipMonths(board, account, 1, grantKey, "Volunteer compensation"));
        view = await View(app, board, id);

        Assert.Equal(5m, view.Balances.BillingCredit);
        Assert.Single(view.Coverage, x => x.Source == CoverageSource.AdministratorGrant);
        Assert.DoesNotContain(view.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 8, 1), new(3026, 8, 1), DiscountKind.Percentage, 100, Guid.NewGuid(), "Honorary membership"));
        On(7, 20);
        await Scan(app, account);
        view = await View(app, board, id);
        var honorary = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 8, 1)).Line;

        Assert.Equal(LineState.Fulfilled, honorary.State);
        Assert.Equal(0, honorary.Amount);
        Assert.Null(honorary.EffectiveOn);
        Assert.Equal(5m, view.Balances.BillingCredit);
        await using var db = Context();

        Assert.Equal(2, await db.Receipts.CountAsync());
        Assert.DoesNotContain(await db.MoneyMovements.ToListAsync(), x => x.Amount <= 0);
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Completed_future_discount_is_a_reviewed_credit_preserving_original_purchase_and_coverage()
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
        await Pay(app, reference, 10);
        view = await View(app, board, id);
        var line = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 7, 1)).Line;
        var discount = await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 7, 1), new(2026, 8, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Future discount"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().CreditFutureDiscount(board, account, line.Id, discount!.Value, 5, Guid.NewGuid(), "Reviewed July purchase"));
        view = await View(app, board, id);

        Assert.Equal(5m, view.Balances.BillingCredit);
        Assert.Equal(10m, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == line.Id).Line.Amount);
        Assert.Equal(2, view.Coverage.Count);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().CreditFutureDiscount(board, account, line.Id, discount!.Value, 5, Guid.NewGuid(), "Duplicate correction")));
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Future_discount_credits_compensate_distinct_dates_and_only_uncredited_parts_of_replacement_discounts()
    {
        Settings["Billing:DefaultIntervalMonths"] = "12";
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        await Pay(app, initial.Invoices[0].Reference, 120);
        On(6, 20);
        var line = Assert.Single((await View(app, board, id)).Invoices.SelectMany(x => x.Lines)).Line;
        async Task<long> Discount(DateOnly start, DateOnly end, decimal percent) => (await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AddDiscount(board, account, 1, start, end, DiscountKind.Percentage, percent, Guid.NewGuid(), "Future discount")))!.Value;
        Task Credit(long discount, decimal amount) => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .CreditFutureDiscount(board, account, line.Id, discount, amount, Guid.NewGuid(), "Reviewed future purchase"));
        Task Withdraw(long discount) => Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, discount, Guid.NewGuid(), "Replacement discount"));
        var july = await Discount(new(2026, 7, 1), new(2026, 8, 1), 50);
        var august = await Discount(new(2026, 8, 1), new(2026, 9, 1), 50);
        await Credit(july, 5.10m);
        await Credit(august, 5.10m);

        Assert.Equal(10.20m, (await View(app, board, id)).Balances.BillingCredit);
        await Assert.ThrowsAsync<ValidationException>(() => Credit(july, 5.10m));
        await Withdraw(july);
        await Withdraw(august);
        var replacement = await Discount(new(2026, 7, 1), new(2026, 9, 1), 75);
        await Credit(replacement, 5.09m);

        await Assert.ThrowsAsync<ValidationException>(() => Credit(replacement, 5.09m));
        await Withdraw(replacement);
        var julyFree = await Discount(new(2026, 7, 1), new(2026, 8, 1), 100);
        await Credit(julyFree, 2.55m);
        var view = await View(app, board, id);

        Assert.Equal(17.84m, view.Balances.BillingCredit);
        Assert.Single(view.Coverage);
        Assert.Equal(120m, Assert.Single(view.Invoices.SelectMany(x => x.Lines)).Line.Amount);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "RACE")]
    public async Task Concurrent_transfers_cannot_double_spend_and_reused_keys_cannot_change_inputs()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        await Pay(app, view.DepositReference, 10);
        async Task<bool> Transfer()
        {
            try
            {
                await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, Guid.NewGuid(), 7, false));
                return true;
            }
            catch (ValidationException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(Transfer(), Transfer());

        Assert.Single(results, x => x);
        view = await View(app, board, id);

        Assert.Equal(new Balances(0, 3, 7), view.Balances);
        var key = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, key, 3, false));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, key, 3, false));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, key, 2, false)));
        view = await View(app, board, id);

        Assert.Equal(new Balances(0, 0, 0), view.Balances);
        Assert.Equal(10m, view.Invoices[0].Paid);
    }

    [Fact]
    [Trait("Acceptance", "JOB")]
    public async Task Billing_due_work_isolates_a_broken_account_and_processes_later_accounts()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var broken = await Admit(app, board, "broken@example.test");
        var good = await Admit(app, board, "good@example.test");
        var brokenView = await View(app, board, broken);
        var goodView = await View(app, board, good);
        await Pay(app, goodView.Invoices[0].Reference, 10);
        await using (var db = Context())
        {
            var operation = new BillingOperation { Id = Guid.NewGuid(), BillingAccountId = brokenView.AccountId, Kind = "test-corruption", OccurredAt = Clock.Now };
            db.BillingOperations.Add(operation);
            db.MoneyMovements.Add(new()
            {
                BillingAccountId = brokenView.AccountId,
                OperationId = operation.Id,
                Kind = MovementKind.BillingToPurchase,
                Amount = 1m,
                EffectiveOn = new DateOnly(2026, 6, 1)
            });
            await db.SaveChangesAsync();
        }
        On(6, 20);
        await using var scope = app.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<BillingDueWorkJob>(scope.ServiceProvider);

        await Assert.ThrowsAsync<AggregateException>(() => job.Execute(null!).AsTask());
        await using var check = Context();
        var renewal = await (from line in check.InvoiceLines
                             join invoice in check.Invoices on line.InvoiceId equals invoice.Id
                             where invoice.BillingAccountId == goodView.AccountId && line.StartsOn == new DateOnly(2026, 7, 1)
                             select line).AnyAsync();

        Assert.True(renewal);
    }

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
        var retainedPage = await client.GetStringAsync("admin/billing/" + account);

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
        Assert.Equal(new DateOnly(2026, 8, 1), view.Items.Single().NextOn);
        Assert.Equal(LineState.Cancelled, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == cancelled).Line.State);
        Assert.Equal(2.58m, view.Balances.BillingCredit);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    public async Task One_off_reference_uses_the_same_funding_path_without_recurring_coverage_or_wallet_sweep()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, account, Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        view = await View(app, board, id);
        var one = view.Invoices.Single(x => x.Invoice.Id == invoiceId);

        Assert.Null(one.Invoice.BillingGroupId);
        await Pay(app, one.Reference, 15);
        view = await View(app, board, id);

        Assert.Equal(12.50m, view.Invoices.Single(x => x.Invoice.Id == invoiceId).Paid);
        Assert.Empty(view.Coverage);
        Assert.Equal(2.50m, view.Balances.Held);
        Assert.Equal(0, view.Balances.BillingCredit);
        var other = await Admit(app, board, "other@example.test");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().Account(Actor(other), account)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().Review(Actor(id))));
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task One_off_due_date_is_inclusive_and_the_following_day_is_late()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var firstId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(),
            "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        var secondId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(),
            "Repair", "repair", 12.50m, new(2026, 6, 20)));
        var oneOffs = (await View(app, board, id)).Invoices;
        var first = oneOffs.Single(x => x.Invoice.Id == firstId);
        var second = oneOffs.Single(x => x.Invoice.Id == secondId);
        On(6, 20);
        await Pay(app, first.Reference, 12.50m, new(2026, 6, 20));
        On(6, 21);
        await Pay(app, second.Reference, 12.50m, new(2026, 6, 21));
        view = await View(app, board, id);

        Assert.Equal(LineState.Fulfilled, view.Invoices.Single(x => x.Invoice.Id == firstId).Lines.Single().Line.State);
        Assert.Equal(LineState.Expired, view.Invoices.Single(x => x.Invoice.Id == secondId).Lines.Single().Line.State);
        Assert.Equal(12.50m, view.Balances.BillingCredit);
        Assert.Equal(0m, view.Balances.Held);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Upgrade_extends_only_open_exclusive_deadlines_and_preserves_closed_purchases()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261031185213_BillingMovementEffectiveIndex");
        var invoices = new Dictionary<LineState, long>();
        foreach (var state in Enum.GetValues<LineState>())
        {
            var invoiceId = (await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId,
                Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20))))!.Value;
            invoices.Add(state, invoiceId);
            if (state == LineState.Fulfilled)
            {
                var invoice = (await View(app, board, id)).Invoices.Single(x => x.Invoice.Id == invoiceId);
                await Pay(app, invoice.Reference, 12.50m);
            }
            if (state == LineState.Cancelled)
                await Run(app, sp => sp.GetRequiredService<BillingCommands>().CancelInvoice(board, view.AccountId, invoiceId,
                    Guid.NewGuid(), "Cancelled workshop"));
        }
        var legacy = await db.InvoiceLines.ToListAsync();
        foreach (var line in legacy)
        {
            line.ExpiresOn = line.ChargeDueOn;
            if (line.InvoiceId == invoices[LineState.Expired])
            {
                line.State = LineState.Expired;
                (await db.Invoices.SingleAsync(x => x.Id == line.InvoiceId)).ClosedAt = Clock.Now;
            }
        }
        await db.SaveChangesAsync();
        // Already-inclusive requests must not gain another day on upgrade.
        var inclusiveId = (await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(),
            "Inclusive workshop", "workshop", 5m, new(2026, 6, 20))))!.Value;
        var inclusiveLine = await db.InvoiceLines.SingleAsync(x => x.InvoiceId == inclusiveId);
        inclusiveLine.ExpiresOn = new DateOnly(2026, 6, 27);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.InvoiceLines.AsNoTracking().ToListAsync();

        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        var after = await db.InvoiceLines.AsNoTracking().ToDictionaryAsync(x => x.Id);

        foreach (var line in before)
        {
            var expectedExpiry = line.State == LineState.Open && line.ExpiresOn <= line.ChargeDueOn
                ? line.ChargeDueOn.AddDays(1) : line.ExpiresOn;
            Assert.Equal(expectedExpiry, after[line.Id].ExpiresOn);
            Assert.Equal(line.State, after[line.Id].State);
            Assert.Equal(line.ChargeDueOn, after[line.Id].ChargeDueOn);
            Assert.Equal(line.StartsOn, after[line.Id].StartsOn);
            Assert.Equal(line.EndsOn, after[line.Id].EndsOn);
            Assert.Equal(line.Amount, after[line.Id].Amount);
        }
        var extended = after[invoices[LineState.Open]].ExpiresOn;
        await db.Database.MigrateAsync("20261031185213_BillingMovementEffectiveIndex");
        var downgraded = await db.InvoiceLines.AsNoTracking().SingleAsync(x => x.InvoiceId == invoices[LineState.Open]);
        Assert.Equal(extended, downgraded.ExpiresOn);
        On(6, 20);
        var payable = (await View(app, board, id)).Invoices.Single(x => x.Invoice.Id == invoices[LineState.Open]);
        await Pay(app, payable.Reference, payable.Remaining);
        var settled = (await View(app, board, id)).Invoices.Single(x => x.Invoice.Id == payable.Invoice.Id);

        Assert.Equal(LineState.Fulfilled, Assert.Single(settled.Lines).Line.State);
        Assert.Equal(12.50m, settled.Paid);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "BILL")]
    public async Task Reviewed_receipt_targets_a_one_off_invoice_before_recurring_bills_with_ownership_and_retry_checks(bool delayed)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var other = await Admit(app, board, "other@example.test");
        var otherView = await View(app, board, other);
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        var receipt = await Pay(app, "missing-reference", 12.50m, new(2026, 6, 1), "workshop");
        if (delayed)
        {
            On(6, 21);
            await Scan(app, view.AccountId);
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(Actor(id), receipt, view.AccountId, null, false, Guid.NewGuid(), "Not administrator", invoiceId: invoiceId)));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, otherView.AccountId, null, false, Guid.NewGuid(), "Wrong account", invoiceId: invoiceId)));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, view.Groups.Single().Id, false, Guid.NewGuid(), "Conflicting purpose", invoiceId: invoiceId)));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, null, false, Guid.NewGuid(), "Not a one-off", invoiceId: view.Invoices[0].Invoice.Id)));
        var key = Guid.NewGuid();
        foreach (var invalidAccount in new[] { 0L, -1L, long.MaxValue })
        {
            var error = await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
                .AssignReceipt(board, receipt, invalidAccount, null, false, key, "Verified workshop payment", invoiceId: invoiceId)));

            Assert.Equal("Choose an existing financial account ID.", error.Message);
        }
        await using var db = Context();

        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.MoneyMovements.ToListAsync());
        Assert.False(await db.BillingOperations.AnyAsync(x => x.Id == key));
        async Task Assign(long? target) => await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, null, false, key, "Verified workshop payment", invoiceId: target));
        await Assign(invoiceId);
        await Assign(invoiceId);

        await Assert.ThrowsAsync<ValidationException>(() => Assign(null));
        Assert.Equal(receipt, await Pay(app, "missing-reference", 12.50m, new(2026, 6, 1), "workshop"));
        var after = await View(app, board, id);

        Assert.Equal(12.50m, after.Invoices.Single(x => x.Invoice.Id == invoiceId).Paid);
        Assert.Equal(0m, after.Invoices.Single(x => x.Invoice.Id == view.Invoices[0].Invoice.Id).Paid);
        Assert.Equal(new Balances(0, 0, 0), after.Balances);
        Assert.Empty(after.Coverage);
        Assert.Equal("missing-reference", (await db.Receipts.AsNoTracking().SingleAsync()).Reference);
        var reconciliation = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board));

        Assert.Equal(0m, reconciliation.Difference);
        Assert.Empty(reconciliation.InvalidLines);
        Assert.Empty(reconciliation.InvalidReceipts);
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
        Assert.True(view.Membership.IsRenewalGrace);
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
    [Trait("Acceptance", "SVC")]
    public async Task Membership_lapse_does_not_freeze_paid_services_and_service_expiry_works_without_a_renewal_invoice()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var membershipReference = view.Invoices[0].Reference;
        await Pay(app, membershipReference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 3, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(6, 20);
        await Scan(app, account);
        On(7, 9);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.False(view.Membership.Active);
        Assert.False(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Equal(new DateOnly(2026, 9, 1), view.Coverage.Where(x => x.Benefit == Benefit.Service).Max(x => x.EndsOn));
        On(8, 15);
        await Pay(app, membershipReference, 10);
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        // No intervening scan issues a renewal; dated eligibility still expires both benefits.
        On(9, 9);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.Empty(view.Subscriptions);
        Assert.Equal(new DateOnly(2026, 9, 1), view.Coverage.Where(x => x.Benefit == Benefit.Service).Max(x => x.EndsOn));
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task Reviewed_unspent_receipt_reversal_and_reassignment_preserve_source_and_reconcile_without_duplicate_cash()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");
        var view = await View(app, board, id);
        var target = await View(app, board, other);
        var receipt = await Pay(app, view.Invoices[0].Reference, 5, sourceId: "mistaken-allocation");
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().UnassignReceipt(board, view.AccountId, receipt, Guid.NewGuid(), "Wrong member reference"));

        Assert.Equal(new Balances(0, 0, 0), (await View(app, board, id)).Balances);
        var key = Guid.NewGuid();
        var group = target.Groups.Single().Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, group, false, key, "Verified member and purpose"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, group, false, key, "Verified member and purpose"));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, null, false, key, "Verified member and purpose")));
        var report = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board));

        Assert.Equal(5m, report.Receipts);
        Assert.Equal(5m, report.Held);
        Assert.Equal(0m, report.Difference);
        Assert.Empty(report.InvalidLines);
        Assert.Empty(report.InvalidReceipts);
        await using var db = Context();

        Assert.Equal(view.Invoices[0].Reference, (await db.Receipts.SingleAsync()).Reference);
        Assert.Single(await db.MoneyMovements.Where(x => x.ReversesMovementId != null).ToListAsync());
        var csv = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Export(board, new(2026, 6, 1), new(2026, 7, 1)));

        Assert.Contains("BillingReceiptReversal", csv);
        Assert.Contains("mistaken-allocation", csv);
    }

    [Fact]
    [Trait("Acceptance", "RACE")]
    public async Task Receipt_matching_racing_deletion_has_no_half_purchase_or_lost_claim()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Task.WhenAll(Pay(app, view.Invoices[0].Reference, 15, sourceId: "race"),
            Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Concurrent termination")));
        await using var db = Context();
        var receipt = await db.Receipts.SingleAsync();

        Assert.NotNull((await db.Users.SingleAsync(x => x.Id == id)).PendingDeletionAt);
        var current = await View(app, board, id);
        if (receipt.State == ReceiptState.Posted)
        {

            Assert.Equal(5m, current.Balances.BillingCredit);
            Assert.Single(current.Coverage);
        }
        else
        {

            Assert.Equal(ReceiptState.Review, receipt.State);
            Assert.Equal(new Balances(0, 0, 0), current.Balances);
            Assert.Empty(current.Coverage);
        }

        Assert.DoesNotContain(current.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Zero_price_catchup_survives_missed_scans_and_price_changes_do_not_rewrite_issued_quotes()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, 1, new(2026, 6, 1), 20));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 6, 1), new(3026, 6, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Discount original quote"));

        Assert.Equal(5m, (await View(app, board, id)).Invoices[0].Total);
        var firstDiscount = (await View(app, board, id)).Discounts.Single().Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, firstDiscount, Guid.NewGuid(), "Honorary instead"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 6, 1), new(3026, 6, 1), DiscountKind.Percentage, 100, Guid.NewGuid(), "Honorary"));
        On(9, 10);
        await Scan(app, account);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Equal(4, view.Coverage.Count);
        Assert.Empty(view.Movements);
        await using var db = Context();

        Assert.Empty(await db.Receipts.ToListAsync());
    }

    [Fact]
    [Trait("Acceptance", "MAIL")]
    public async Task Invoice_render_failure_moves_the_delivery_to_review_instead_of_retrying()
    {
        Settings["Billing:Iban"] = "FI2112345600000785";
        var templates = Path.Combine(DirectoryPath, "broken-invoice");
        Directory.CreateDirectory(templates);
        await File.WriteAllTextAsync(Path.Combine(templates, "lasku.typ"), "#panic(\"broken template\")");
        Settings["Billing:TemplateDirectory"] = templates;
        On(6, 1);
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        await using var db = Context();
        var delivery = await db.Deliveries.SingleAsync(x => x.UserId == id && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.Empty(transport.Sent);
        Assert.Equal(DeliveryState.Review, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == delivery.Id)).State);
    }

    [Fact]
    public async Task Typst_renders_generic_barcode_invoice_and_mail_checks_revision_preference_and_ownership()
    {
        Settings["Billing:Iban"] = "FI2112345600000785";
        On(6, 1);
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");
        var profile = Input().Profile;
        profile.Name = "Literal #panic(\"not executable\")";
        await Run(app, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().UpdateProfile(Actor(id), id, profile));
        var view = await View(app, board, id);
        var invoice = view.Invoices[0].Invoice.Id;
        var pdf = await Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), invoice));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf[..8]));
        Assert.True(pdf.Length > 1000);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(other), invoice)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), long.MaxValue)));
        await using var db = Context();
        var delivery = await db.Deliveries.SingleAsync(x => x.InvoiceId == invoice && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.NotNull(Assert.Single(transport.Sent).Pdf);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.Single(transport.Sent);
        var otherView = await View(app, board, other);
        var optedOut = await db.Deliveries.SingleAsync(x => x.UserId == other && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetEmailInvoices(Actor(other), otherView.AccountId, false, Guid.NewGuid()));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(optedOut.Id));

        Assert.Equal(DeliveryState.Cancelled, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == optedOut.Id)).State);
        var service = await Service(app, board);
        var waiting = await Admit(app, board, "stale@example.test");
        var waitingView = await View(app, board, waiting);
        var stale = await db.Deliveries.SingleAsync(x => x.UserId == waiting && x.Kind == MailKind.Invoice);
        // A queued revision must not send the old payable quote after an invoice edit.
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, waitingView.AccountId, service, 1, false, Guid.NewGuid()));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(stale.Id));

        Assert.Equal(DeliveryState.Cancelled, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == stale.Id)).State);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task Deployment_template_receives_structured_data_assets_and_association_local_issue_date()
    {
        Clock.Now = new DateTimeOffset(2026, 6, 1, 22, 30, 0, TimeSpan.Zero);
        var templates = Path.Combine(DirectoryPath, "trusted-invoice");
        Directory.CreateDirectory(templates);
        await File.WriteAllTextAsync(Path.Combine(templates, "heading.typ"), "Trusted deployment invoice");
        await File.WriteAllTextAsync(Path.Combine(templates, "lasku.typ"), """
            #let invoice = json(sys.inputs.at("invoice"))
            #assert(invoice.date == "2026-06-02")
            #assert(invoice.items.len() == 1 and invoice.remaining_cents == 1000)
            #include "heading.typ"
            #invoice.number
            """);
        Settings["Billing:TemplateDirectory"] = templates;
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var pdf = await Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), view.Invoices[0].Invoice.Id));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf[..8]));
    }

    [Fact]
    [Trait("Acceptance", "CAL")]
    public async Task Interval_selection_preserves_issued_quotes_and_bridges_next_unissued_period_to_original_phase()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var before = await View(app, board, id);
        var invoice = before.Invoices[0];
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ChangeInterval(Actor(id), before.AccountId, before.Items.Single().Id, 3, Guid.NewGuid()));
        var after = await View(app, board, id);

        Assert.Equal(invoice.Reference, after.Invoices[0].Reference);
        Assert.Equal(invoice.Total, after.Invoices[0].Total);
        Assert.Equal(invoice.Invoice.Revision, after.Invoices[0].Invoice.Revision);
        await Pay(app, invoice.Reference, 10);
        On(6, 20);
        await Scan(app, before.AccountId);
        var next = Assert.Single((await View(app, board, id)).Invoices, x => x.Invoice.ClosedAt is null);
        var line = Assert.Single(next.Lines).Line;

        Assert.Equal(new DateOnly(2026, 7, 1), line.StartsOn);
        Assert.Equal(new DateOnly(2026, 9, 1), line.EndsOn);
        Assert.NotEqual(invoice.Reference, next.Reference);
        await Pay(app, next.Reference, next.Remaining);

        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "DEV")]
    public async Task Database_rejects_competing_open_invoices_current_subscriptions_and_invalid_money_facts()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        await using var db = Context();
        async Task Reject(FormattableString sql, string constraint)
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql));

            Assert.Equal(constraint, error.ConstraintName);
        }
        var invoice = view.Invoices[0].Invoice;
        await Reject($"INSERT INTO \"Invoices\" (\"BillingAccountId\", \"BillingGroupId\", \"IssuedAt\", \"Revision\", \"NotifiedRevision\") VALUES ({view.AccountId}, {invoice.BillingGroupId}, {Clock.Now}, 1, 0)", "IX_Invoices_BillingGroupId");
        await Reject($"INSERT INTO \"ServiceEnrollments\" (\"UserId\", \"ServiceDefinitionId\", \"StartedAt\") VALUES ({id}, {service}, {Clock.Now})", "IX_ServiceEnrollments_UserId_ServiceDefinitionId");
        var operation = await db.BillingOperations.FirstAsync();
        await Reject($"INSERT INTO \"MoneyMovements\" (\"BillingAccountId\", \"OperationId\", \"Kind\", \"Amount\", \"EffectiveOn\") VALUES ({view.AccountId}, {operation.Id}, 'BillingRefund', -1, {new DateOnly(2026, 6, 1)})", "CK_Movement_Amount");
        await Reject($"INSERT INTO \"MoneyMovements\" (\"BillingAccountId\", \"OperationId\", \"Kind\", \"Amount\", \"EffectiveOn\") VALUES ({view.AccountId}, {operation.Id}, 'ReceiptToBilling', 1, {new DateOnly(2026, 6, 1)})", "CK_Movement_Receipt");
        await Reject($"INSERT INTO \"MoneyMovements\" (\"BillingAccountId\", \"OperationId\", \"Kind\", \"Amount\", \"EffectiveOn\") VALUES ({view.AccountId}, {operation.Id}, 'LinePurchase', 1, {new DateOnly(2026, 6, 1)})", "CK_Movement_Line");

        Assert.Empty(await db.MoneyMovements.ToListAsync());
    }

    [Fact]
    [Trait("Acceptance", "RACE")]
    public async Task Receipt_racing_service_removal_either_completes_before_removal_or_preserves_unspent_credit()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board);
        var enrollment = await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        await Task.WhenAll(Pay(app, view.Invoices[0].Reference, 30),
            Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(board, view.AccountId, enrollment!.Value, true, Guid.NewGuid(), "Concurrent explicit removal")));
        var after = await View(app, board, id);

        Assert.Empty(after.Subscriptions);
        Assert.Equal(0m, after.Balances.Held);
        var line = Assert.Single(after.Invoices.SelectMany(x => x.Lines), x => x.Line.Benefit == Benefit.Service).Line;
        if (line.State == LineState.Fulfilled)
        {

            Assert.Equal(0m, after.Balances.BillingCredit);
            Assert.Contains(after.Coverage, x => x.Benefit == Benefit.Service);
        }
        else
        {

            Assert.Equal(LineState.Cancelled, line.State);
            Assert.Equal(30m, after.Balances.BillingCredit);
            Assert.DoesNotContain(after.Coverage, x => x.Benefit == Benefit.Service);
        }

        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
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
