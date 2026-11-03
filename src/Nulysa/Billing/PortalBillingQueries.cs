using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;

namespace Nulysa.Billing;

public sealed partial class BillingQueries
{
    // A member read path: history is paged in SQL, while balances remain aggregates of the complete journal.
    // BillingFacts' existing standing and pricing evaluators still decide the displayed state.
    public async Task<BillingView?> Portal(ClaimsPrincipal actor, int billsPage = 1, int transactionsPage = 1,
        int pageSize = 10, bool history = false, CancellationToken ct = default)
    {
        var id = Authority.Id(actor);
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        var account = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(account => account.UserId == id, ct);
        if (account is null)
            return null;
        pageSize = Math.Clamp(pageSize, 1, 100);
        billsPage = Math.Clamp(billsPage, 1, 1_000_000);
        transactionsPage = Math.Clamp(transactionsPage, 1, 1_000_000);
        var today = clock.Today;
        var accountInvoices = db.Invoices.AsNoTracking().Where(invoice => invoice.BillingAccountId == account.Id);
        var openIds = await accountInvoices.Where(invoice => invoice.ClosedAt == null && db.InvoiceLines.Any(line =>
            line.InvoiceId == invoice.Id && line.State == LineState.Open && line.Amount > 0)).Select(invoice => invoice.Id).ToListAsync(ct);
        var previous = accountInvoices.Where(invoice => !openIds.Contains(invoice.Id));
        var previousCount = history ? await previous.CountAsync(ct) : 0;
        // Clamp obsolete URLs to the last page rather than loading the entire history.
        billsPage = Math.Min(billsPage, Math.Max(1, (previousCount + pageSize - 1) / pageSize));
        var previousIds = history ? await previous.OrderByDescending(invoice => invoice.IssuedAt).ThenByDescending(invoice => invoice.Id)
            .Skip((billsPage - 1) * pageSize).Take(pageSize).Select(invoice => invoice.Id).ToListAsync(ct) : [];
        var displayedIds = openIds.Concat(previousIds).ToArray();
        var invoices = await accountInvoices.Where(invoice => displayedIds.Contains(invoice.Id)).ToListAsync(ct);
        var accountLines = db.InvoiceLines.AsNoTracking().Where(line => accountInvoices.Any(invoice => invoice.Id == line.InvoiceId));
        var lastFailure = await accountLines.Where(line => line.Benefit == Benefit.Membership && line.Purpose != PurchasePurpose.Resumption &&
                line.State != LineState.Fulfilled && line.PaymentCutoffExclusive <= today)
            .OrderByDescending(line => line.PaymentCutoffExclusive).ThenByDescending(line => line.Id).Select(line => (long?)line.Id).FirstOrDefaultAsync(ct);
        var previousMembershipEnd = await db.Coverage.Where(period => period.BillingAccountId == account.Id &&
                period.Benefit == Benefit.Membership && period.EndExclusive <= today)
            .OrderByDescending(period => period.EndExclusive).Select(period => (DateOnly?)period.EndExclusive).FirstOrDefaultAsync(ct);
        var serviceRenewals = accountLines.Where(line => line.ServiceEnrollmentId != null && line.StartsOn <= today &&
            (line.Purpose == PurchasePurpose.Renewal || line.Purpose == PurchasePurpose.Alignment) &&
            (line.State == LineState.Open || line.State == LineState.Expired) &&
            db.ServiceEnrollments.Any(enrollment => enrollment.Id == line.ServiceEnrollmentId && enrollment.EndedAt == null));
        // Keep the evaluator's exact renewal/cancellation witnesses even after long gaps in activity.
        var lines = await accountLines.Where(line => displayedIds.Contains(line.InvoiceId) || line.State == LineState.Open || line.Id == lastFailure ||
            (line.Benefit == Benefit.Membership && line.StartsOn == previousMembershipEnd && (line.State == LineState.Cancelled || line.State == LineState.Expired)) ||
            (serviceRenewals.Any(candidate => candidate.Id == line.Id) && !serviceRenewals.Any(other =>
                other.ServiceEnrollmentId == line.ServiceEnrollmentId && other.StartsOn > line.StartsOn))).ToListAsync(ct);
        var displayedLineIds = lines.Where(line => displayedIds.Contains(line.InvoiceId)).Select(line => line.Id).ToArray();
        var heldKinds = Kinds(BalanceKind.Held, 1);
        var releasedKinds = Kinds(BalanceKind.Held, -1);
        var holds = await db.MoneyMovements.AsNoTracking().Where(movement => movement.BillingAccountId == account.Id &&
                movement.InvoiceLineId != null && displayedLineIds.Contains(movement.InvoiceLineId.Value))
            .GroupBy(movement => movement.InvoiceLineId!.Value)
            .Select(group => new { Id = group.Key, Amount = group.Sum(movement => heldKinds.Contains(movement.Kind) ? movement.Amount : releasedKinds.Contains(movement.Kind) ? -movement.Amount : 0) })
            .ToDictionaryAsync(row => row.Id, row => row.Amount, ct);
        var totals = new Dictionary<BalanceKind, decimal>();
        foreach (var balance in Enum.GetValues<BalanceKind>())
            totals[balance] = await Journal(account.Id, balance).SumAsync(row => row.Delta, ct);
        var coverageQuery = db.Coverage.AsNoTracking().Where(period => period.BillingAccountId == account.Id);
        var coverage = await coverageQuery.Where(period => period.EndExclusive > today || !coverageQuery.Any(other =>
                other.Benefit == period.Benefit && other.ServiceDefinitionId == period.ServiceDefinitionId && other.EndExclusive <= today &&
                (other.EndExclusive > period.EndExclusive || (other.EndExclusive == period.EndExclusive && other.Id > period.Id))))
            .ToListAsync(ct);
        var facts = new BillingFacts
        {
            Account = account,
            User = await db.Users.AsNoTracking().SingleAsync(user => user.Id == id, ct),
            MembershipRenewalGraceDays = options.Value.MembershipRenewalGraceDays,
            Groups = await db.BillingGroups.AsNoTracking().Where(group => group.BillingAccountId == account.Id).ToListAsync(ct),
            Items = await db.RecurringItems.AsNoTracking().Where(item => item.EndedAt == null && db.BillingGroups.Any(group => group.Id == item.BillingGroupId && group.BillingAccountId == account.Id)).ToListAsync(ct),
            Invoices = invoices,
            Lines = lines,
            Movements = [],
            CreditLots = [],
            Coverage = coverage,
            Enrollments = await db.ServiceEnrollments.AsNoTracking().Where(enrollment => enrollment.UserId == id && enrollment.EndedAt == null).ToListAsync(ct),
            Services = await db.ServiceDefinitions.AsNoTracking().ToListAsync(ct),
            Fees = await db.Fees.AsNoTracking().ToListAsync(ct),
            Prices = await db.FeePrices.AsNoTracking().ToListAsync(ct),
            Discounts = await db.Discounts.AsNoTracking().Where(discount => discount.BillingAccountId == account.Id).ToListAsync(ct),
            ReadBalances = new(totals[BalanceKind.BillingCredit], totals[BalanceKind.PurchaseBalance], totals[BalanceKind.Held]),
            ReadHolds = holds
        };
        return await ReadView(facts, (transactionsPage - 1) * pageSize, pageSize, ct, true, portal: true, history: history, previousBillCount: previousCount);
    }

