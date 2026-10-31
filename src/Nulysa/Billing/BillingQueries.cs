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
public sealed record Forecast(string Description, DateOnly Start, DateOnly End, decimal Amount, int Months);
public sealed record Reconciliation(decimal Receipts, decimal BillingCredit, decimal PurchaseBalance, decimal Held, decimal Purchases,
    decimal Corrections, decimal Refunds, decimal Difference, List<long> InvalidLines, List<long> InvalidReceipts);
public sealed record FinancialClaim(long AccountId, string? Name, string? Email, decimal BillingCredit, decimal PurchaseBalance);
public sealed record BillingView(long AccountId, long? UserId, string DepositReference, bool EmailInvoices, Balances Balances, Standing Membership,
    List<InvoiceView> Invoices, List<RecurringItem> Items, List<BillingGroup> Groups, List<SubscriptionView> Subscriptions,
    List<MoneyMovement> Movements, List<Coverage> Coverage, List<Discount> Discounts, List<BillingOperation> History, List<Forecast> Forecasts);

public sealed class BillingQueries(AppDbContext db, AssociationClock clock, IOptions<BillingOptions> options)
{
    public async Task<BillingView?> Member(ClaimsPrincipal actor, long userId, CancellationToken ct = default)
    {
        await Authority.RequireOwnerOrAdministrator(db, actor, userId, ct);
        var account = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        return account is null ? null : await Account(actor, account.Id, ct);
    }

    public async Task<BillingView> Account(ClaimsPrincipal actor, long accountId, CancellationToken ct = default)
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
                var nextStart = item.NextOn;
                var benefit = item.MembershipId is not null ? Benefit.Membership : Benefit.Service;
                foreach (var covered in facts.Periods(benefit, fee.ServiceDefinitionId).OrderBy(x => x.Start))
                    if (covered.Start <= nextStart && nextStart < covered.End)
                        nextStart = covered.End;

                var hasEffectivePrice = facts.Prices.Any(x => x.FeeDefinitionId == fee.Id && x.EffectiveOn <= nextStart);
                if (!hasEffectivePrice)
                    continue;

                var period = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, nextStart, true);
                var prices = facts.Prices.Where(x => x.FeeDefinitionId == fee.Id).ToList();
                var discounts = facts.Discounts.Where(x => x.FeeDefinitionId == fee.Id).ToList();
                var price = Pricing.Calculate(group.Anchor, group.IntervalMonths, period, prices, discounts, facts.Periods(benefit, fee.ServiceDefinitionId));
                forecasts.Add(new(fee.Name, period.Start, period.End, price.Amount, group.IntervalMonths));
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
            facts.Movements.OrderByDescending(x => x.Id).Take(100).ToList(), facts.Coverage, facts.Discounts, operations, forecasts);
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

        var billing = Sum(MovementKind.ReceiptToBilling, MovementKind.LineToBilling, MovementKind.PurchaseToBilling, MovementKind.PurchaseCredit) - Sum(MovementKind.BillingToLine, MovementKind.BillingToPurchase, MovementKind.BillingRefund, MovementKind.BillingReceiptReversal);
        var purchase = Sum(MovementKind.ReceiptToPurchase, MovementKind.BillingToPurchase) - Sum(MovementKind.PurchaseToBilling, MovementKind.PurchaseRefund, MovementKind.PurchaseReceiptReversal);
        var held = Sum(MovementKind.ReceiptToLine, MovementKind.BillingToLine) - Sum(MovementKind.LineToBilling, MovementKind.LinePurchase);
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
                row.line?.StartsOn, row.line?.EndsOn, m.DiscountId, m.ReversesMovementId, row.Reason }.Select(Csv)));
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
