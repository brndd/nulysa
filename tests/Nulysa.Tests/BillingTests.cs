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
    [Trait("Acceptance", "BILL")]
    public async Task Approval_partial_membership_first_funding_independent_fulfilment_and_cancellation_conserve_every_euro()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var initial = await View(app, Actor(id), id); var account = initial.AccountId;
        Assert.True(initial.Membership.Active); Assert.Equal(10m, Assert.Single(initial.Invoices).Remaining);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        var bill = Assert.Single((await View(app, board, id)).Invoices); Assert.Equal(40m, bill.Total);
        var receiptId = await Pay(app, bill.Reference, 15m, sourceId: "duplicate-safe");
        Assert.Equal(receiptId, await Pay(app, bill.Reference, 15m, sourceId: "duplicate-safe"));
        var partial = await View(app, board, id); bill = Assert.Single(partial.Invoices);
        Assert.Equal(10m, bill.Paid); Assert.Equal(5m, bill.Held); Assert.Equal(25m, bill.Remaining);
        Assert.Single(partial.Coverage); Assert.Equal(Benefit.Membership, partial.Coverage[0].Benefit);
        Assert.Equal(new Balances(0, 0, 5), partial.Balances);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().CancelInvoice(board, account, bill.Invoice.Id, Guid.NewGuid(), "Cancel remaining requests"));
        var cancelled = await View(app, board, id);
        Assert.Equal(new Balances(5, 0, 0), cancelled.Balances); Assert.Single(cancelled.Coverage);
        Assert.NotNull(Assert.Single(cancelled.Invoices).Invoice.ClosedAt);
        await using var db = Context();
        Assert.Equal(15m, await db.Receipts.Where(x => x.State == ReceiptState.Posted).SumAsync(x => x.Amount));
        Assert.Equal(10m, await db.MoneyMovements.Where(x => x.Kind == MovementKind.LinePurchase).SumAsync(x => x.Amount));
        Assert.Equal(5m, await db.MoneyMovements.Where(x => x.Kind == MovementKind.LineToBilling).SumAsync(x => x.Amount));
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Timely_delayed_renewal_restores_original_period_after_timeout_but_genuine_return_is_prorated()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId; var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10); On(6, 20); await Scan(app, account);
        On(7, 9); await Scan(app, account); Assert.False((await View(app, board, id)).Membership.Active);
        await Pay(app, reference, 10, new(2026, 7, 7));
        view = await View(app, board, id);
        Assert.True(view.Membership.Active);
        Assert.Contains(view.Coverage, x => x.StartsOn == new DateOnly(2026, 7, 1) && x.EndsOn == new DateOnly(2026, 8, 1));
        var renewal = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 7, 1)).Line;
        Assert.Equal(new DateOnly(2026, 7, 7), renewal.EffectiveOn); Assert.Equal(10m, renewal.Amount);
        On(7, 20); await Scan(app, account); On(8, 9); await Scan(app, account);
        On(9, 15); await Pay(app, reference, 10);
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
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var initial = await View(app, board, id); var account = initial.AccountId; var reference = initial.Invoices[0].Reference;
        On(6, 20); await Scan(app, account);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));
        if (changePrice) await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, 1, new(2026, 6, 20), 20));
        On(6, 21); await Pay(app, reference, 10, new(2026, 6, 18));
        var view = await View(app, board, id);
        var line = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Resumption).Line;
        Assert.Equal(LineState.Fulfilled, line.State); Assert.Equal(4.33m, line.Amount);
        Assert.Equal(new DateOnly(2026, 6, 18), line.StartsOn); Assert.Equal(new DateOnly(2026, 7, 1), line.EndsOn);
        Assert.Equal(new DateOnly(2026, 6, 18), line.EffectiveOn); Assert.Equal(5.67m, view.Balances.BillingCredit);
        Assert.Equal(new DateOnly(2026, 6, 18), view.Coverage.Min(x => x.StartsOn));
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "DATE")]
    public async Task Backdated_return_completion_resumes_billing_at_the_purchased_boundary(bool splitPayment)
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var initial = await View(app, board, id); var account = initial.AccountId; var reference = initial.Invoices[0].Reference;
        On(7, 2); await Scan(app, account);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));
        if (splitPayment)
        {
            await Run(app, sp => sp.GetRequiredService<BillingCommands>().ChangeInterval(Actor(id), account, initial.Items.Single().Id, 3, Guid.NewGuid()));
            await Pay(app, reference, 1, new(2026, 6, 25));
        }
        On(7, 3); await Pay(app, reference, splitPayment ? 9 : 10, new(2026, 6, splitPayment ? 24 : 25));
        var view = await View(app, board, id);
        var returned = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Purpose == PurchasePurpose.Resumption).Line;
        Assert.Equal(LineState.Fulfilled, returned.State); Assert.Equal(2m, returned.Amount);
        Assert.Equal(new DateOnly(2026, 6, 25), returned.EffectiveOn);
        Assert.Equal(new DateOnly(2026, 7, 1), Assert.Single(view.Items).NextOn);
        await Scan(app, account); await Scan(app, account);
        view = await View(app, board, id);
        var renewal = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);
        Assert.Equal(new DateOnly(2026, 7, 1), renewal.Line.StartsOn);
        Assert.Equal(new DateOnly(2026, splitPayment ? 9 : 8, 1), renewal.Line.EndsOn); Assert.Equal(8m, renewal.Held);
        await Pay(app, view.Invoices.Single(x => x.Invoice.Id == renewal.Line.InvoiceId).Reference, renewal.Remaining);
        On(7, 8); await Scan(app, account); view = await View(app, board, id);
        Assert.True(view.Membership.Active); Assert.Equal(renewal.Line.EndsOn, view.Items.Single().NextOn);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "DATE")]
    public async Task Initial_service_uses_last_required_receipt_date_including_out_of_order_and_never_reprices_on_a_timer()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId; var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10); var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        On(6, 6); await Pay(app, reference, 5);
        On(6, 10); await Scan(app, account);
        var before = (await View(app, board, id)).Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service);
        Assert.Equal(30m, before.Line.Amount); Assert.Equal(5m, before.Held); Assert.Equal(LineState.Open, before.Line.State);
        await Pay(app, reference, 20, new(2026, 6, 4));
        view = await View(app, board, id);
        var line = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service).Line;
        Assert.Equal(25m, line.Amount); Assert.Equal(new DateOnly(2026, 6, 6), line.StartsOn); Assert.Equal(new DateOnly(2026, 6, 6), line.EffectiveOn);
        Assert.Equal(LineState.Fulfilled, line.State); Assert.Equal(new Balances(0, 0, 0), view.Balances);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(10, 1550)]
    [Trait("Acceptance", "ADJ")]
    public async Task Delayed_initial_service_payment_keeps_approved_discounts_across_rollover_and_future_free_coverage(int freeDays, int expectedCents)
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var initial = await View(app, board, id); var account = initial.AccountId; var reference = initial.Invoices[0].Reference;
        await Pay(app, reference, 10); var service = await Service(app, board, free: freeDays);
        On(6, 20); await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        await using var db = Context(); var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service);
        On(6, 21); await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, fee.Id,
            new(2026, 6, 21), new(2026, 8, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Approved service discount"));
        On(7, 2); await Scan(app, account); await Pay(app, reference, 50, new(2026, 6, 25));
        var view = await View(app, board, id);
        var line = Assert.Single(view.Invoices.SelectMany(x => x.Lines), x => x.Line.Benefit == Benefit.Service).Line;
        Assert.Equal(LineState.Fulfilled, line.State); Assert.Equal(expectedCents / 100m, line.Amount);
        Assert.Equal(new DateOnly(2026, 6, freeDays == 0 ? 25 : 30), line.StartsOn);
        Assert.Equal(new DateOnly(2026, 6, 25), line.EffectiveOn);
        Assert.Contains(await db.LineAdjustments.AsNoTracking().ToListAsync(), x => x.InvoiceLineId == line.Id);
        Assert.Equal(line.EndsOn, view.Items.Single(x => x.ServiceEnrollmentId is not null).NextOn);
        await Scan(app, account); await Scan(app, account);
        On(7, 8); await Scan(app, account); view = await View(app, board, id);
        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.EndsOn == new DateOnly(2026, 8, 1));
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Free_days_are_once_only_and_independent_from_payment_window_and_stopping_preserves_coverage()
    {
        On(3, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId;
        await Pay(app, view.Invoices[0].Reference, 10); var service = await Service(app, board, free: 20);
        On(3, 10); var key = Guid.NewGuid();
        var enrollment = await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, key));
        Assert.Equal(enrollment, await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, key)));
        view = await View(app, board, id);
        var grant = Assert.Single(view.Coverage, x => x.Source == CoverageSource.InitialFree);
        Assert.Equal(new DateOnly(2026, 3, 30), grant.EndsOn);
        var serviceLine = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service).Line;
        Assert.Equal(new DateOnly(2026, 3, 30), serviceLine.StartsOn); Assert.Equal(new DateOnly(2026, 5, 1), serviceLine.EndsOn);
        Assert.Equal(new DateOnly(2026, 3, 24), serviceLine.ExpiresOn);
        On(3, 24); await Scan(app, account); view = await View(app, board, id);
        Assert.True(Assert.Single(view.Subscriptions).Standing.Active); Assert.Equal(LineState.Expired, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == serviceLine.Id).Line.State);
        On(3, 30); await Scan(app, account); Assert.Empty((await View(app, board, id)).Subscriptions);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id); Assert.Single(view.Coverage, x => x.Source == CoverageSource.InitialFree);
        Assert.False(Assert.Single(view.Subscriptions).Standing.Active);
        var current = view.Subscriptions[0].Enrollment.Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(board, account, current, true, Guid.NewGuid(), "Explicit removal"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, true, Guid.NewGuid()));
        view = await View(app, board, id); Assert.Single(view.Coverage, x => x.Source == CoverageSource.AdministratorGrant);
        current = view.Subscriptions[0].Enrollment.Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(Actor(id), account, current, false, Guid.NewGuid(), "Stop renewal"));
        view = await View(app, board, id); Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.All(view.Invoices.SelectMany(x => x.Lines).Where(x => x.Line.Benefit == Benefit.Service), x => Assert.NotEqual(LineState.Open, x.Line.State));
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Discount_grant_and_honorary_coverage_release_only_unfinished_funds_without_fictitious_money()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId; var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10); On(6, 20); await Scan(app, account); await Pay(app, reference, 5);
        var grantKey = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().GrantMembershipMonths(board, account, 1, grantKey, "Volunteer compensation"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().GrantMembershipMonths(board, account, 1, grantKey, "Volunteer compensation"));
        view = await View(app, board, id); Assert.Equal(5m, view.Balances.BillingCredit);
        Assert.Single(view.Coverage, x => x.Source == CoverageSource.AdministratorGrant);
        Assert.DoesNotContain(view.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 8, 1), new(3026, 8, 1), DiscountKind.Percentage, 100, Guid.NewGuid(), "Honorary membership"));
        On(7, 20); await Scan(app, account); view = await View(app, board, id);
        var honorary = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 8, 1)).Line;
        Assert.Equal(LineState.Fulfilled, honorary.State); Assert.Equal(0, honorary.Amount); Assert.Null(honorary.EffectiveOn);
        Assert.Equal(5m, view.Balances.BillingCredit);
        await using var db = Context(); Assert.Equal(2, await db.Receipts.CountAsync()); Assert.DoesNotContain(await db.MoneyMovements.ToListAsync(), x => x.Amount <= 0);
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Completed_future_discount_is_a_reviewed_credit_preserving_original_purchase_and_coverage()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId; var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10); On(6, 20); await Scan(app, account); await Pay(app, reference, 10);
        view = await View(app, board, id); var line = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 7, 1)).Line;
        var discount = await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 7, 1), new(2026, 8, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Future discount"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().CreditFutureDiscount(board, account, line.Id, discount!.Value, 5, Guid.NewGuid(), "Reviewed July purchase"));
        view = await View(app, board, id);
        Assert.Equal(5m, view.Balances.BillingCredit); Assert.Equal(10m, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == line.Id).Line.Amount);
        Assert.Equal(2, view.Coverage.Count);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().CreditFutureDiscount(board, account, line.Id, discount!.Value, 5, Guid.NewGuid(), "Duplicate correction")));
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Future_discount_credits_compensate_distinct_dates_and_only_uncredited_parts_of_replacement_discounts()
    {
        Settings["Billing:DefaultIntervalMonths"] = "12";
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var initial = await View(app, board, id); var account = initial.AccountId; await Pay(app, initial.Invoices[0].Reference, 120);
        On(6, 20); var line = Assert.Single((await View(app, board, id)).Invoices.SelectMany(x => x.Lines)).Line;
        async Task<long> Discount(DateOnly start, DateOnly end, decimal percent) => (await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AddDiscount(board, account, 1, start, end, DiscountKind.Percentage, percent, Guid.NewGuid(), "Future discount")))!.Value;
        Task Credit(long discount, decimal amount) => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .CreditFutureDiscount(board, account, line.Id, discount, amount, Guid.NewGuid(), "Reviewed future purchase"));
        Task Withdraw(long discount) => Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, discount, Guid.NewGuid(), "Replacement discount"));
        var july = await Discount(new(2026, 7, 1), new(2026, 8, 1), 50);
        var august = await Discount(new(2026, 8, 1), new(2026, 9, 1), 50);
        await Credit(july, 5.10m); await Credit(august, 5.10m);
        Assert.Equal(10.20m, (await View(app, board, id)).Balances.BillingCredit);
        await Assert.ThrowsAsync<ValidationException>(() => Credit(july, 5.10m));
        await Withdraw(july); await Withdraw(august);
        var replacement = await Discount(new(2026, 7, 1), new(2026, 9, 1), 75);
        await Credit(replacement, 5.09m);
        await Assert.ThrowsAsync<ValidationException>(() => Credit(replacement, 5.09m));
        await Withdraw(replacement);
        var julyFree = await Discount(new(2026, 7, 1), new(2026, 8, 1), 100);
        await Credit(julyFree, 2.55m);
        var view = await View(app, board, id); Assert.Equal(17.84m, view.Balances.BillingCredit);
        Assert.Single(view.Coverage); Assert.Equal(120m, Assert.Single(view.Invoices.SelectMany(x => x.Lines)).Line.Amount);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "RACE")]
    public async Task Concurrent_transfers_cannot_double_spend_and_reused_keys_cannot_change_inputs()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true"; On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId;
        await Pay(app, view.DepositReference, 10);
        async Task<bool> Transfer()
        {
            try { await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, Guid.NewGuid(), 7, false)); return true; }
            catch (ValidationException) { return false; }
        }
        var results = await Task.WhenAll(Transfer(), Transfer()); Assert.Single(results, x => x);
        view = await View(app, board, id); Assert.Equal(new Balances(0, 3, 7), view.Balances);
        var key = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, key, 3, false));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, key, 3, false));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), account, key, 2, false)));
        view = await View(app, board, id); Assert.Equal(new Balances(0, 0, 0), view.Balances); Assert.Equal(10m, view.Invoices[0].Paid);
    }

    [Fact]
    [Trait("Acceptance", "ERASE")]
    public async Task Pending_deletion_receipts_stay_in_review_after_restoration_and_erasure_preserves_the_unspent_claim()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true"; On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId; var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 5); await Pay(app, view.DepositReference, 20);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Termination"));
        var receipt = await Pay(app, reference, 10, sourceId: "pending-deletion");
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, Guid.NewGuid(), "Verified request"));
        await Pay(app, reference, 10, sourceId: "pending-deletion");
        await using var db = Context(); Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == receipt)).State);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Second termination"));
        On(6, 15); Assert.True(await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(id)));
        var retained = await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == account);
        Assert.Null(retained.UserId); Assert.Equal("New Member", retained.ClaimName); Assert.Equal(Email, retained.ClaimEmail);
        var financial = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Account(board, account)); Assert.Equal(new Balances(5, 20, 0), financial.Balances);
        Assert.Empty(await db.RecurringItems.ToListAsync()); Assert.Empty(await db.ServiceEnrollments.ToListAsync());
        var rejoined = await Admit(app, board); Assert.NotEqual(account, (await View(app, board, rejoined)).AccountId);
        Clock.Now = DateTimeOffset.UtcNow;
        await using var web = await StartProvider(); using var client = Client(); await Login(client, "board@example.test");
        var retainedPage = await client.GetStringAsync("admin/billing/" + account);
        Assert.Contains("Retained invoice evidence", retainedPage); Assert.DoesNotContain("/admin/members//billing", retainedPage);
        Assert.DoesNotContain("New Member", retainedPage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "ERASE")]
    public async Task Restoring_paid_membership_resumes_renewal_without_reopening_cancelled_purchases_or_services(bool renewalAlreadyIssued)
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var initial = await View(app, board, id); var account = initial.AccountId; await Pay(app, initial.Invoices[0].Reference, 10);
        var service = await Service(app, board, free: 30);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        if (renewalAlreadyIssued) { On(6, 20); await Scan(app, account); }
        else On(6, 5);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Termination"));
        var cancelled = (await View(app, board, id)).Invoices.SelectMany(x => x.Lines).Where(x => x.Line.State == LineState.Cancelled).Select(x => x.Line.Id).ToList();
        On(6, 22); var restore = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, restore, "Verified restoration"));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, restore, "Verified restoration"));
        var restored = await View(app, board, id); Assert.True(restored.Membership.Active); Assert.Empty(restored.Subscriptions);
        Assert.Single(restored.Items); Assert.NotEqual(initial.Items.Single().Id, restored.Items[0].Id);
        Assert.DoesNotContain(restored.Invoices.SelectMany(x => x.Lines), x => cancelled.Contains(x.Line.Id) && x.Line.State != LineState.Cancelled);
        await Scan(app, account);
        var renewal = Assert.Single((await View(app, board, id)).Invoices.SelectMany(x => x.Lines), x => x.Line.Benefit == Benefit.Membership && x.Line.State == LineState.Open).Line;
        Assert.Equal(new DateOnly(2026, 7, 1), renewal.StartsOn); Assert.Equal(new DateOnly(2026, 8, 1), renewal.EndsOn);
        On(7, 1); Assert.True((await View(app, board, id)).Membership.Active);
        On(7, 8); await Scan(app, account); Assert.False((await View(app, board, id)).Membership.Active);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ResumeMembership(Actor(id), account, Guid.NewGuid()));
        Assert.Single((await View(app, board, id)).Items);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    public async Task One_off_reference_uses_the_same_funding_path_without_recurring_coverage_or_wallet_sweep()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId;
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, account, Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        view = await View(app, board, id); var one = view.Invoices.Single(x => x.Invoice.Id == invoiceId); Assert.Null(one.Invoice.BillingGroupId);
        await Pay(app, one.Reference, 15); view = await View(app, board, id);
        Assert.Equal(12.50m, view.Invoices.Single(x => x.Invoice.Id == invoiceId).Paid);
        Assert.Empty(view.Coverage); Assert.Equal(2.50m, view.Balances.Held); Assert.Equal(0, view.Balances.BillingCredit);
        var other = await Admit(app, board, "other@example.test");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().Account(Actor(other), account)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().Review(Actor(id))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "BILL")]
    public async Task Reviewed_receipt_targets_a_one_off_invoice_before_recurring_bills_with_ownership_and_retry_checks(bool delayed)
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var other = await Admit(app, board, "other@example.test"); var otherView = await View(app, board, other);
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        var receipt = await Pay(app, "missing-reference", 12.50m, new(2026, 6, 1), "workshop");
        if (delayed) { On(6, 21); await Scan(app, view.AccountId); }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(Actor(id), receipt, view.AccountId, null, false, Guid.NewGuid(), "Not administrator", invoiceId: invoiceId)));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, otherView.AccountId, null, false, Guid.NewGuid(), "Wrong account", invoiceId: invoiceId)));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, view.Groups.Single().Id, false, Guid.NewGuid(), "Conflicting purpose", invoiceId: invoiceId)));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, null, false, Guid.NewGuid(), "Not a one-off", invoiceId: view.Invoices[0].Invoice.Id)));
        await using var db = Context(); Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.MoneyMovements.ToListAsync());
        var key = Guid.NewGuid();
        async Task Assign(long? target) => await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, null, false, key, "Verified workshop payment", invoiceId: target));
        await Assign(invoiceId); await Assign(invoiceId);
        await Assert.ThrowsAsync<ValidationException>(() => Assign(null));
        Assert.Equal(receipt, await Pay(app, "missing-reference", 12.50m, new(2026, 6, 1), "workshop"));
        var after = await View(app, board, id);
        Assert.Equal(12.50m, after.Invoices.Single(x => x.Invoice.Id == invoiceId).Paid);
        Assert.Equal(0m, after.Invoices.Single(x => x.Invoice.Id == view.Invoices[0].Invoice.Id).Paid);
        Assert.Equal(new Balances(0, 0, 0), after.Balances); Assert.Empty(after.Coverage);
        Assert.Equal("missing-reference", (await db.Receipts.AsNoTracking().SingleAsync()).Reference);
        var reconciliation = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board));
        Assert.Equal(0m, reconciliation.Difference); Assert.Empty(reconciliation.InvalidLines); Assert.Empty(reconciliation.InvalidReceipts);
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Membership_lapse_does_not_freeze_paid_services_and_service_expiry_works_without_a_renewal_invoice()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId; var membershipReference = view.Invoices[0].Reference;
        await Pay(app, membershipReference, 10); var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 3, false, Guid.NewGuid()));
        view = await View(app, board, id); var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(6, 20); await Scan(app, account); On(7, 8); await Scan(app, account);
        view = await View(app, board, id); Assert.False(view.Membership.Active); Assert.False(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Equal(new DateOnly(2026, 9, 1), view.Coverage.Where(x => x.Benefit == Benefit.Service).Max(x => x.EndsOn));
        On(8, 15); await Pay(app, membershipReference, 10);
        view = await View(app, board, id); Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        // No intervening scan issues a renewal; dated eligibility still expires both benefits.
        On(9, 9); await Scan(app, account); view = await View(app, board, id);
        Assert.Empty(view.Subscriptions); Assert.Equal(new DateOnly(2026, 9, 1), view.Coverage.Where(x => x.Benefit == Benefit.Service).Max(x => x.EndsOn));
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task Reviewed_unspent_receipt_reversal_and_reassignment_preserve_source_and_reconcile_without_duplicate_cash()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board); var other = await Admit(app, board, "other@example.test");
        var view = await View(app, board, id); var target = await View(app, board, other);
        var receipt = await Pay(app, view.Invoices[0].Reference, 5, sourceId: "mistaken-allocation");
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().UnassignReceipt(board, view.AccountId, receipt, Guid.NewGuid(), "Wrong member reference"));
        Assert.Equal(new Balances(0, 0, 0), (await View(app, board, id)).Balances);
        var key = Guid.NewGuid(); var group = target.Groups.Single().Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, group, false, key, "Verified member and purpose"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, group, false, key, "Verified member and purpose"));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, null, false, key, "Verified member and purpose")));
        var report = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board));
        Assert.Equal(5m, report.Receipts); Assert.Equal(5m, report.Held); Assert.Equal(0m, report.Difference); Assert.Empty(report.InvalidLines); Assert.Empty(report.InvalidReceipts);
        await using var db = Context(); Assert.Equal(view.Invoices[0].Reference, (await db.Receipts.SingleAsync()).Reference);
        Assert.Single(await db.MoneyMovements.Where(x => x.ReversesMovementId != null).ToListAsync());
        var csv = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Export(board, new(2026, 6, 1), new(2026, 7, 1)));
        Assert.Contains("BillingReceiptReversal", csv); Assert.Contains("mistaken-allocation", csv);
    }

    [Fact]
    [Trait("Acceptance", "RACE")]
    public async Task Receipt_matching_racing_deletion_has_no_half_purchase_or_lost_claim()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Task.WhenAll(Pay(app, view.Invoices[0].Reference, 15, sourceId: "race"),
            Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Concurrent termination")));
        await using var db = Context(); var receipt = await db.Receipts.SingleAsync();
        Assert.NotNull((await db.Users.SingleAsync(x => x.Id == id)).PendingDeletionAt);
        var current = await View(app, board, id);
        if (receipt.State == ReceiptState.Posted) { Assert.Equal(5m, current.Balances.BillingCredit); Assert.Single(current.Coverage); }
        else { Assert.Equal(ReceiptState.Review, receipt.State); Assert.Equal(new Balances(0, 0, 0), current.Balances); Assert.Empty(current.Coverage); }
        Assert.DoesNotContain(current.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Zero_price_catchup_survives_missed_scans_and_price_changes_do_not_rewrite_issued_quotes()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var account = view.AccountId;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, 1, new(2026, 6, 1), 20));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 6, 1), new(3026, 6, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Discount original quote"));
        Assert.Equal(5m, (await View(app, board, id)).Invoices[0].Total);
        var firstDiscount = (await View(app, board, id)).Discounts.Single().Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, firstDiscount, Guid.NewGuid(), "Honorary instead"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 6, 1), new(3026, 6, 1), DiscountKind.Percentage, 100, Guid.NewGuid(), "Honorary"));
        On(9, 10); await Scan(app, account); await Scan(app, account);
        view = await View(app, board, id); Assert.True(view.Membership.Active); Assert.Equal(4, view.Coverage.Count);
        Assert.Empty(view.Movements); await using var db = Context(); Assert.Empty(await db.Receipts.ToListAsync());
    }

    [Fact]
    public async Task Typst_renders_generic_barcode_invoice_and_mail_checks_revision_preference_and_ownership()
    {
        Settings["Billing:Iban"] = "FI2112345600000785"; On(6, 1); var board = await Board(); var transport = new RecordingMail(); await using var app = CommandsApp(transport);
        var id = await Admit(app, board); var other = await Admit(app, board, "other@example.test");
        var profile = Input().Profile; profile.Name = "Literal #panic(\"not executable\")";
        await Run(app, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().UpdateProfile(Actor(id), id, profile));
        var view = await View(app, board, id); var invoice = view.Invoices[0].Invoice.Id;
        var pdf = await Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), invoice));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf[..8])); Assert.True(pdf.Length > 1000);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(other), invoice)));
        await using var db = Context(); var delivery = await db.Deliveries.SingleAsync(x => x.InvoiceId == invoice && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));
        Assert.NotNull(Assert.Single(transport.Sent).Pdf);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id)); Assert.Single(transport.Sent);
        var otherView = await View(app, board, other); var optedOut = await db.Deliveries.SingleAsync(x => x.UserId == other && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetEmailInvoices(Actor(other), otherView.AccountId, false, Guid.NewGuid()));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(optedOut.Id));
        Assert.Equal(DeliveryState.Cancelled, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == optedOut.Id)).State);
        var service = await Service(app, board); var waiting = await Admit(app, board, "stale@example.test");
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
        var templates = Path.Combine(DirectoryPath, "trusted-invoice"); Directory.CreateDirectory(templates);
        await File.WriteAllTextAsync(Path.Combine(templates, "heading.typ"), "Trusted deployment invoice");
        await File.WriteAllTextAsync(Path.Combine(templates, "lasku.typ"), """
            #let invoice = json(sys.inputs.at("invoice"))
            #assert(invoice.date == "2026-06-02")
            #assert(invoice.items.len() == 1 and invoice.remaining_cents == 1000)
            #include "heading.typ"
            #invoice.number
            """);
        Settings["Billing:TemplateDirectory"] = templates;
        var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id);
        var pdf = await Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), view.Invoices[0].Invoice.Id));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf[..8]));
    }

    [Fact]
    [Trait("Acceptance", "CAL")]
    public async Task Interval_selection_preserves_issued_quotes_and_bridges_next_unissued_period_to_original_phase()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var before = await View(app, board, id); var invoice = before.Invoices[0];
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ChangeInterval(Actor(id), before.AccountId, before.Items.Single().Id, 3, Guid.NewGuid()));
        var after = await View(app, board, id);
        Assert.Equal(invoice.Reference, after.Invoices[0].Reference); Assert.Equal(invoice.Total, after.Invoices[0].Total);
        Assert.Equal(invoice.Invoice.Revision, after.Invoices[0].Invoice.Revision);
        await Pay(app, invoice.Reference, 10); On(6, 20); await Scan(app, before.AccountId);
        var next = Assert.Single((await View(app, board, id)).Invoices, x => x.Invoice.ClosedAt is null);
        var line = Assert.Single(next.Lines).Line;
        Assert.Equal(new DateOnly(2026, 7, 1), line.StartsOn); Assert.Equal(new DateOnly(2026, 9, 1), line.EndsOn);
        Assert.NotEqual(invoice.Reference, next.Reference);
        await Pay(app, next.Reference, next.Remaining);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "DEV")]
    public async Task Database_rejects_competing_open_invoices_current_subscriptions_and_invalid_money_facts()
    {
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); var service = await Service(app, board);
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
        On(6, 1); var board = await Board(); await using var app = CommandsApp(); var id = await Admit(app, board);
        var view = await View(app, board, id); await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board);
        var enrollment = await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        await Task.WhenAll(Pay(app, view.Invoices[0].Reference, 30),
            Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(board, view.AccountId, enrollment!.Value, true, Guid.NewGuid(), "Concurrent explicit removal")));
        var after = await View(app, board, id); Assert.Empty(after.Subscriptions);
        Assert.Equal(0m, after.Balances.Held);
        var line = Assert.Single(after.Invoices.SelectMany(x => x.Lines), x => x.Line.Benefit == Benefit.Service).Line;
        if (line.State == LineState.Fulfilled) { Assert.Equal(0m, after.Balances.BillingCredit); Assert.Contains(after.Coverage, x => x.Benefit == Benefit.Service); }
        else { Assert.Equal(LineState.Cancelled, line.State); Assert.Equal(30m, after.Balances.BillingCredit); Assert.DoesNotContain(after.Coverage, x => x.Benefit == Benefit.Service); }
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    public async Task Unconsumed_S1_request_keeps_its_original_deadline_without_sending_an_expired_invoice()
    {
        On(6, 20); var user = await CreateUser(); await using var app = CommandsApp(); await using var db = Context();
        var approved = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        db.Members.Add(new() { UserId = user.Id, JoinedOn = new(2026, 6, 1) });
        var account = new BillingAccount { UserId = user.Id, CreatedAt = approved }; db.BillingAccounts.Add(account); await db.SaveChangesAsync();
        db.InitialMembershipRequests.Add(new() { BillingAccountId = account.Id, Anchor = new(2026, 6, 1), RequestedAt = approved }); await db.SaveChangesAsync();
        await Scan(app, account.Id); await Scan(app, account.Id);
        var view = await View(app, Actor(user.Id), user.Id); Assert.False(view.Membership.Active);
        var invoice = Assert.Single(view.Invoices); var line = Assert.Single(invoice.Lines).Line;
        Assert.Equal(LineState.Expired, line.State); Assert.Equal(new DateOnly(2026, 6, 15), line.ExpiresOn);
        Assert.NotNull(invoice.Invoice.ClosedAt); Assert.DoesNotContain(await db.Deliveries.ToListAsync(), x => x.Kind == MailKind.Invoice);
    }
}
