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

}
