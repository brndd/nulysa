using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Accounts;
using Nulysa.Services;
using Quartz;

namespace Nulysa.Billing;

public sealed record ReceiptInput(string Source, string SourceAccount, string ExternalId, decimal Amount, string Currency,
    DateOnly ReceivedOn, string Reference, string Message = "", string Counterparty = "");

public sealed partial class BillingCommands
{
    public async Task<long> Import(ReceiptInput input, CancellationToken ct = default)
    {
        var hasStableSource = !string.IsNullOrWhiteSpace(input.Source) && input.Source.Length <= 40
            && !string.IsNullOrWhiteSpace(input.SourceAccount) && input.SourceAccount.Length <= 200
            && !string.IsNullOrWhiteSpace(input.ExternalId) && input.ExternalId.Length <= 200;
        var hasValidMoney = input.Amount != 0
            && Math.Abs(input.Amount) <= 9999999999999999m
            && Pricing.Round(input.Amount) == input.Amount
            && input.Currency.Length == 3;
        var hasValidDateAndText = input.ReceivedOn <= clock.Today
            && input.Reference.Length <= 200
            && input.Message.Length <= 4000
            && input.Counterparty.Length <= 1000;
        if (!hasStableSource || !hasValidMoney || !hasValidDateAndText)
            throw new ValidationException("Invalid booked transaction: provide stable source IDs, currency, receipt date and a nonzero cent amount.");

        long id;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Serialize on the provider identity so concurrent reimports cannot create duplicate cash.
            var sourceLockKey = JsonSerializer.Serialize(new { input.Source, input.SourceAccount, input.ExternalId });
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({sourceLockKey}, 0))", ct);

            var existingReceipt = await db.Receipts.SingleOrDefaultAsync(x => x.Source == input.Source && x.SourceAccount == input.SourceAccount && x.ExternalId == input.ExternalId, ct);
            if (existingReceipt is not null)
            {
                if (existingReceipt.Amount != input.Amount || existingReceipt.Currency != input.Currency || existingReceipt.ReceivedOn != input.ReceivedOn ||
                    existingReceipt.Reference != input.Reference || existingReceipt.Message != input.Message || existingReceipt.Counterparty != input.Counterparty)
                    throw new ValidationException("Conflicting source transaction; reconcile with the provider instead of overwriting it.");
                id = existingReceipt.Id;
            }
            else
            {
                var receipt = new Receipt
                {
                    Source = input.Source,
                    SourceAccount = input.SourceAccount,
                    ExternalId = input.ExternalId,
                    Amount = input.Amount,
                    Currency = input.Currency,
                    ReceivedOn = input.ReceivedOn,
                    ImportedAt = clock.Now,
                    Reference = input.Reference,
                    Message = input.Message,
                    Counterparty = input.Counterparty
                };
                db.Receipts.Add(receipt);
                await db.SaveChangesAsync(ct);
                id = receipt.Id;
            }

