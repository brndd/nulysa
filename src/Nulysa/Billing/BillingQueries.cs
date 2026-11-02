using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Integrations;
using Nulysa.Membership;
using Nulysa.Services;

namespace Nulysa.Billing;

public sealed record LineView(InvoiceLine Line, decimal Held, decimal Remaining);
public sealed record InvoiceView(Invoice Invoice, string Reference, List<LineView> Lines)
{
    public decimal Total => Lines.Where(x => x.Line.State is LineState.Open or LineState.Fulfilled).Sum(x => x.Line.Amount);
    public decimal Paid => Lines.Where(x => x.Line.State == LineState.Fulfilled).Sum(x => x.Line.Amount);
    public decimal Held => Lines.Sum(x => x.Held);
    public decimal Remaining => Lines.Sum(x => x.Remaining);
}
public sealed record SubscriptionView(ServiceEnrollment Enrollment, string Name, Standing Standing);
public sealed record Forecast(string Description, DateOnly Start, DateOnly EndExclusive, decimal Amount, int Months);
public sealed record Reconciliation(decimal Receipts, decimal BillingCredit, decimal PurchaseBalance, decimal Held, decimal Purchases,
    decimal Corrections, decimal Refunds, decimal Difference, List<long> InvalidLines, List<long> InvalidReceipts);
public sealed record FinancialClaim(long AccountId, string? Name, string? Email, decimal BillingCredit, decimal PurchaseBalance);
public enum BalanceKind { BillingCredit, PurchaseBalance, Held }
public sealed record StatementLineView(long MovementId, DateOnly EffectiveOn, DateTimeOffset RecordedAt, MovementKind Kind, decimal Amount,
    decimal RunningBalance, string Description, long? ReceiptId, long? InvoiceLineId, long? InvoiceId, long? DiscountId, long? ReversesMovementId,
    long? LotId = null, decimal? LotOriginalAmount = null, decimal? LotRemaining = null);
public sealed record BalanceStatement(BalanceKind Balance, decimal OpeningBalance, decimal ClosingBalance, bool HasMore, List<StatementLineView> Lines);
public sealed record BillingView(long AccountId, long? UserId, string DepositReference, bool EmailInvoices, Balances Balances, Standing Membership,
    List<InvoiceView> Invoices, List<RecurringItem> Items, List<BillingGroup> Groups, List<SubscriptionView> Subscriptions,
    List<MoneyMovement> Movements, List<Coverage> Coverage, List<Discount> Discounts, List<BillingOperation> History, List<Forecast> Forecasts,
    List<ServiceDefinition> Services)
{
    public Dictionary<long, string> FeeNames { get; init; } = [];
    public List<BalanceStatement> Statements { get; init; } = [];
    public BalanceStatement Statement(BalanceKind balance) => Statements.Single(x => x.Balance == balance);
}

public sealed class BillingQueries(AppDbContext db, AssociationClock clock, IOptions<BillingOptions> options)
{
    public async Task<DateOnly> MembershipGrantEndExclusive(ClaimsPrincipal actor, long accountId, int months, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var facts = await BillingFacts.Load(db, accountId, ct, options.Value.MembershipRenewalGraceDays);
        return facts.MembershipGrantPeriod(months, clock.Today).EndExclusive;
    }

    public async Task<decimal> DiscountCreditPreview(ClaimsPrincipal actor, long accountId, long lineId, long discountId, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var facts = await BillingFacts.Load(db, accountId, ct, options.Value.MembershipRenewalGraceDays);
        var line = facts.Lines.SingleOrDefault(line => line.Id == lineId && line.State == LineState.Fulfilled)
            ?? throw new System.ComponentModel.DataAnnotations.ValidationException("Choose a completed purchase in this account.");
        var discount = facts.Discounts.SingleOrDefault(discount => discount.Id == discountId && discount.FeeDefinitionId == line.FeeDefinitionId && discount.WithdrawnAt == null)
            ?? throw new System.ComponentModel.DataAnnotations.ValidationException("Choose an applicable discount.");
        if (discount.StartsOn < clock.Today)
            throw new System.ComponentModel.DataAnnotations.ValidationException("Used time is not automatically refundable; review future effective time only.");
        var previous = facts.Movements
            .Where(movement => movement.InvoiceLineId == lineId && movement.Kind == MovementKind.PurchaseCredit)
            .OrderBy(movement => movement.Id)
            .Select(movement => (facts.Discounts.Single(discount => discount.Id == movement.DiscountId), movement.Amount))
            .ToList();
        return Pricing.FutureDiscountCredit(System.Text.Json.JsonSerializer.Deserialize<List<PriceSegment>>(line.PricingJson)!, discount, previous);
    }

