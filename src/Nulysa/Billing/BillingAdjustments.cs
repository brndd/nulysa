using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Services;

namespace Nulysa.Billing;

public sealed record ServiceInput(string Code, string Name, bool SelfSubscriptionAllowed, int InitialFreeDays, int RenewalGraceDays, int Priority, string Category);

public sealed partial class BillingCommands
{
    public Task<long?> OneOff(ClaimsPrincipal actor, long accountId, Guid key, string description, string category, decimal amount, DateOnly dueOn, CancellationToken ct = default)
        => Change(actor, accountId, key, "one-off", new { description, category, amount, dueOn }, "One-off advance payment request", true, async (facts, operation, scheduler) =>
        {
            Live(facts);

            var hasDescription = !string.IsNullOrWhiteSpace(description) && description.Length <= 300;
            var hasCategory = !string.IsNullOrWhiteSpace(category) && category.Length <= 80;
            var isPositiveCentAmount = amount > 0 && Pricing.Round(amount) == amount;
            if (!hasDescription || !hasCategory || !isPositiveCentAmount || dueOn <= clock.Today)
                throw new ValidationException("Provide a description, category, positive cent amount and future payment deadline.");

            var invoice = new Invoice { BillingAccountId = accountId, IssuedAt = clock.Now, OneOffReference = PaymentReference.Create('3') };
            facts.Invoices.Add(invoice);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);

            var line = new InvoiceLine
            {
                InvoiceId = invoice.Id,
                Benefit = Benefit.OneOff,
                Purpose = PurchasePurpose.OneOff,
                PurchaseKey = $"one-off:{key}",
                Description = description.Trim(),
                Category = category.Trim(),
                Amount = amount,
                StartsOn = clock.Today,
                EndsOn = dueOn,
                OriginalStartsOn = clock.Today,
                OriginalEndsOn = dueOn,
                ChargeDueOn = dueOn,
                ExpiresOn = dueOn
            };
            facts.Lines.Add(line);
            db.InvoiceLines.Add(line);
            operation.ResultId = invoice.Id;
        }, ct);

    public Task CancelInvoice(ClaimsPrincipal actor, long accountId, long invoiceId, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "cancel-invoice", new { invoiceId }, reason, true, async (facts, operation, scheduler) =>
        {
            if (!facts.Invoices.Any(x => x.Id == invoiceId))
                throw new ValidationException("Invoice belongs to another account.");
            foreach (var line in facts.Lines.Where(x => x.InvoiceId == invoiceId))
                CloseLine(facts, operation, line, LineState.Cancelled);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);
    }

    public Task SetEmailInvoices(ClaimsPrincipal actor, long accountId, bool enabled, Guid key, CancellationToken ct = default)
        => Change(actor, accountId, key, "email-preference", new { enabled }, "Invoice email preference", false, (facts, operation, scheduler) =>
        {
            Live(facts);
            facts.Account.EmailInvoices = enabled;
            return Task.CompletedTask;
        }, ct);

    public Task<long?> AddDiscount(ClaimsPrincipal actor, long accountId, long feeId, DateOnly start, DateOnly end, DiscountKind kind,
        decimal value, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "discount", new { feeId, start, end, kind, value }, reason, true, async (facts, operation, scheduler) =>
        {
            Live(facts);

            var hasValidFee = facts.Fees.Any(x => x.Id == feeId && !x.Retired);
            var hasValidDates = start >= clock.Today && end > start;
            var hasValidValue = value >= 0
                && Pricing.Round(value) == value
                && Enum.IsDefined(kind)
                && (kind != DiscountKind.Percentage || value <= 100);
            if (!hasValidFee || !hasValidDates || !hasValidValue)
                throw new ValidationException("A discount requires a valid fee, future effective dates, and a percentage (0–100) or nonnegative monthly replacement price.");
            var overlapsExisting = facts.Discounts.Any(x => x.FeeDefinitionId == feeId && x.WithdrawnAt is null && x.StartsOn < end && start < x.EndsOn);
            if (overlapsExisting)
                throw new ValidationException("Discounts for the same fee cannot overlap; withdraw the previous discount first.");

            var discount = new Discount { BillingAccountId = accountId, FeeDefinitionId = feeId, StartsOn = start, EndsOn = end, Kind = kind, Value = value, OperationId = operation.Id };
            facts.Discounts.Add(discount);
            db.Discounts.Add(discount);
            await db.SaveChangesAsync(ct);
            operation.ResultId = discount.Id;

            await RepriceAdjusted(facts, operation, feeId, ct);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);
    }

    public Task WithdrawDiscount(ClaimsPrincipal actor, long accountId, long discountId, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "withdraw-discount", new { discountId }, reason, true, async (facts, operation, scheduler) =>
        {
            Live(facts);
            var discount = facts.Discounts.SingleOrDefault(x => x.Id == discountId && x.WithdrawnAt is null) ?? throw new ValidationException("Discount is unavailable.");
            discount.WithdrawnAt = clock.Now;
            await RepriceAdjusted(facts, operation, discount.FeeDefinitionId, ct);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);
    }

    private async Task RepriceAdjusted(BillingFacts facts, BillingOperation operation, long feeId, CancellationToken ct)
    {
        var openLines = facts.Lines.Where(x => x.FeeDefinitionId == feeId && x.State == LineState.Open).ToList();
        var discounts = facts.Discounts.Where(x => x.FeeDefinitionId == feeId).ToList();
        foreach (var line in openLines)
        {
            var quote = Pricing.Adjust(JsonSerializer.Deserialize<List<PriceSegment>>(line.PricingJson)!,
                discounts, clock.Today, facts.Periods(line.Benefit, line.ServiceDefinitionId));
            Requote(facts, operation, line, quote, new(line.StartsOn, line.EndsOn));

            if (quote.Segments.Count == 0)
                CloseLine(facts, operation, line, LineState.Cancelled);
            else if (facts.Held(line) >= line.Amount && clock.Today < line.ExpiresOn)
                Complete(facts, operation, line, line.Amount == 0 ? null : clock.Today);
        }

        await db.SaveChangesAsync(ct);
    }

    public Task GrantMembershipMonths(ClaimsPrincipal actor, long accountId, int months, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "membership-grant", new { months }, reason, true, async (facts, operation, scheduler) =>
        {
            Live(facts);
            if (months is < 1 or > 120)
                throw new ValidationException("Grant between 1 and 120 whole months.");

            var periods = facts.Periods(Benefit.Membership, null).OrderBy(x => x.Start).ToList();
            var extensionStart = clock.Today;
            var hasCurrentCoverage = periods.Any(x => x.Start <= extensionStart && extensionStart < x.End);
            if (!hasCurrentCoverage)
                throw new ValidationException("A duration extension requires current paid or granted membership coverage.");
            foreach (var period in periods)
                if (period.Start <= extensionStart && period.End > extensionStart)
                    extensionStart = period.End;

            Grant(facts, operation, Benefit.Membership, null, new(extensionStart, extensionStart.AddMonths(months)), CoverageSource.AdministratorGrant, $"membership-grant:{key}");
            await RepriceAdjusted(facts, operation, 1, ct);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);
    }

    public Task CreditFutureDiscount(ClaimsPrincipal actor, long accountId, long lineId, long discountId, decimal reviewedAmount, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "future-discount-credit", new { lineId, discountId, reviewedAmount }, reason, true, async (facts, operation, scheduler) =>
        {
            Live(facts);
            var line = facts.Lines.SingleOrDefault(x => x.Id == lineId && x.State == LineState.Fulfilled) ?? throw new ValidationException("Select a completed purchase.");
            var discount = facts.Discounts.SingleOrDefault(x => x.Id == discountId && x.FeeDefinitionId == line.FeeDefinitionId && x.WithdrawnAt is null)
                ?? throw new ValidationException("Select an applicable discount.");
            if (discount.StartsOn < clock.Today)
                throw new ValidationException("Used time is not automatically refundable; review future effective time only.");

            var originalSegments = JsonSerializer.Deserialize<List<PriceSegment>>(line.PricingJson)!;
            var previousCredits = facts.Movements
                .Where(x => x.InvoiceLineId == lineId && x.Kind == MovementKind.PurchaseCredit)
                .OrderBy(x => x.Id)
                .Select(x => (facts.Discounts.Single(d => d.Id == x.DiscountId), x.Amount))
                .ToList();
            var remainingCredit = Pricing.FutureDiscountCredit(originalSegments, discount, previousCredits);
            if (reviewedAmount <= 0 || reviewedAmount != remainingCredit)
                throw new ValidationException($"Review the calculated remaining credit: {remainingCredit:0.00} EUR.");

            Move(facts, operation, MovementKind.PurchaseCredit, remainingCredit, clock.Today, lineId, discount: discountId);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);
    }

    public async Task<long> ConfigureService(ClaimsPrincipal actor, long? id, ServiceInput input, CancellationToken ct = default)
    {
        var hasValidCode = !string.IsNullOrWhiteSpace(input.Code) && input.Code.Length <= 80;
        var hasValidName = !string.IsNullOrWhiteSpace(input.Name) && input.Name.Length <= 200;
        var hasValidDurations = input.InitialFreeDays is >= 0 and <= 365
            && input.RenewalGraceDays is >= 0 and <= 365
            && input.Priority >= 0;
        var hasValidCategory = !string.IsNullOrWhiteSpace(input.Category) && input.Category.Length <= 80;
        if (!hasValidCode || !hasValidName || !hasValidDurations || !hasValidCategory)
            throw new ValidationException("Invalid service configuration.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72134504)", ct);
        await Authority.RequireAdministrator(db, actor, ct);

        var service = id is { } existing ? await db.ServiceDefinitions.SingleAsync(x => x.Id == existing, ct) : new ServiceDefinition();
        if (id is null)
            db.ServiceDefinitions.Add(service);
        else if (service.Code != input.Code)
            throw new ValidationException("A service's stable code cannot change.");

        service.Code = input.Code;
        service.Name = input.Name;
        service.SelfSubscriptionAllowed = input.SelfSubscriptionAllowed;
        service.InitialFreeDays = input.InitialFreeDays;
        service.RenewalGraceDays = input.RenewalGraceDays;
        service.Priority = input.Priority;
        await db.SaveChangesAsync(ct);

        var fee = await db.Fees.SingleOrDefaultAsync(x => x.ServiceDefinitionId == service.Id, ct);
        if (fee is null)
        {
            fee = new FeeDefinition { ServiceDefinitionId = service.Id };
            db.Fees.Add(fee);
        }
        fee.Name = input.Name;
        fee.Category = input.Category;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return service.Id;
    }

    public async Task SetPrice(ClaimsPrincipal actor, long feeId, DateOnly effectiveOn, decimal monthlyAmount, CancellationToken ct = default)
    {
        if (monthlyAmount < 0 || Pricing.Round(monthlyAmount) != monthlyAmount)
            throw new ValidationException("Use a nonnegative cent price.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72134504)", ct);
        await Authority.RequireAdministrator(db, actor, ct);

        var existingPrices = await db.FeePrices.Where(x => x.FeeDefinitionId == feeId).ToListAsync(ct);
        if (existingPrices.Any(x => x.EffectiveOn == effectiveOn))
            throw new ValidationException("A price already exists for this date. Add a new dated price; issued quotes remain unchanged.");
        if (existingPrices.Count > 0 && effectiveOn < clock.Today)
            throw new ValidationException("New price changes cannot be backdated.");

        db.FeePrices.Add(new() { FeeDefinitionId = feeId, EffectiveOn = effectiveOn, MonthlyAmount = monthlyAmount });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