    private sealed class JournalEntry
    {
        public required MoneyMovement Movement { get; init; }
        public decimal Delta { get; init; }
    }

    // Derive SQL kind sets from the same signed-delta functions used by the command evaluator.
    private static MovementKind[] Kinds(BalanceKind balance, int sign) => Enum.GetValues<MovementKind>().Where(kind => (balance switch
    {
        BalanceKind.BillingCredit => BillingFacts.BillingBalanceDelta(kind),
        BalanceKind.PurchaseBalance => BillingFacts.PurchaseBalanceDelta(kind),
        _ => BillingFacts.HeldBalanceDelta(kind)
    }) == sign).ToArray();

    private IQueryable<JournalEntry> Journal(long accountId, BalanceKind balance)
    {
        var plus = Kinds(balance, 1);
        var minus = Kinds(balance, -1);
        return db.MoneyMovements.AsNoTracking().Where(movement => movement.BillingAccountId == accountId &&
                (plus.Contains(movement.Kind) || minus.Contains(movement.Kind)))
            .Select(movement => new JournalEntry { Movement = movement, Delta = plus.Contains(movement.Kind) ? movement.Amount : -movement.Amount });
    }

    private async Task<List<BalanceStatement>> PortalStatements(long accountId, int skip, int take, CancellationToken ct)
    {
        var statements = new List<BalanceStatement>();
        foreach (var balance in Enum.GetValues<BalanceKind>())
        {
            var query = Journal(accountId, balance);
            var entries = await query.OrderByDescending(row => row.Movement.EffectiveOn).ThenByDescending(row => row.Movement.Id)
                .Skip(skip).Take(take + 1).ToListAsync(ct);
            var hasMore = entries.Count > take;
            entries = entries.Take(take).Reverse().ToList();
            var first = entries.FirstOrDefault()?.Movement;
            // Opening amount includes all earlier effective dates and stable ID ties, including backdated entries.
            var opening = first is null ? 0 : await query.Where(row => row.Movement.EffectiveOn < first.EffectiveOn ||
                    (row.Movement.EffectiveOn == first.EffectiveOn && row.Movement.Id < first.Id)).SumAsync(row => row.Delta, ct);
            var movements = entries.Select(row => row.Movement).ToList();
            var operationIds = movements.Select(movement => movement.OperationId).ToArray();
            var operations = await db.BillingOperations.AsNoTracking().Where(operation => operationIds.Contains(operation.Id)).ToDictionaryAsync(operation => operation.Id, ct);
            var receiptIds = movements.Where(movement => movement.ReceiptId != null).Select(movement => movement.ReceiptId!.Value).ToArray();
            var receipts = await db.Receipts.AsNoTracking().Where(receipt => receiptIds.Contains(receipt.Id)).ToDictionaryAsync(receipt => receipt.Id, ct);
            var lineIds = movements.Where(movement => movement.InvoiceLineId != null).Select(movement => movement.InvoiceLineId!.Value).ToArray();
            var lines = await db.InvoiceLines.AsNoTracking().Where(line => lineIds.Contains(line.Id)).ToDictionaryAsync(line => line.Id, ct);
            var discountIds = movements.Where(movement => movement.DiscountId != null).Select(movement => movement.DiscountId!.Value).ToArray();
            var discounts = await db.Discounts.AsNoTracking().Where(discount => discountIds.Contains(discount.Id)).ToDictionaryAsync(discount => discount.Id, ct);
            var reversedIds = movements.Where(movement => movement.ReversesMovementId != null).Select(movement => movement.ReversesMovementId!.Value).ToArray();
            var reversed = await db.MoneyMovements.AsNoTracking().Where(movement => reversedIds.Contains(movement.Id)).ToDictionaryAsync(movement => movement.Id, ct);
            var lots = await db.CreditLots.AsNoTracking().Where(lot => lot.BillingAccountId == accountId && receiptIds.Contains(lot.ReceiptId)).OrderBy(lot => lot.Id).ToListAsync(ct);
            var receiptFunding = await db.MoneyMovements.AsNoTracking().Where(movement => movement.BillingAccountId == accountId &&
                movement.Kind == MovementKind.ReceiptToBilling && movement.ReceiptId != null && receiptIds.Contains(movement.ReceiptId.Value)).OrderBy(movement => movement.Id).ToListAsync(ct);
            var lotsByMovement = receiptFunding.GroupBy(movement => movement.ReceiptId!.Value).SelectMany(group =>
                group.Zip(lots.Where(lot => lot.ReceiptId == group.Key), (movement, lot) => (movement.Id, Lot: lot))).ToDictionary(row => row.Id, row => row.Lot);
            var running = opening;
            var result = new List<StatementLineView>();
            foreach (var entry in entries)
            {
                var movement = entry.Movement;
                running += entry.Delta;
                var line = lines.GetValueOrDefault(movement.InvoiceLineId ?? 0);
                var lot = lotsByMovement.GetValueOrDefault(movement.Id);
                result.Add(new(movement.Id, movement.EffectiveOn, operations.GetValueOrDefault(movement.OperationId)?.OccurredAt ?? default,
                    movement.Kind, entry.Delta, running, Describe(movement, line, receipts.GetValueOrDefault(movement.ReceiptId ?? 0),
                        discounts.GetValueOrDefault(movement.DiscountId ?? 0), reversed.GetValueOrDefault(movement.ReversesMovementId ?? 0)),
                    movement.ReceiptId, movement.InvoiceLineId, line?.InvoiceId, movement.DiscountId, movement.ReversesMovementId,
                    lot?.Id, lot?.OriginalAmount, lot?.RemainingAmount));
            }
            result.Reverse();
            statements.Add(new(balance, opening, running, hasMore, result));
        }
        return statements;
    }
}