    public async Task<InvoiceView> InvoiceForMember(ClaimsPrincipal actor, long invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException("Bill not found.");
        var account = await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == invoice.BillingAccountId, ct);
        if (account.UserId is { } userId)
            await Authority.RequireOwnerOrAdministrator(db, actor, userId, ct);
        else
            await Authority.RequireAdministrator(db, actor, ct);

        return View(await BillingFacts.Load(db, account.Id, ct, options.Value.MembershipRenewalGraceDays), invoice);
    }

    public async Task<BillingView?> Member(ClaimsPrincipal actor, long userId, int skip = 0, int take = 0, CancellationToken ct = default, bool newestFirst = false)
    {
        await Authority.RequireOwnerOrAdministrator(db, actor, userId, ct);
        var account = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        return account is null ? null : await Account(actor, account.Id, skip, take, ct, newestFirst);
    }

    public async Task<BillingView> Account(ClaimsPrincipal actor, long accountId, int skip = 0, int take = 0, CancellationToken ct = default, bool newestFirst = false)
    {
        var account = await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == accountId, ct);
        if (account.UserId is { } id)
            await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        else
            await Authority.RequireAdministrator(db, actor, ct);

        var facts = await BillingFacts.Load(db, accountId, ct, options.Value.MembershipRenewalGraceDays);
        var invoices = facts.Invoices
            .OrderByDescending(x => x.IssuedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => View(facts, x))
            .ToList();
        var forecasts = new List<Forecast>();
        var membership = facts.Membership(clock.Today);
        if (membership.Active)
        {
            foreach (var item in facts.Items.Where(x => x.EndedAt is null))
            {
                var group = facts.Groups.Single(x => x.Id == item.BillingGroupId);
                var fee = facts.Fees.Single(x => x.Id == item.FeeDefinitionId);
                if (fee.Retired)
                    continue;
                var nextStart = item.NextUnissuedPeriodStartsOn;
                var benefit = item.MembershipUserId is not null ? Benefit.Membership : Benefit.Service;
                foreach (var covered in facts.Periods(benefit, fee.ServiceDefinitionId).OrderBy(x => x.Start))
                    if (covered.Start <= nextStart && nextStart < covered.EndExclusive)
                        nextStart = covered.EndExclusive;

                var hasEffectivePrice = facts.Prices.Any(x => x.FeeDefinitionId == fee.Id && x.EffectiveOn <= nextStart);
                if (!hasEffectivePrice)
                    continue;

                var period = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, nextStart, true);
                var prices = facts.Prices.Where(x => x.FeeDefinitionId == fee.Id).ToList();
                var discounts = facts.Discounts.Where(x => x.FeeDefinitionId == fee.Id).ToList();
                var price = Pricing.Calculate(group.Anchor, group.IntervalMonths, period, prices, discounts, facts.Periods(benefit, fee.ServiceDefinitionId));
                forecasts.Add(new(fee.Name, period.Start, period.EndExclusive, price.Amount, group.IntervalMonths));
            }
        }

        var subscriptions = facts.Enrollments
            .Where(x => x.EndedAt is null)
            .Select(x => new SubscriptionView(x, facts.Services.Single(s => s.Id == x.ServiceDefinitionId).Name, facts.Service(x, clock.Today)))
            .ToList();
        var operations = await db.BillingOperations.AsNoTracking()
            .Where(x => x.BillingAccountId == accountId)
            .OrderByDescending(x => x.OccurredAt)
            .Take(100)
            .ToListAsync(ct);
        return new(account.Id, account.UserId, account.PurchaseDepositReference, account.EmailInvoices, facts.Balances, membership, invoices,
            facts.Items.Where(x => x.EndedAt is null).ToList(), facts.Groups, subscriptions,
            facts.Movements.OrderByDescending(x => x.Id).Take(100).ToList(), facts.Coverage, facts.Discounts, operations, forecasts, facts.Services)
        {
            FeeNames = facts.Fees.ToDictionary(fee => fee.Id, fee => fee.Name),
            Statements = await StatementsFor(facts, null, null, skip, take, ct, newestFirst)
        };
    }

    public async Task<List<BalanceStatement>> Statements(ClaimsPrincipal actor, long accountId, DateOnly? from = null, DateOnly? to = null,
        int skip = 0, int take = 0, CancellationToken ct = default)
    {
        var account = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == accountId, ct)
            ?? throw new System.ComponentModel.DataAnnotations.ValidationException("Choose an existing financial account ID.");
        if (account.UserId is { } id)
            await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        else
            await Authority.RequireAdministrator(db, actor, ct);
        var facts = await BillingFacts.Load(db, accountId, ct, options.Value.MembershipRenewalGraceDays);
        return await StatementsFor(facts, from, to, skip, take, ct);
    }

    public async Task<BalanceStatement> Statement(ClaimsPrincipal actor, long accountId, BalanceKind balance, DateOnly? from = null,
        DateOnly? to = null, int skip = 0, int take = 0, CancellationToken ct = default)
        => (await Statements(actor, accountId, from, to, skip, take, ct)).Single(x => x.Balance == balance);

    private async Task<List<BalanceStatement>> StatementsFor(BillingFacts facts, DateOnly? from, DateOnly? to, int skip, int take, CancellationToken ct, bool newestFirst = false)
    {
        if (from is not null && to is not null && to <= from)
            throw new System.ComponentModel.DataAnnotations.ValidationException("Choose an end date after the start date.");
        if (skip < 0 || take < 0)
            throw new System.ComponentModel.DataAnnotations.ValidationException("Statement paging cannot be negative.");

        var operationIds = facts.Movements.Select(x => x.OperationId).Distinct().ToList();
        var operations = operationIds.Count == 0
            ? new Dictionary<Guid, BillingOperation>()
            : await db.BillingOperations.AsNoTracking().Where(x => operationIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var receiptIds = facts.Movements.Where(x => x.ReceiptId is not null).Select(x => x.ReceiptId!.Value).Distinct().ToList();
        var receipts = receiptIds.Count == 0
            ? new Dictionary<long, Receipt>()
            : await db.Receipts.AsNoTracking().Where(x => receiptIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var lines = facts.Lines.ToDictionary(x => x.Id);
        var discounts = facts.Discounts.ToDictionary(x => x.Id);
        var movements = facts.Movements.ToDictionary(x => x.Id);

        var results = new List<BalanceStatement>();
        // Lots are created in the same order as their ReceiptToBilling movements; pair them deterministically.
        var lotsByMovement = facts.Movements
            .Where(x => x.Kind == MovementKind.ReceiptToBilling && x.ReceiptId is not null)
            .GroupBy(x => x.ReceiptId!.Value)
            .SelectMany(g => g.OrderBy(x => x.Id)
                .Zip(facts.CreditLots.Where(l => l.ReceiptId == g.Key).OrderBy(l => l.Id),
                    (movement, lot) => (movement.Id, lot)))
            .ToDictionary(x => x.Id, x => x.lot);
        foreach (var balance in Enum.GetValues<BalanceKind>())
        {
            var ordered = facts.Movements
                .Select(movement => new
                {
                    movement,
                    delta = balance switch
                    {
                        BalanceKind.BillingCredit => BillingFacts.BillingBalanceDelta(movement.Kind),
                        BalanceKind.PurchaseBalance => BillingFacts.PurchaseBalanceDelta(movement.Kind),
                        _ => BillingFacts.HeldBalanceDelta(movement.Kind)
                    } * movement.Amount
                })
                .Where(x => x.delta != 0)
                .OrderBy(x => x.movement.EffectiveOn)
                .ThenBy(x => x.movement.Id)
                .ToList();
            var opening = from is { } start ? ordered.Where(x => x.movement.EffectiveOn < start).Sum(x => x.delta) : 0;
            var inRange = ordered.Where(x => (from is null || x.movement.EffectiveOn >= from) && (to is null || x.movement.EffectiveOn < to)).ToList();
            var limit = take > 0 ? take : int.MaxValue;
            var remaining = Math.Max(0, inRange.Count - skip);
            var offset = newestFirst ? Math.Max(0, remaining - limit) : skip;
            var page = inRange.Skip(offset).Take(newestFirst ? Math.Min(limit, remaining) : limit).ToList();
            var running = opening + inRange.Take(offset).Sum(x => x.delta);
            var pageOpening = running;
            var statementLines = new List<StatementLineView>();
            foreach (var entry in page)
            {
                running += entry.delta;
                var movement = entry.movement;
                var lot = lotsByMovement.GetValueOrDefault(movement.Id);
                statementLines.Add(new(movement.Id, movement.EffectiveOn, operations.GetValueOrDefault(movement.OperationId)?.OccurredAt ?? default,
                    movement.Kind, entry.delta, running,
                    Describe(movement, lines.GetValueOrDefault(movement.InvoiceLineId ?? 0), receipts.GetValueOrDefault(movement.ReceiptId ?? 0),
                        discounts.GetValueOrDefault(movement.DiscountId ?? 0), movements.GetValueOrDefault(movement.ReversesMovementId ?? 0)),
                    movement.ReceiptId, movement.InvoiceLineId, lines.GetValueOrDefault(movement.InvoiceLineId ?? 0)?.InvoiceId,
                    movement.DiscountId, movement.ReversesMovementId, lot?.Id, lot?.OriginalAmount, lot?.RemainingAmount));
            }
            // Calculate running balances chronologically before reversing presentation order.
            if (newestFirst)
                statementLines.Reverse();
            results.Add(new(balance, pageOpening, running, inRange.Count > skip + page.Count, statementLines));
        }
        return results;
    }

    private static string Describe(MoneyMovement movement, InvoiceLine? line, Receipt? receipt, Discount? discount, MoneyMovement? reversed)
    {
        var text = movement.Kind switch
        {
            MovementKind.ReceiptToLine => "Receipt funding held for a purchase",
            MovementKind.ReceiptToBilling => "Billing credit from a receipt",
            MovementKind.ReceiptToPurchase => "Purchase balance deposit",
            MovementKind.BillingToLine => "Billing credit applied to a purchase",
            MovementKind.LineToBilling => "Unspent purchase funds released to billing credit",
            MovementKind.LinePurchase => "Purchase completed from held funds",
            MovementKind.BillingToPurchase => "Billing credit transferred to purchase balance",
            MovementKind.PurchaseToBilling => "Purchase balance transferred to billing credit",
            MovementKind.PurchaseCredit => "Billing credit correction",
            MovementKind.BillingRefund => "Billing credit refund",
            MovementKind.PurchaseRefund => "Purchase balance refund",
            MovementKind.BillingReceiptReversal => "Billing credit allocation reversed",
            MovementKind.PurchaseReceiptReversal => "Purchase balance allocation reversed",
            _ => movement.Kind.ToString()
        };
        if (line is not null && !string.IsNullOrWhiteSpace(line.Description))
            text += $" — {line.Description}";
        if (receipt is not null && movement.ReceiptId is not null)
            text += $" — receipt #{receipt.Id}" + (string.IsNullOrWhiteSpace(receipt.Reference) ? "" : $" ({receipt.Reference})");
        if (discount is not null && movement.DiscountId is not null)
            text += $" — discount #{discount.Id}";
        if (reversed is not null && movement.ReversesMovementId is not null)
            text += $" — reverses movement #{reversed.Id}";
        return text;
    }

    internal static InvoiceView View(BillingFacts facts, Invoice invoice)
    {
        var reference = invoice.OneOffReference ?? facts.Groups.Single(x => x.Id == invoice.BillingGroupId).PaymentReference;
        var lines = facts.Lines
            .Where(x => x.InvoiceId == invoice.Id)
            .OrderBy(x => x.Id)
            .Select(line => new LineView(line, facts.Held(line), line.State == LineState.Open ? line.Amount - facts.Held(line) : 0))
            .ToList();
        return new(invoice, reference, lines);
    }

    public async Task<List<ServiceDefinition>> Services(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireOwnerOrAdministrator(db, actor, Authority.Id(actor), ct);
        return await db.ServiceDefinitions.AsNoTracking().Where(x => !x.Retired).OrderBy(x => x.Priority).ThenBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<List<ServiceDefinition>> AllServices(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.ServiceDefinitions.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<List<Receipt>> Review(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.Receipts.AsNoTracking().Where(x => x.State == ReceiptState.New || x.State == ReceiptState.Review).OrderBy(x => x.ImportedAt).Take(100).ToListAsync(ct);
    }

    public async Task<List<FeeDefinition>> Fees(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.Fees.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<List<FeePrice>> Prices(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.FeePrices.AsNoTracking().OrderByDescending(x => x.EffectiveOn).ToListAsync(ct);
    }

    public async Task<List<BankSyncState>> BankStatus(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.BankSyncStates.AsNoTracking().OrderBy(x => x.Account).ToListAsync(ct);
    }

    public async Task<List<FinancialClaim>> Claims(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var accounts = await db.BillingAccounts.AsNoTracking().Where(x => x.UserId == null && x.ClaimEmail != null).ToListAsync(ct);
        if (accounts.Count == 0)
            return [];

        var ids = accounts.Select(x => x.Id).ToList();
        var rows = await db.MoneyMovements.AsNoTracking()
            .Where(x => ids.Contains(x.BillingAccountId))
            .GroupBy(x => new { x.BillingAccountId, x.Kind })
            .Select(x => new { x.Key.BillingAccountId, x.Key.Kind, Amount = x.Sum(m => m.Amount) })
            .ToListAsync(ct);
        var balances = rows.GroupBy(x => x.BillingAccountId)
            .ToDictionary(
                group => group.Key,
                group => new Balances(
                    group.Sum(x => BillingFacts.BillingBalanceDelta(x.Kind) * x.Amount),
                    group.Sum(x => BillingFacts.PurchaseBalanceDelta(x.Kind) * x.Amount),
                    0));
        return accounts.Select(account =>
        {
            var balance = balances.GetValueOrDefault(account.Id);
            return new FinancialClaim(account.Id, account.ClaimName, account.ClaimEmail, balance?.BillingCredit ?? 0, balance?.PurchaseBalance ?? 0);
        }).ToList();
    }

    public async Task<Reconciliation> Reconcile(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);

        // One MVCC snapshot: a concurrent purchase must not look like an accounting discrepancy.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var totals = await db.MoneyMovements.AsNoTracking()
            .GroupBy(x => x.Kind)
            .Select(x => new { Kind = x.Key, Amount = x.Sum(m => m.Amount) })
            .ToDictionaryAsync(x => x.Kind, x => x.Amount, ct);
        decimal Sum(params MovementKind[] kinds) => kinds.Sum(x => totals.GetValueOrDefault(x));

        var billing = totals.Sum(x => BillingFacts.BillingBalanceDelta(x.Key) * x.Value);
        var purchase = totals.Sum(x => BillingFacts.PurchaseBalanceDelta(x.Key) * x.Value);
        var held = totals.Sum(x => BillingFacts.HeldBalanceDelta(x.Key) * x.Value);
        var receipts = await db.Receipts.Where(x => x.State == ReceiptState.Posted).SumAsync(x => x.Amount, ct);
        var bought = Sum(MovementKind.LinePurchase);
        var corrections = Sum(MovementKind.PurchaseCredit);
        var refunds = Sum(MovementKind.BillingRefund, MovementKind.PurchaseRefund);
        var invalidLines = await db.InvoiceLines.Where(line => db.MoneyMovements.Where(x => x.InvoiceLineId == line.Id).Sum(x =>
            x.Kind == MovementKind.ReceiptToLine || x.Kind == MovementKind.BillingToLine ? x.Amount : x.Kind == MovementKind.LineToBilling || x.Kind == MovementKind.LinePurchase ? -x.Amount : 0) < 0 ||
            (line.State != LineState.Open && db.MoneyMovements.Where(x => x.InvoiceLineId == line.Id).Sum(x =>
            x.Kind == MovementKind.ReceiptToLine || x.Kind == MovementKind.BillingToLine ? x.Amount : x.Kind == MovementKind.LineToBilling || x.Kind == MovementKind.LinePurchase ? -x.Amount : 0) != 0)).Select(x => x.Id).ToListAsync(ct);
        var invalidReceipts = await db.Receipts.Where(receipt => (receipt.State == ReceiptState.Posted ? receipt.Amount : 0) != db.MoneyMovements.Where(x => x.ReceiptId == receipt.Id)
            .Sum(x => x.Kind == MovementKind.BillingReceiptReversal || x.Kind == MovementKind.PurchaseReceiptReversal ? -x.Amount : x.Amount)).Select(x => x.Id).ToListAsync(ct);

        await tx.CommitAsync(ct);
        var difference = receipts - (billing + purchase + held + bought - corrections + refunds);
        return new(receipts, billing, purchase, held, bought, corrections, refunds, difference, invalidLines, invalidReceipts);
    }

    public async Task<string> Export(ClaimsPrincipal actor, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (end <= start)
            throw new System.ComponentModel.DataAnnotations.ValidationException("Choose an end date after the start date.");
        var rows = await (from movement in db.MoneyMovements.AsNoTracking()
                          join operation in db.BillingOperations on movement.OperationId equals operation.Id
                          join line in db.InvoiceLines on movement.InvoiceLineId equals line.Id into lines
                          from line in lines.DefaultIfEmpty()
                          join receipt in db.Receipts on movement.ReceiptId equals receipt.Id into receipts
                          from receipt in receipts.DefaultIfEmpty()
                          where movement.EffectiveOn >= start && movement.EffectiveOn < end
                          orderby movement.EffectiveOn, movement.Id
                          select new { movement, operation.OccurredAt, operation.Reason, line, receipt }).ToListAsync(ct);
        var csv = new StringBuilder("movement_id,account_id,operation_id,effective_date,processed_at,kind,amount,currency,receipt_id,source,source_account,external_id,reference,line_id,invoice_id,category,period_start,period_end,discount_id,reverses_movement_id,reason\r\n");
        foreach (var row in rows)
        {
            var m = row.movement;
            csv.AppendLine(string.Join(',', new object?[] { m.Id, m.BillingAccountId, m.OperationId, m.EffectiveOn, row.OccurredAt, m.Kind, m.Amount, "EUR", m.ReceiptId,
                row.receipt?.Source, row.receipt?.SourceAccount, row.receipt?.ExternalId, row.receipt?.Reference, m.InvoiceLineId, row.line?.InvoiceId, row.line?.Category,
                row.line?.StartsOn, row.line?.EndExclusive, m.DiscountId, m.ReversesMovementId, row.Reason }.Select(Csv)));
        }
        return csv.ToString();
    }

    public async Task<string> ExportReceipts(ClaimsPrincipal actor, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (end <= start)
            throw new System.ComponentModel.DataAnnotations.ValidationException("Choose an end date after the start date.");
        var receipts = await db.Receipts.AsNoTracking().Where(x => x.ReceivedOn >= start && x.ReceivedOn < end)
            .OrderBy(x => x.ReceivedOn).ThenBy(x => x.Id).ToListAsync(ct);
        var csv = new StringBuilder("receipt_id,source,source_account,external_id,received_on,imported_at,amount,currency,reference,message,counterparty,state,account_id,review_reason\r\n");
        foreach (var receipt in receipts)
            csv.AppendLine(string.Join(',', new object?[] { receipt.Id, receipt.Source, receipt.SourceAccount, receipt.ExternalId, receipt.ReceivedOn,
                receipt.ImportedAt, receipt.Amount, receipt.Currency, receipt.Reference, receipt.Message, receipt.Counterparty, receipt.State,
                receipt.BillingAccountId, receipt.ReviewReason }.Select(Csv)));
        return csv.ToString();
    }

    internal static string Csv(object? value)
    {
        var text = value switch
        {
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? ""
        };
        if (value is string && (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('@') || text.TrimStart().StartsWith('-')))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