            await tx.CommitAsync(ct);
        }

        // Matching has its own transaction; a review outcome must persist even when no money moves.
        await Match(id, ct: ct);
        return id;
    }

    public async Task<long> ImportManual(ClaimsPrincipal actor, ReceiptInput input, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await Import(input, ct);
    }

    public async Task AssignReceipt(ClaimsPrincipal actor, long receiptId, long accountId, long? groupId, bool purchaseDeposit,
        Guid operation, string reason, CancellationToken ct = default, long? invoiceId = null)
    {
        Reason(reason);
        await Match(receiptId, actor, accountId, groupId, purchaseDeposit, operation, reason, ct, invoiceId);
    }

    private async Task Match(long receiptId, ClaimsPrincipal? actor = null, long? manualAccount = null, long? manualGroup = null,
        bool manualDeposit = false, Guid operationKey = default, string reason = "Reference matching", CancellationToken ct = default, long? manualInvoice = null)
    {
        var sourceReceipt = await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == receiptId, ct);
        if (actor is null && sourceReceipt.State != ReceiptState.New)
            return;

        var normalizedReference = PaymentReference.Normalize(sourceReceipt.Reference);
        var group = await db.BillingGroups.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentReference == normalizedReference, ct);
        var oneOff = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.OneOffReference == normalizedReference, ct);
        var deposit = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.PurchaseDepositReference == normalizedReference, ct);
        var accountId = manualAccount ?? group?.BillingAccountId ?? oneOff?.BillingAccountId ?? deposit?.Id;

        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        BillingFacts? facts = accountId is { } account ? await Lock(account, ct) : null;

        // The pre-lock state is only a filter; re-read the source row under the account lock.
        foreach (var entry in db.ChangeTracker.Entries<Receipt>().ToList())
            entry.State = EntityState.Detached;
        var receipt = await db.Receipts.FromSqlInterpolated($"SELECT * FROM \"Receipts\" WHERE \"Id\" = {receiptId} FOR UPDATE").SingleAsync(ct);

        if (actor is not null)
            await Authority.RequireAdministrator(db, actor, ct);
        if (actor is null && receipt.State != ReceiptState.New)
            return;

        async Task Review(string reviewReason)
        {
            receipt.State = ReceiptState.Review;
            receipt.ReviewReason = reviewReason;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var toDeposit = actor is null ? deposit is not null : manualDeposit;
        var groupId = actor is null ? group?.Id : manualGroup;
        var invoiceId = actor is null ? oneOff?.Id : manualInvoice;
        object operationInput = invoiceId is null
            ? new { receiptId, accountId, groupId, toDeposit }
            : new { receiptId, accountId, groupId, toDeposit, invoiceId };

        if (receipt.State == ReceiptState.Posted)
        {
            // Posted source facts are immutable; only an explicit reversal may free the allocation.
            if (actor is not null && (facts is null || await Operation(facts, operationKey, "receipt", operationInput, Authority.Id(actor), reason, ct) is not null))
                throw new ValidationException("This receipt is posted. Reverse an eligible unspent allocation before reassignment.");
            return;
        }

        var canPost = facts is not null
            && facts.User is { MayLogIn: true }
            && receipt.Amount >= 0
            && receipt.Currency == "EUR";
        if (!canPost)
        {
            if (actor is not null)
                throw new ValidationException("Only incoming EUR receipts can be assigned to a live member.");

            await Review(facts?.User is { PendingDeletionAt: not null }
                ? "Account pending deletion — manual repair required after restoration"
                : "Unknown reference, unavailable account, outgoing transaction or unsupported currency");
            return;
        }

        var billingFacts = facts!;
        bool LineIsInDestination(InvoiceLine line) => groupId is not null
            ? billingFacts.Invoices.Single(i => i.Id == line.InvoiceId).BillingGroupId == groupId
            : line.InvoiceId == invoiceId;

        var invoiceIsOneOff = invoiceId is not null && billingFacts.Invoices.Any(x => x.Id == invoiceId && x.BillingGroupId is null);
        if (invoiceId is not null && (groupId is not null || toDeposit || !invoiceIsOneOff))
            throw new ValidationException("Select a one-off invoice belonging to this account, without a group or deposit destination.");
        if (groupId is not null && !billingFacts.Groups.Any(x => x.Id == groupId))
            throw new ValidationException("The selected group belongs to another account.");
        var depositConflict = toDeposit && (!Settings.PurchaseBalanceEnabled || groupId is not null);
        if (depositConflict)
        {
            if (actor is not null)
                throw new ValidationException("Purchase deposits are unavailable or conflict with a group selection.");

            await Review("Purchase balance is disabled");
            return;
        }

        var operation = await Operation(billingFacts, actor is null ? Guid.NewGuid() : operationKey, "receipt", operationInput, actor is null ? null : Authority.Id(actor), reason, ct);
        if (operation is null)
            return;
        operation.ResultId = receiptId;

        if (!toDeposit)
        {
            var recoverableLines = billingFacts.Lines
                .Where(x => x.State == LineState.Expired && receipt.ReceivedOn < x.ExpiresOn && LineIsInDestination(x))
                .ToList();
            if (recoverableLines.Any(x => !CanRecover(billingFacts, x)))
            {
                await Review("Timely delayed receipt conflicts with later allocation or explicit lifecycle changes");
                return;
            }
            foreach (var line in recoverableLines)
                Recover(billingFacts, operation, line);

            var timelyLineIds = billingFacts.Lines
                .Where(x => x.State == LineState.Open && receipt.ReceivedOn < x.ExpiresOn && LineIsInDestination(x))
                .Select(x => x.Id)
                .ToHashSet();
            await Expire(billingFacts, operation, scheduler, ct, timelyLineIds, notifyInactiveWithoutLine: true);

            // A timely receipt can settle a renewal the worker has not issued yet.
            if (groupId is { } matchedGroup)
            {
                ReopenEndedServicesForTimelyReceipt(billingFacts, operation, receipt.ReceivedOn, matchedGroup);
                await IssueDueRenewals(billingFacts, operation, receipt.ReceivedOn, matchedGroup, ct);
            }

            var needsMembershipResumption = groupId is not null
                && Settings.AutomaticMembershipReactivation
                && !billingFacts.Membership(receipt.ReceivedOn).Active
                && !billingFacts.Lines.Any(x => x.Benefit == Benefit.Membership && x.State == LineState.Open);
            if (needsMembershipResumption)
            {
                var membership = billingFacts.Items.Where(x => x.MembershipId is not null).OrderByDescending(x => x.Id).FirstOrDefault();
                if (membership is not null && membership.BillingGroupId == groupId)
                {
                    membership.EndedAt = null;
                    await Issue(billingFacts, operation, membership, receipt.ReceivedOn, PurchasePurpose.Resumption,
                        receipt.ReceivedOn.AddDays(Settings.InitialMembershipGraceDays), ct);
                }
            }
        }

        receipt.BillingAccountId = billingFacts.Account.Id;
        receipt.State = ReceiptState.Posted;
        receipt.ReviewReason = null;

        if (toDeposit)
            Move(billingFacts, operation, MovementKind.ReceiptToPurchase, receipt.Amount, receipt.ReceivedOn, receipt: receipt.Id);
        else
        {
            var remaining = receipt.Amount;
            var destinationInvoices = billingFacts.Invoices.Where(x => groupId is not null ? x.BillingGroupId == groupId : x.Id == invoiceId).ToList();
            foreach (var line in OrderedLines(billingFacts, destinationInvoices))
            {
                if (remaining == 0)
                    break;
                remaining -= await Fund(billingFacts, operation, line, remaining, receipt.ReceivedOn, receipt.Id, ct);
            }

            // Overpayment remains billing credit; it never disappears from the journal.
            Move(billingFacts, operation, MovementKind.ReceiptToBilling, remaining, receipt.ReceivedOn, receipt: receipt.Id);
            await Expire(billingFacts, operation, scheduler, ct, notifyInactiveWithoutLine: true);
            await AllocateCredit(billingFacts, operation, clock.Today, ct);
        }

        await Finish(billingFacts, scheduler, false, ct);
        await tx.CommitAsync(ct);
    }

    private static bool IsAutomaticEndReason(string? reason) => reason is "nonpayment" or "service-retired";

    private void ReopenEndedServicesForTimelyReceipt(BillingFacts facts, BillingOperation operation, DateOnly receivedOn, long groupId)
    {
        var enrollmentIds = facts.Enrollments
            .Where(x => x.EndedAt is not null
                && x.EndReason == "nonpayment"
                && x.RenewalStoppedAt is null
                && facts.Items.Any(item => item.ServiceEnrollmentId == x.Id && item.BillingGroupId == groupId)
                && !facts.Lines.Any(line => line.ServiceEnrollmentId == x.Id && line.State is LineState.Expired or LineState.Cancelled)
                && !facts.Enrollments.Any(newer => newer.Id != x.Id
                    && newer.ServiceDefinitionId == x.ServiceDefinitionId
                    && newer.StartedAt >= x.StartedAt)
                && !facts.Services.Single(service => service.Id == x.ServiceDefinitionId).Retired
                && facts.Fees.SingleOrDefault(fee => fee.ServiceDefinitionId == x.ServiceDefinitionId) is { Retired: false }
                && facts.ServiceGraceEnd(x, receivedOn) is { } cutoff
                && receivedOn < cutoff
                && facts.Membership(receivedOn).Active)
            .Select(x => x.Id)
            .ToList();

        foreach (var id in enrollmentIds)
            ReopenService(facts, operation, facts.Enrollments.Single(x => x.Id == id));
    }

    private void ReopenService(BillingFacts facts, BillingOperation operation, ServiceEnrollment enrollment, long? recurringItemId = null)
    {
        var wasEnded = enrollment.EndedAt is not null;
        enrollment.EndedAt = null;
        enrollment.EndReason = null;
        var items = recurringItemId is { } id
            ? facts.Items.Where(x => x.Id == id)
            : facts.Items.Where(x => x.ServiceEnrollmentId == enrollment.Id).OrderByDescending(x => x.Id).Take(1);
        foreach (var item in items)
            item.EndedAt = null;
        if (wasEnded)
            db.ServiceChanges.Add(new() { ServiceEnrollmentId = enrollment.Id, OperationId = operation.Id, Action = "timely-receipt-correction" });
    }

    private bool CanRecover(BillingFacts facts, InvoiceLine line)
    {
        var membershipItemEnded = line.Benefit == Benefit.Membership
            && line.RecurringItemId is { } membershipItem
            && facts.Items.Single(x => x.Id == membershipItem).EndedAt is not null;
        if (membershipItemEnded)
            return false;

        var laterLineExists = facts.Lines.Any(x => x.Id != line.Id
            && x.RecurringItemId == line.RecurringItemId
            && x.RecurringItemId != null
            && x.StartsOn >= line.StartsOn
            && x.State is LineState.Open or LineState.Fulfilled);
        if (laterLineExists)
            return false;

        if (line.ServiceEnrollmentId is { } id)
        {
            var enrollment = facts.Enrollments.Single(x => x.Id == id);
            var enrollmentClosed = enrollment.RenewalStoppedAt is not null
                || (enrollment.EndedAt is not null && !IsAutomaticEndReason(enrollment.EndReason));
            var newerEnrollmentExists = facts.Enrollments.Any(x => x.Id != id
                && x.ServiceDefinitionId == enrollment.ServiceDefinitionId
                && x.StartedAt >= enrollment.StartedAt);
            if (enrollmentClosed || newerEnrollmentExists)
                return false;
        }

        var release = facts.Movements
            .Where(x => x.InvoiceLineId == line.Id && x.Kind == MovementKind.LineToBilling)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
        if (release is null)
            return true;

        // Recovery must not spend money that was already reallocated away from this line.
        var moneyStillAvailable = facts.Balances.BillingCredit >= release.Amount;
        var laterSpendingExists = facts.Movements.Any(x => x.Id > release.Id
            && x.Kind is MovementKind.BillingToLine or MovementKind.BillingToPurchase or MovementKind.BillingRefund);
        return moneyStillAvailable && !laterSpendingExists;
    }

    private void Recover(BillingFacts facts, BillingOperation operation, InvoiceLine line)
    {
        if (line.State != LineState.Expired)
            return;

        line.State = LineState.Open;
        if (line.ServiceEnrollmentId is { } enrollmentId)
            ReopenService(facts, operation, facts.Enrollments.Single(x => x.Id == enrollmentId), line.RecurringItemId);

        var release = facts.Movements
            .Where(x => x.InvoiceLineId == line.Id && x.Kind == MovementKind.LineToBilling)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
        if (release is not null && facts.Held(line) == 0)
            Move(facts, operation, MovementKind.BillingToLine, release.Amount, line.LastFundingOn ?? release.EffectiveOn, line.Id);
    }

    private IEnumerable<InvoiceLine> OrderedLines(BillingFacts facts, IEnumerable<Invoice> invoices)
    {
        var byEarliestOpenCharge = invoices
            .OrderBy(invoice => facts.Lines
                .Where(line => line.InvoiceId == invoice.Id && line.State == LineState.Open)
                .Min(line => (DateOnly?)line.ChargeDueOn))
            .ThenBy(invoice => invoice.IssuedAt)
            .ThenBy(invoice => invoice.Id);

        return byEarliestOpenCharge.SelectMany(invoice => facts.Lines
            .Where(line => line.InvoiceId == invoice.Id && line.State == LineState.Open)
            .OrderBy(line => line.Benefit == Benefit.Membership
                ? -1
                : facts.Services.SingleOrDefault(service => service.Id == line.ServiceDefinitionId)?.Priority ?? int.MaxValue)
            .ThenBy(line => line.ChargeDueOn)
            .ThenBy(line => line.Id))
            .ToList();
    }

    private async Task<decimal> Fund(BillingFacts facts, BillingOperation operation, InvoiceLine line, decimal available, DateOnly on, long? receiptId, CancellationToken ct)
    {
        var effectiveDate = line.LastFundingOn is { } previousFunding && previousFunding > on ? previousFunding : on;
        var canFund = line.State == LineState.Open
            && effectiveDate < line.ExpiresOn
            && (line.ServiceEnrollmentId is not { } enrollmentId
                || facts.Enrollments.Any(x => x.Id == enrollmentId && x.EndedAt is null && x.RenewalStoppedAt is null));
        if (!canFund)
            return 0;

        var heldBefore = facts.Held(line);
        var isPricedAtFunding = line.Purpose == PurchasePurpose.Resumption
            || (line.Purpose == PurchasePurpose.Initial && line.Benefit == Benefit.Service);
        if (isPricedAtFunding)
        {
            var pricingJson = line.PricingJson;
            if (effectiveDate < line.StartsOn && line.StartsOn > line.OriginalStartsOn)
            {
                var quoteDate = effectiveDate > line.OriginalStartsOn ? effectiveDate : line.OriginalStartsOn;
                pricingJson = await db.LineAdjustments
                    .Where(x => x.InvoiceLineId == line.Id && x.OldStart <= quoteDate && quoteDate < x.OldEnd)
                    .OrderByDescending(x => x.Id)
                    .Select(x => x.PricingJson)
                    .FirstOrDefaultAsync(ct)
                    ?? line.OriginalPricingJson;
            }

            var segments = JsonSerializer.Deserialize<List<PriceSegment>>(pricingJson)!;
            var quote = Pricing.Slice(segments, effectiveDate, facts.Periods(line.Benefit, line.ServiceDefinitionId));
            var needsRepricing = quote.Segments.Count == 0
                || (line.Purpose == PurchasePurpose.Resumption && effectiveDate < line.OriginalStartsOn);
            if (needsRepricing)
            {
                var group = facts.Groups.Single(x => x.Id == facts.Invoices.Single(i => i.Id == line.InvoiceId).BillingGroupId);
                var period = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, effectiveDate, false);
                var unquotedEnd = quote.Segments.Count == 0 || period.End < quote.Segments[0].Start
                    ? period.End
                    : quote.Segments[0].Start;
                var discounts = facts.Discounts
                    .Where(x => x.FeeDefinitionId == line.FeeDefinitionId)
                    .Select(x =>
                    {
                        var end = x.WithdrawnAt is { } withdrawn
                            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(withdrawn, clock.Zone).DateTime)
                            : x.EndsOn;
                        return new Discount { Id = x.Id, StartsOn = x.StartsOn, EndsOn = end < x.EndsOn ? end : x.EndsOn, Kind = x.Kind, Value = x.Value };
                    })
                    .Where(x => x.StartsOn < x.EndsOn)
                    .ToList();
                var prices = facts.Prices.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList();
                var prefix = Pricing.Calculate(group.Anchor, group.IntervalMonths, new(effectiveDate, unquotedEnd), prices,
                    discounts, facts.Periods(line.Benefit, line.ServiceDefinitionId));
                quote = Pricing.FromSegments(prefix.Segments.Concat(quote.Segments
                    .Where(x => x.Start < period.End)
                    .Select(x => x with { End = x.End > period.End ? period.End : x.End })));
            }

            if (quote.Segments.Count > 0 && heldBefore + available >= quote.Amount)
                Requote(facts, operation, line, quote, new(quote.Segments.Min(x => x.Start), quote.Segments.Max(x => x.End)));
        }

        var fundingAmount = Math.Min(available, Math.Max(0, line.Amount - facts.Held(line)));
        Move(facts, operation, receiptId is null ? MovementKind.BillingToLine : MovementKind.ReceiptToLine, fundingAmount, on, line.Id, receiptId);
        if (fundingAmount > 0)
            line.LastFundingOn = effectiveDate;
        if (facts.Held(line) == line.Amount)
            Complete(facts, operation, line, line.Amount == 0 ? null : effectiveDate);

        await db.SaveChangesAsync(ct);
        return fundingAmount;
    }

    private async Task AllocateCredit(BillingFacts facts, BillingOperation operation, DateOnly on, CancellationToken ct)
    {
        foreach (var line in OrderedLines(facts, facts.Invoices.Where(x => x.BillingGroupId is not null)))
        {
            var available = facts.Balances.BillingCredit;
            if (available <= 0)
                break;

            await Fund(facts, operation, line, available, on, null, ct);
        }
    }

    public async Task IgnoreReceipt(ClaimsPrincipal actor, long id, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Authority.RequireAdministrator(db, actor, ct);
        var receipt = await db.Receipts.FromSqlInterpolated($"SELECT * FROM \"Receipts\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (receipt.State == ReceiptState.Posted)
            throw new ValidationException("Posted receipts cannot be ignored.");
        receipt.State = ReceiptState.Ignored;
        receipt.ReviewReason = $"Administrator #{Authority.Id(actor)}: {reason}";
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<long?> Change(ClaimsPrincipal actor, long accountId, Guid key, string kind, object input, string reason,
        bool administrator, Func<BillingFacts, BillingOperation, IScheduler, Task> change, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var facts = await Lock(accountId, ct);

        if (administrator)
            await Authority.RequireAdministrator(db, actor, ct);
        else
            await Authority.RequireOwnerOrAdministrator(db, actor, facts.Account.UserId ?? 0, ct);

        var operation = await Operation(facts, key, kind, input, Authority.Id(actor), reason, ct);
        if (operation is null)
            return (await db.BillingOperations.SingleAsync(x => x.Id == key, ct)).ResultId;

        await change(facts, operation, scheduler);
        await Finish(facts, scheduler, true, ct);
        await tx.CommitAsync(ct);
        return operation.ResultId;
    }

    public Task Transfer(ClaimsPrincipal actor, long accountId, Guid key, decimal amount, bool toPurchase, CancellationToken ct = default)
        => Change(actor, accountId, key, "transfer", new { amount, toPurchase }, "Member balance transfer", false, async (facts, operation, scheduler) =>
        {
            Live(facts);
            if (!Settings.PurchaseBalanceEnabled)
                throw new ValidationException("Purchase balance is disabled.");

            var available = toPurchase ? facts.Balances.BillingCredit : facts.Balances.PurchaseBalance;
            var isPositiveCentAmount = amount > 0 && Pricing.Round(amount) == amount;
            if (!isPositiveCentAmount || amount > available)
                throw new ValidationException("Transfer requires a positive cent amount within the available balance.");

            Move(facts, operation, toPurchase ? MovementKind.BillingToPurchase : MovementKind.PurchaseToBilling, amount, clock.Today);
            if (!toPurchase)
                await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);

    public Task Refund(ClaimsPrincipal actor, long accountId, Guid key, decimal amount, bool purchase, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "record-refund", new { amount, purchase }, reason, true, (facts, operation, scheduler) =>
        {
            var available = purchase ? facts.Balances.PurchaseBalance : facts.Balances.BillingCredit;
            var isPositiveCentAmount = amount > 0 && Pricing.Round(amount) == amount;
            if (!isPositiveCentAmount || amount > available)
                throw new ValidationException("Record only a confirmed refund within the available balance; held and spent funds are unavailable.");

            Move(facts, operation, purchase ? MovementKind.PurchaseRefund : MovementKind.BillingRefund, amount, clock.Today);
            if (facts.Balances is { BillingCredit: 0, PurchaseBalance: 0, Held: 0 })
            {
                facts.Account.ClaimName = null;
                facts.Account.ClaimEmail = null;
            }
            return Task.CompletedTask;
        }, ct);
    }

    public Task UnassignReceipt(ClaimsPrincipal actor, long accountId, long receiptId, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "unassign-receipt", new { receiptId }, reason, true, async (facts, operation, scheduler) =>
        {
            var receipt = await db.Receipts.FromSqlInterpolated($"SELECT * FROM \"Receipts\" WHERE \"Id\" = {receiptId} FOR UPDATE").SingleAsync(ct);
            if (receipt.State != ReceiptState.Posted || receipt.BillingAccountId != accountId)
                throw new ValidationException("Select a posted receipt assigned to this account.");

            var postedAllocations = facts.Movements
                .Where(x => x.ReceiptId == receiptId && x.Kind is MovementKind.ReceiptToLine or MovementKind.ReceiptToBilling or MovementKind.ReceiptToPurchase)
                .Where(x => !facts.Movements.Any(reversal => reversal.ReversesMovementId == x.Id))
                .ToList();
            if (postedAllocations.Any(x => x.InvoiceLineId is { } lineId && facts.Lines.Single(line => line.Id == lineId).State == LineState.Fulfilled))
                throw new ValidationException("The receipt funded a completed purchase. Treasurer review is required; this command does not revoke purchased time or create debt.");

            foreach (var lineId in postedAllocations.Where(x => x.InvoiceLineId != null).Select(x => x.InvoiceLineId!.Value).Distinct())
                CloseLine(facts, operation, facts.Lines.Single(x => x.Id == lineId), LineState.Cancelled);

            var purchaseAmount = postedAllocations.Where(x => x.Kind == MovementKind.ReceiptToPurchase).Sum(x => x.Amount);
            var billingAmount = postedAllocations.Where(x => x.Kind != MovementKind.ReceiptToPurchase).Sum(x => x.Amount);
            if (purchaseAmount > facts.Balances.PurchaseBalance || billingAmount > facts.Balances.BillingCredit)
                throw new ValidationException("The receipt's money is no longer available. Treasurer reconciliation is required; no negative balance or clawback was created.");

            foreach (var movement in postedAllocations)
                Move(facts, operation, movement.Kind == MovementKind.ReceiptToPurchase ? MovementKind.PurchaseReceiptReversal : MovementKind.BillingReceiptReversal,
                    movement.Amount, clock.Today, receipt: receiptId, reverses: movement.Id);

            // Reverse into review, never silently drop the source fact.
            receipt.BillingAccountId = null;
            receipt.State = ReceiptState.Review;
            receipt.ReviewReason = "Allocation reversed by administrator — explicit purpose assignment required";
            operation.ResultId = receiptId;
            if (facts.User is null && facts.Balances is { BillingCredit: 0, PurchaseBalance: 0, Held: 0 })
            {
                facts.Account.ClaimName = null;
                facts.Account.ClaimEmail = null;
            }
        }, ct);
    }
}
