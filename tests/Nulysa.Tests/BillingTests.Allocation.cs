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
    [InlineData("credit")]
    [InlineData("group")]
    [InlineData("deposit")]
    [InlineData("invoice")]
    public async Task Typed_receipt_destinations_preserve_allocation_and_existing_operation_fingerprints(string purpose)
    {
        On(6, 1);
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .OneOff(board, view.AccountId, Guid.NewGuid(), "Workshop", "workshop", 5, new(2026, 6, 20)));
        var receiptId = await Pay(app, "unknown", 5);
        var key = Guid.NewGuid();
        const string reason = "Verified purpose";
        ReceiptDestination destination = purpose switch
        {
            "group" => new ReceiptDestination.RecurringGroup(view.Groups.Single().Id),
            "deposit" => new ReceiptDestination.PurchaseDeposit(),
            "invoice" => new ReceiptDestination.OneOffInvoice(invoiceId!.Value),
            _ => new ReceiptDestination.BillingCredit()
        };

        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receiptId, view.AccountId, destination, key, reason));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receiptId, view.AccountId, destination, key, reason));

        var after = await View(app, board, id);
        Assert.Equal(purpose == "deposit" ? new Balances(0, 5, 0)
            : purpose == "invoice" ? new Balances(0, 0, 0) : new Balances(0, 0, 5), after.Balances);
        Assert.Equal(purpose == "invoice" ? 5 : 0, after.Invoices.Single(invoice => invoice.Invoice.Id == invoiceId).Paid);
        await using var db = Context();
        Assert.Equal(ReceiptState.Posted, (await db.Receipts.SingleAsync(receipt => receipt.Id == receiptId)).State);
        Assert.Equal(0, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);

        // Keys saved before typed routing must still recognize the same administrator retry.
        long? accountId = view.AccountId;
        long? groupId = purpose == "group" ? view.Groups.Single().Id : null;
        var toDeposit = purpose == "deposit";
        object input = purpose == "invoice"
            ? new { receiptId, accountId, groupId, toDeposit, invoiceId }
            : new { receiptId, accountId, groupId, toDeposit };
        long? actor = Nulysa.Accounts.Authority.Id(board);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { input, actor, reason })));
        Assert.Equal(expectedHash, (await db.BillingOperations.SingleAsync(operation => operation.Id == key)).InputHash);
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task Balance_statements_reconcile_available_and_held_balances_and_keep_transfers_in_both()
    {
        On(6, 1);
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var other = await Admit(app, board, "statement-other@example.test");
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 15);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Transfer(Actor(id), view.AccountId, Guid.NewGuid(), 2, true));
        view = await View(app, board, id);

        Assert.Equal(new Balances(3, 2, 0), view.Balances);
        var reconciliation = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board));
        Assert.Equal(view.Balances, new Balances(reconciliation.BillingCredit, reconciliation.PurchaseBalance, reconciliation.Held));
        Assert.Equal(0m, reconciliation.Difference);
        Assert.Empty(reconciliation.InvalidLines);
        Assert.Empty(reconciliation.InvalidReceipts);

        var billing = view.Statement(BalanceKind.BillingCredit);
        var purchase = view.Statement(BalanceKind.PurchaseBalance);
        var held = view.Statement(BalanceKind.Held);

        Assert.Equal(view.Balances.BillingCredit, billing.ClosingBalance);
        Assert.Equal(view.Balances.PurchaseBalance, purchase.ClosingBalance);
        Assert.Equal(view.Balances.Held, held.ClosingBalance);
        Assert.Equal(billing.ClosingBalance, billing.Lines[^1].RunningBalance);
        Assert.Equal(purchase.ClosingBalance, purchase.Lines[^1].RunningBalance);
        Assert.Equal(held.ClosingBalance, held.Lines[^1].RunningBalance);
        Assert.All(billing.Lines, line =>
        {
            Assert.NotEqual(default, line.RecordedAt);
            Assert.False(string.IsNullOrWhiteSpace(line.Description));
        });
        var receiptCredit = Assert.Single(billing.Lines, x => x.Kind == MovementKind.ReceiptToBilling);
        Assert.NotNull(receiptCredit.ReceiptId);
        Assert.Contains(billing.Lines, x => x.Kind == MovementKind.BillingToPurchase && x.Amount == -2);
        Assert.Contains(purchase.Lines, x => x.Kind == MovementKind.BillingToPurchase && x.Amount == 2);
        Assert.DoesNotContain(billing.Lines, x => x.Kind == MovementKind.ReceiptToLine);
        Assert.Contains(held.Lines, x => x.Kind == MovementKind.ReceiptToLine && x.InvoiceLineId is not null);
        Assert.Contains(held.Lines, x => x.Kind == MovementKind.LinePurchase && x.Amount == -10);

        var filtered = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Statement(board, view.AccountId, BalanceKind.BillingCredit, from: new(2026, 6, 1), to: new(2026, 7, 2)));
        Assert.Equal(billing.ClosingBalance, filtered.ClosingBalance);
        var paged = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Statement(board, view.AccountId, BalanceKind.BillingCredit, skip: 1, take: 1));
        Assert.Single(paged.Lines);
        Assert.Equal(billing.Lines[0].Amount, paged.OpeningBalance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Run(app, sp => sp.GetRequiredService<BillingQueries>().Statements(Actor(other), view.AccountId)));
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
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, new ReceiptDestination.RecurringGroup(group), key, "Verified member and purpose"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, new ReceiptDestination.RecurringGroup(group), key, "Verified member and purpose"));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().AssignReceipt(board, receipt, target.AccountId, new ReceiptDestination.BillingCredit(), key, "Verified member and purpose")));
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

}
