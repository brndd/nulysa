using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;
using Nulysa.Services;
using Quartz;

namespace Nulysa.Billing;

public sealed partial class BillingCommands(AppDbContext db, AssociationClock clock, IOptions<BillingOptions> options,
    ISchedulerFactory schedulers, MailQueue mail)
{
    private BillingOptions Settings => options.Value;

    // Caller owns the outer transaction; the account row serializes all journal writes.
    private async Task<BillingFacts> Lock(long accountId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Billing requires an outer transaction.");

        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"BillingAccounts\" WHERE \"Id\" = {accountId} FOR UPDATE", ct);
        return await BillingFacts.Load(db, accountId, ct, Settings.MembershipRenewalGraceDays);
    }

    private async Task<BillingOperation?> Operation(BillingFacts facts, Guid key, string kind, object input, long? actor, string reason, CancellationToken ct)
    {
        if (key == Guid.Empty)
            throw new ValidationException("An operation key is required.");
        if (reason.Length > 1000)
            throw new ValidationException("Reason is too long.");

        var inputHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { input, actor, reason })));
        var prior = await db.BillingOperations.SingleOrDefaultAsync(x => x.Id == key, ct);
        if (prior is not null)
        {
            // The key is the idempotency key; a different replay must fail rather than mutate twice.
            if (prior.BillingAccountId != facts.Account.Id || prior.Kind != kind || prior.InputHash != inputHash)
                throw new ValidationException("This operation key was already used with different inputs.");
            return null;
        }

        var operation = new BillingOperation { Id = key, BillingAccountId = facts.Account.Id, Kind = kind, InputHash = inputHash, ActorId = actor, Reason = reason, OccurredAt = clock.Now };
        db.BillingOperations.Add(operation);
        return operation;
    }

    private static void Live(BillingFacts facts)
    {
        if (facts.User is not { MayLogIn: true })
            throw new ValidationException("This account cannot purchase while unavailable or pending deletion.");
    }

    private static void Reason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new ValidationException("Supply a reason, up to 1000 characters.");
    }

    private void Move(BillingFacts facts, BillingOperation operation, MovementKind kind, decimal amount, DateOnly on, long? line = null, long? receipt = null, long? discount = null, long? reverses = null)
    {
        if (amount == 0)
            return;
        if (amount < 0 || Pricing.Round(amount) != amount)
            throw new InvalidOperationException("Money movements must be positive cents.");

        // Sign is carried by Kind, never by a negative movement amount.
        var movement = new MoneyMovement { BillingAccountId = facts.Account.Id, OperationId = operation.Id, Kind = kind, Amount = amount, EffectiveOn = on, InvoiceLineId = line, ReceiptId = receipt, DiscountId = discount, ReversesMovementId = reverses };
        facts.Movements.Add(movement);
        db.MoneyMovements.Add(movement);
    }

    private void Grant(BillingFacts facts, BillingOperation operation, Benefit benefit, long? service, Period period, CoverageSource source, string origin, long? line = null)
    {
        if (facts.Coverage.Any(x => x.Origin == origin))
            return;
        var coverage = new Coverage { BillingAccountId = facts.Account.Id, Benefit = benefit, ServiceDefinitionId = service, StartsOn = period.Start, EndsOn = period.End, Source = source, Origin = origin, InvoiceLineId = line, OperationId = operation.Id };
        facts.Coverage.Add(coverage);
        db.Coverage.Add(coverage);
    }

    private async Task<BillingGroup> Group(BillingFacts facts, int months, DateOnly anchor, CancellationToken ct)
    {
        if (!Settings.AllowedIntervals.Contains(months))
            throw new ValidationException("Choose an allowed billing interval.");
        var group = facts.Groups.SingleOrDefault(x => x.IntervalMonths == months);
        if (group is not null)
            return group;
        group = new() { BillingAccountId = facts.Account.Id, IntervalMonths = months, Anchor = anchor };
        facts.Groups.Add(group);
        db.BillingGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    // Approval owns the application lock, outer transaction and Quartz enlistment.
    public async Task Initialize(BillingAccount account, DateOnly anchor, DateTimeOffset approvedAt, IScheduler scheduler, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Initialization requires the admission transaction.");
        var facts = await BillingFacts.Load(db, account.Id, ct, Settings.MembershipRenewalGraceDays);
        Live(facts);

        var request = await db.InitialMembershipRequests.SingleOrDefaultAsync(x => x.BillingAccountId == account.Id, ct);
        if (request is { InvoiceId: not null } or { CancelledAt: not null })
            return;

        // The request is the handover record; its invoice ID is set atomically with the first line.
        request ??= new InitialMembershipRequest { BillingAccountId = account.Id, Anchor = anchor, RequestedAt = approvedAt };
        if (db.Entry(request).State == EntityState.Detached)
            db.InitialMembershipRequests.Add(request);

        var operation = (await Operation(facts, Guid.NewGuid(), "admission", new { anchor }, null, "Initial membership request", ct))!;
        var group = await Group(facts, Settings.DefaultIntervalMonths, anchor, ct);
        var item = new RecurringItem { BillingGroupId = group.Id, FeeDefinitionId = 1, MembershipId = account.UserId, StartsOn = anchor, NextOn = anchor };
        facts.Items.Add(item);
        db.RecurringItems.Add(item);
        await db.SaveChangesAsync(ct);

        var line = await Issue(facts, operation, item, anchor, PurchasePurpose.Initial, anchor.AddDays(Settings.InitialMembershipGraceDays), ct);
        request.InvoiceId = line.InvoiceId;

        await Expire(facts, operation, scheduler, ct);
        await AllocateCredit(facts, operation, anchor, ct);
        await Finish(facts, scheduler, true, ct);
    }

    private async Task<InvoiceLine> Issue(BillingFacts facts, BillingOperation operation, RecurringItem item, DateOnly start,
        PurchasePurpose purpose, DateOnly expires, CancellationToken ct)
    {
        var group = facts.Groups.Single(x => x.Id == item.BillingGroupId);
        var fee = facts.Fees.Single(x => x.Id == item.FeeDefinitionId);
        if (fee.Retired)
            throw new ValidationException("The fee is retired.");

        var benefit = item.MembershipId is null ? Benefit.Service : Benefit.Membership;
        var coveredPeriods = facts.Periods(benefit, fee.ServiceDefinitionId).ToList();
        foreach (var period in coveredPeriods.OrderBy(x => x.Start))
            if (period.Start <= start && start < period.End)
                start = period.End;

        var mergeShortBridge = purpose is PurchasePurpose.Alignment || (benefit == Benefit.Service && purpose == PurchasePurpose.Initial);
        var billingPeriod = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, start, mergeShortBridge);
        var purchaseKey = $"item:{item.Id}:{purpose}:{start:yyyy-MM-dd}:{billingPeriod.End:yyyy-MM-dd}" + (purpose == PurchasePurpose.Resumption ? $":{operation.Id}" : "");
        var existingLine = facts.Lines.SingleOrDefault(x => x.PurchaseKey == purchaseKey);
        if (existingLine is not null)
            return existingLine;

        var prices = facts.Prices.Where(x => x.FeeDefinitionId == fee.Id).ToList();
        var discounts = facts.Discounts.Where(x => x.FeeDefinitionId == fee.Id).ToList();
        var quote = Pricing.Calculate(group.Anchor, group.IntervalMonths, billingPeriod, prices, discounts, coveredPeriods);

        var invoice = facts.Invoices.SingleOrDefault(x => x.BillingGroupId == group.Id && x.ClosedAt is null);
        if (invoice is null)
        {
            invoice = new() { BillingAccountId = facts.Account.Id, BillingGroupId = group.Id, IssuedAt = clock.Now };
            facts.Invoices.Add(invoice);
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);
        }

        var chargeDueOn = purpose is PurchasePurpose.Initial or PurchasePurpose.Resumption
            ? (benefit == Benefit.Service && billingPeriod.End < expires ? billingPeriod.End : expires) : start;
        var pricingJson = JsonSerializer.Serialize(quote.Segments);

        var line = new InvoiceLine
        {
            InvoiceId = invoice.Id,
            RecurringItemId = item.Id,
            ServiceEnrollmentId = item.ServiceEnrollmentId,
            ServiceDefinitionId = fee.ServiceDefinitionId,
            FeeDefinitionId = fee.Id,
            Benefit = benefit,
            Purpose = purpose,
            PurchaseKey = purchaseKey,
            Description = fee.Name,
            Category = fee.Category,
            StartsOn = billingPeriod.Start,
            EndsOn = billingPeriod.End,
            OriginalStartsOn = billingPeriod.Start,
            OriginalEndsOn = billingPeriod.End,
            ChargeDueOn = chargeDueOn,
            ExpiresOn = expires,
            Amount = quote.Amount,
            PricingJson = pricingJson,
            OriginalPricingJson = pricingJson
        };

        facts.Lines.Add(line);
        db.InvoiceLines.Add(line);
        item.NextOn = line.EndsOn;
        invoice.Revision++;
        await db.SaveChangesAsync(ct);
        operation.ResultId ??= line.Id;

        if (line.Amount == 0)
            Complete(facts, operation, line, null);
        return line;
    }

    private void Complete(BillingFacts facts, BillingOperation operation, InvoiceLine line, DateOnly? on)
    {
        Live(facts);
        if (line.State != LineState.Open || facts.Held(line) < line.Amount)
            throw new InvalidOperationException("Only fully funded open lines can complete.");
        var enrollmentAuthorizesPurchase = line.ServiceEnrollmentId is not { } enrollmentId
            || facts.Enrollments.Any(x => x.Id == enrollmentId && x.EndedAt is null && x.RenewalStoppedAt is null);
        if (!enrollmentAuthorizesPurchase)
            throw new ValidationException("This subscription no longer authorizes purchases.");

        Move(facts, operation, MovementKind.LinePurchase, line.Amount, on ?? clock.Today, line.Id);
        line.State = LineState.Fulfilled;
        line.FulfilledAt = clock.Now;
        line.EffectiveOn = on;

        if (line.Benefit != Benefit.OneOff)
        {
            var segments = JsonSerializer.Deserialize<List<PriceSegment>>(line.PricingJson)!;
            foreach (var period in segments.Select(x => new Period(x.Start, x.End)).Distinct())
                Grant(facts, operation, line.Benefit, line.ServiceDefinitionId, period, CoverageSource.Purchase, $"purchase:{line.Id}:{period.Start:yyyy-MM-dd}", line.Id);
        }

        if (line.ServiceEnrollmentId is { } id)
            facts.Enrollments.Single(x => x.Id == id).FirstPaidOn ??= on ?? clock.Today;
        facts.Invoices.Single(x => x.Id == line.InvoiceId).Revision++;
    }

    private void CloseLine(BillingFacts facts, BillingOperation operation, InvoiceLine line, LineState state)
    {
        if (line.State != LineState.Open)
            return;

        // Release unspent money before the line leaves Open; held must return to zero.
        Move(facts, operation, MovementKind.LineToBilling, facts.Held(line), clock.Today, line.Id);
        line.State = state;
        facts.Invoices.Single(x => x.Id == line.InvoiceId).Revision++;
    }

    private void Requote(BillingFacts facts, BillingOperation operation, InvoiceLine line, Quote quote, Period period)
    {
        var pricingJson = JsonSerializer.Serialize(quote.Segments);
        var isUnchanged = line.Amount == quote.Amount
            && line.StartsOn == period.Start
            && line.EndsOn == period.End
            && line.PricingJson == pricingJson;
        if (isUnchanged)
            return;

        db.LineAdjustments.Add(new()
        {
            InvoiceLineId = line.Id,
            OperationId = operation.Id,
            OldAmount = line.Amount,
            NewAmount = quote.Amount,
            OldStart = line.StartsOn,
            OldEnd = line.EndsOn,
            NewStart = period.Start,
            NewEnd = period.End,
            PricingJson = line.PricingJson
        });
        if (line.RecurringItemId is { } itemId)
        {
            var item = facts.Items.Single(x => x.Id == itemId);
            if (item.NextOn == line.EndsOn)
                item.NextOn = period.End;
        }

        line.Amount = quote.Amount;
        line.StartsOn = period.Start;
        line.EndsOn = period.End;
        line.PricingJson = pricingJson;
        if (facts.Held(line) > line.Amount)
            Move(facts, operation, MovementKind.LineToBilling, facts.Held(line) - line.Amount, clock.Today, line.Id);
        facts.Invoices.Single(x => x.Id == line.InvoiceId).Revision++;
    }

    private async Task Finish(BillingFacts facts, IScheduler scheduler, bool notify, CancellationToken ct)
    {
        foreach (var invoice in facts.Invoices)
        {
            var openLines = facts.Lines.Where(x => x.InvoiceId == invoice.Id && x.State == LineState.Open).ToList();
            var hasOpenLines = openLines.Count > 0;
            invoice.DueOn = hasOpenLines ? openLines.Min(x => x.ChargeDueOn) : null;
            invoice.ClosedAt = hasOpenLines ? null : invoice.ClosedAt ?? clock.Now;

            var needsInvoiceEmail = notify
                && invoice.ClosedAt is null
                && invoice.NotifiedRevision != invoice.Revision
                && facts.Account.EmailInvoices;
            if (needsInvoiceEmail)
            {
                await mail.Add(scheduler, Guid.NewGuid(), MailKind.Invoice, facts.Account.UserId, invoiceId: invoice.Id, invoiceRevision: invoice.Revision, ct: ct);
                invoice.NotifiedRevision = invoice.Revision;
            }
        }

        // Derived balances may never go negative or leave money held on a closed line.
        var balances = facts.Balances;
        if (balances.BillingCredit < 0 || balances.PurchaseBalance < 0 || facts.Lines.Any(x => facts.Held(x) < 0 || (x.State != LineState.Open && facts.Held(x) != 0)))
            throw new InvalidOperationException("Journal reconciliation failed.");
        await db.SaveChangesAsync(ct);
    }
}
