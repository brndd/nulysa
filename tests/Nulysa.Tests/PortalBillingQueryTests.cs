using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;

namespace Nulysa.Tests;

public sealed class PortalBillingQueryTests : BoardTest
{
    [Fact]
    public async Task Member_projection_matches_the_existing_financial_evaluator_and_pages_histories_independently()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var full = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "bank", "member-paid", 10, "EUR", new(2026, 6, 1), full.Invoices[0].Reference)));
        for (var n = 0; n < 12; n++)
            await Run(commands, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, full.AccountId, Guid.NewGuid(), $"Purchase {n}", "misc", 1, new(2026, 6, 15)));
        for (var n = 0; n < 12; n++)
        {
            var invoice = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, full.AccountId, Guid.NewGuid(), $"Cancelled {n}", "misc", 1, new(2026, 6, 15)));
            await Run(commands, sp => sp.GetRequiredService<BillingCommands>().CancelInvoice(board, full.AccountId, invoice!.Value, Guid.NewGuid(), "Superseded synthetic request"));
        }
        for (var n = 0; n < 13; n++)
            await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "bank", $"deposit-{n}", 1, "EUR", new DateOnly(2026, 5, 15).AddDays(n % 3), full.DepositReference)));
        full = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id, take: 5, newestFirst: true)))!;
        var page = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Portal(Actor(id), pageSize: 5, history: true)))!;
        Assert.Equal(full.Balances, page.Balances);
        Assert.Equal(full.Membership, page.Membership);
        Assert.Equal(full.Forecasts, page.Forecasts);
        Assert.Empty(page.Movements);
        Assert.Empty(page.History);
        Assert.Equal(13, page.PreviousBillCount);
        Assert.Equal(5, page.Invoices.Count(invoice => invoice.Invoice.ClosedAt != null || invoice.Remaining == 0));
        Assert.Equal(12, page.Invoices.Count(invoice => invoice.Invoice.ClosedAt == null && invoice.Remaining > 0));
        foreach (var statement in page.Statements)
        {
            var expected = full.Statement(statement.Balance);
            Assert.Equal(expected.OpeningBalance, statement.OpeningBalance);
            Assert.Equal(expected.ClosingBalance, statement.ClosingBalance);
            Assert.Equal(expected.HasMore, statement.HasMore);
            Assert.Equal(expected.Lines, statement.Lines);
        }
        var older = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Portal(Actor(id), transactionsPage: 2, pageSize: 5, history: true)))!;
        var olderFull = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id, skip: 5, take: 5, newestFirst: true)))!;
        Assert.Equal(olderFull.Statement(BalanceKind.PurchaseBalance).Lines, older.Statement(BalanceKind.PurchaseBalance).Lines);
        Assert.Equal(page.Invoices.Select(invoice => invoice.Invoice.Id), older.Invoices.Select(invoice => invoice.Invoice.Id));
        var otherBills = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Portal(Actor(id), billsPage: 2, pageSize: 5, history: true)))!;
        Assert.Equal(page.Statement(BalanceKind.PurchaseBalance).Lines, otherBills.Statement(BalanceKind.PurchaseBalance).Lines);
        Assert.Equal(12, otherBills.Invoices.Count(invoice => invoice.Invoice.ClosedAt == null && invoice.Remaining > 0));
        Assert.NotEqual(page.Invoices.Where(invoice => invoice.Invoice.ClosedAt != null).Select(invoice => invoice.Invoice.Id), otherBills.Invoices.Where(invoice => invoice.Invoice.ClosedAt != null).Select(invoice => invoice.Invoice.Id));
        var bill = full.Invoices.First();
        var detail = await Run(commands, sp => sp.GetRequiredService<BillingQueries>().InvoiceForMember(Actor(id), bill.Invoice.Id));
        Assert.Equal(bill.Reference, detail.Reference);
        Assert.Equal(bill.Lines.Select(row => (row.Line.Id, row.Held, row.Remaining)), detail.Lines.Select(row => (row.Line.Id, row.Held, row.Remaining)));
        var summary = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Portal(Actor(id))))!;
        Assert.Empty(summary.Statements);
        Assert.All(summary.Invoices, invoice => Assert.True(invoice.Remaining > 0));
        Assert.Equal(full.Membership, summary.Membership);
        await using var db = Context();
        Assert.Equal(13m, (await db.MoneyMovements.Where(movement => movement.BillingAccountId == full.AccountId && movement.Kind == MovementKind.ReceiptToPurchase).SumAsync(movement => movement.Amount)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(commands, sp => sp.GetRequiredService<BillingQueries>().Portal(Actor(999999))));
    }
    [Fact]
    public async Task Projection_keeps_the_evaluators_renewal_witnesses_after_a_long_coverage_gap()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var original = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var service = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, null,
            new("witness-service", "Witness service", true, 0, 7, 10, "service")));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(fee => fee.ServiceDefinitionId == service);
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2026, 6, 1), 10));
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Subscribe(Actor(id), original.AccountId, service, 1, false, Guid.NewGuid()));
        // Synthetic read-model fixture: a long-expired renewal followed by independently granted coverage.
        var enrollment = await db.ServiceEnrollments.SingleAsync(enrollment => enrollment.UserId == id);
        enrollment.FirstPaidOn = new(2026, 6, 1);
        var line = await db.InvoiceLines.SingleAsync(line => line.ServiceEnrollmentId == enrollment.Id);
        line.Purpose = PurchasePurpose.Renewal;
        line.State = LineState.Expired;
        var operation = await db.BillingOperations.Where(operation => operation.BillingAccountId == original.AccountId).Select(operation => operation.Id).FirstAsync();
        db.Coverage.AddRange(
            new Coverage { BillingAccountId = original.AccountId, Benefit = Benefit.Membership, StartsOn = new(2028, 6, 1), EndExclusive = new(2028, 8, 1), Source = CoverageSource.AdministratorGrant, OperationId = operation, Origin = "synthetic membership grant" },
            new Coverage { BillingAccountId = original.AccountId, Benefit = Benefit.Service, ServiceDefinitionId = service, StartsOn = new(2028, 6, 1), EndExclusive = new(2028, 7, 1), Source = CoverageSource.AdministratorGrant, OperationId = operation, Origin = "synthetic service grant" });
        await db.SaveChangesAsync();
        Clock.Now = new(2028, 7, 2, 10, 0, 0, TimeSpan.Zero);
        var expected = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var actual = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Portal(Actor(id))))!;
        Assert.Equal(expected.Membership, actual.Membership);
        Assert.Equal(StandingState.AwaitingPayment, Assert.Single(expected.Subscriptions).Standing.State);
        Assert.Equal(Assert.Single(expected.Subscriptions).Standing, Assert.Single(actual.Subscriptions).Standing);
    }

}
