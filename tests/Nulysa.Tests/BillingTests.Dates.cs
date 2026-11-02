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
        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(view.Items).NextUnissuedPeriodStartsOn);
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
        Assert.Equal(renewal.Line.EndsOn, view.Items.Single().NextUnissuedPeriodStartsOn);
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
        Assert.Equal(line.EndsOn, view.Items.Single(x => x.ServiceEnrollmentId is not null).NextUnissuedPeriodStartsOn);
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

}
