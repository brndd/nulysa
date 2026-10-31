using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Accounts;
using Quartz;

namespace Nulysa.Billing;

public sealed record ReceiptInput(string Source, string SourceAccount, string ExternalId, decimal Amount, string Currency,
    DateOnly ReceivedOn, string Reference, string Message = "", string Counterparty = "");

public sealed partial class BillingCommands
{
    public async Task<long> Import(ReceiptInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Source) || input.Source.Length > 40 || string.IsNullOrWhiteSpace(input.SourceAccount) || input.SourceAccount.Length > 200 ||
            string.IsNullOrWhiteSpace(input.ExternalId) || input.ExternalId.Length > 200 || input.Amount == 0 || Math.Abs(input.Amount) > 9999999999999999m ||
            Pricing.Round(input.Amount) != input.Amount || input.Currency.Length != 3 || input.ReceivedOn > clock.Today ||
            input.Reference.Length > 200 || input.Message.Length > 4000 || input.Counterparty.Length > 1000)
            throw new ValidationException("Invalid booked transaction: provide stable source IDs, currency, receipt date and a nonzero cent amount.");
        long id;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var sourceKey = JsonSerializer.Serialize(new { input.Source, input.SourceAccount, input.ExternalId });
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({sourceKey}, 0))", ct);
            var existing = await db.Receipts.SingleOrDefaultAsync(x => x.Source == input.Source && x.SourceAccount == input.SourceAccount && x.ExternalId == input.ExternalId, ct);
            if (existing is not null)
            {
                if (existing.Amount != input.Amount || existing.Currency != input.Currency || existing.ReceivedOn != input.ReceivedOn ||
                    existing.Reference != input.Reference || existing.Message != input.Message || existing.Counterparty != input.Counterparty)
                    throw new ValidationException("Conflicting source transaction; reconcile with the provider instead of overwriting it.");
                id = existing.Id;
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
                db.Receipts.Add(receipt); await db.SaveChangesAsync(ct); id = receipt.Id;
            }
            await tx.CommitAsync(ct);
        }
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
        var source = await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == receiptId, ct);
        if (actor is null && source.State != ReceiptState.New) return;
        var reference = PaymentReference.Normalize(source.Reference);
        var group = await db.BillingGroups.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentReference == reference, ct);
        var oneOff = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.OneOffReference == reference, ct);
        var deposit = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.PurchaseDepositReference == reference, ct);
        var accountId = manualAccount ?? group?.BillingAccountId ?? oneOff?.BillingAccountId ?? deposit?.Id;
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        BillingFacts? facts = accountId is { } account ? await Lock(account, ct) : null;
        db.ChangeTracker.Entries<Receipt>().ToList().ForEach(x => x.State = EntityState.Detached);
        var receipt = await db.Receipts.FromSqlInterpolated($"SELECT * FROM \"Receipts\" WHERE \"Id\" = {receiptId} FOR UPDATE").SingleAsync(ct);
        if (actor is not null) await Authority.RequireAdministrator(db, actor, ct);
        if (actor is null && receipt.State != ReceiptState.New) return;
        var toDeposit = actor is null ? deposit is not null : manualDeposit;
        var groupId = actor is null ? group?.Id : manualGroup;
        var invoiceId = actor is null ? oneOff?.Id : manualInvoice;
        object input = invoiceId is null ? new { receiptId, accountId, groupId, toDeposit } : new { receiptId, accountId, groupId, toDeposit, invoiceId };
        if (receipt.State == ReceiptState.Posted)
        {
            if (actor is not null && (facts is null || await Operation(facts, operationKey, "receipt", input, Authority.Id(actor), reason, ct) is not null))
                throw new ValidationException("This receipt is posted. Reverse an eligible unspent allocation before reassignment.");
            return;
        }
        if (facts is null || facts.User is not { MayLogIn: true } || receipt.Amount < 0 || receipt.Currency != "EUR")
        {
            if (actor is not null) throw new ValidationException("Only incoming EUR receipts can be assigned to a live member.");
            receipt.State = ReceiptState.Review;
            receipt.ReviewReason = facts?.User is { PendingDeletionAt: not null } ? "Account pending deletion — manual repair required after restoration" : "Unknown reference, unavailable account, outgoing transaction or unsupported currency";
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
        }
        if (invoiceId is not null && (groupId is not null || toDeposit || !facts.Invoices.Any(x => x.Id == invoiceId && x.BillingGroupId is null)))
            throw new ValidationException("Select a one-off invoice belonging to this account, without a group or deposit destination.");
        if (groupId is not null && !facts.Groups.Any(x => x.Id == groupId)) throw new ValidationException("The selected group belongs to another account.");
        if (toDeposit && (!Settings.PurchaseBalanceEnabled || groupId is not null))
        {
            if (actor is not null) throw new ValidationException("Purchase deposits are unavailable or conflict with a group selection.");
            receipt.State = ReceiptState.Review; receipt.ReviewReason = "Purchase balance is disabled";
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
        }
        var operation = await Operation(facts, actor is null ? Guid.NewGuid() : operationKey, "receipt", input, actor is null ? null : Authority.Id(actor), reason, ct);
        if (operation is null) return;
        operation.ResultId = receiptId;
        if (!toDeposit)
        {
            var candidates = facts.Lines.Where(x => x.State == LineState.Expired && receipt.ReceivedOn < x.ExpiresOn &&
                (groupId is not null ? facts.Invoices.Single(i => i.Id == x.InvoiceId).BillingGroupId == groupId : x.InvoiceId == invoiceId)).ToList();
            if (candidates.Any(x => !CanRecover(facts, x)))
            {
                receipt.State = ReceiptState.Review; receipt.ReviewReason = "Timely delayed receipt conflicts with later allocation or explicit lifecycle changes";
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
            }
            foreach (var line in candidates) Recover(facts, operation, line);
            var timely = facts.Lines.Where(x => x.State == LineState.Open && receipt.ReceivedOn < x.ExpiresOn &&
                (groupId is not null ? facts.Invoices.Single(i => i.Id == x.InvoiceId).BillingGroupId == groupId : x.InvoiceId == invoiceId)).Select(x => x.Id).ToHashSet();
            await Expire(facts, operation, scheduler, ct, timely);
            if (groupId is not null && Settings.AutomaticMembershipReactivation && !facts.Membership(receipt.ReceivedOn).Active &&
                !facts.Lines.Any(x => x.Benefit == Benefit.Membership && x.State == LineState.Open))
            {
                var membership = facts.Items.SingleOrDefault(x => x.MembershipId is not null && x.EndedAt is null && x.BillingGroupId == groupId);
                if (membership is not null) await Issue(facts, operation, membership, receipt.ReceivedOn, PurchasePurpose.Resumption,
                    receipt.ReceivedOn.AddDays(Settings.InitialMembershipGraceDays), ct);
            }
        }
        receipt.BillingAccountId = facts.Account.Id; receipt.State = ReceiptState.Posted; receipt.ReviewReason = null;
        if (toDeposit) Move(facts, operation, MovementKind.ReceiptToPurchase, receipt.Amount, receipt.ReceivedOn, receipt: receipt.Id);
        else
        {
            var remaining = receipt.Amount;
            var invoices = facts.Invoices.Where(x => groupId is not null ? x.BillingGroupId == groupId : x.Id == invoiceId).ToList();
            foreach (var line in OrderedLines(facts, invoices))
            {
                if (remaining == 0) break;
                remaining -= await Fund(facts, operation, line, remaining, receipt.ReceivedOn, receipt.Id, ct);
            }
            Move(facts, operation, MovementKind.ReceiptToBilling, remaining, receipt.ReceivedOn, receipt: receipt.Id);
            await Expire(facts, operation, scheduler, ct);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }
        await Finish(facts, scheduler, false, ct); await tx.CommitAsync(ct);
    }

    private bool CanRecover(BillingFacts facts, InvoiceLine line)
    {
        if (line.Benefit == Benefit.Membership && line.RecurringItemId is { } membershipItem && facts.Items.Single(x => x.Id == membershipItem).EndedAt is not null) return false;
        if (facts.Lines.Any(x => x.Id != line.Id && x.RecurringItemId == line.RecurringItemId && x.RecurringItemId != null && x.StartsOn >= line.StartsOn && x.State is LineState.Open or LineState.Fulfilled)) return false;
        if (line.ServiceEnrollmentId is { } id)
        {
            var enrollment = facts.Enrollments.Single(x => x.Id == id);
            if (enrollment.RenewalStoppedAt is not null || (enrollment.EndedAt is not null && enrollment.EndReason != "nonpayment") ||
                facts.Enrollments.Any(x => x.Id != id && x.ServiceDefinitionId == enrollment.ServiceDefinitionId && x.StartedAt >= enrollment.StartedAt)) return false;
        }
        var release = facts.Movements.Where(x => x.InvoiceLineId == line.Id && x.Kind == MovementKind.LineToBilling).OrderByDescending(x => x.Id).FirstOrDefault();
        return release is null || (facts.Balances.BillingCredit >= release.Amount && !facts.Movements.Any(x => x.Id > release.Id &&
            x.Kind is MovementKind.BillingToLine or MovementKind.BillingToPurchase or MovementKind.BillingRefund));
    }

    private void Recover(BillingFacts facts, BillingOperation operation, InvoiceLine line)
    {
        if (line.State != LineState.Expired) return;
        line.State = LineState.Open;
        if (line.ServiceEnrollmentId is { } id)
        {
            var enrollment = facts.Enrollments.Single(x => x.Id == id);
            enrollment.EndedAt = null; enrollment.EndReason = null;
            var item = facts.Items.Single(x => x.Id == line.RecurringItemId); item.EndedAt = null;
            db.ServiceChanges.Add(new() { ServiceEnrollmentId = id, OperationId = operation.Id, Action = "timely-receipt-correction" });
        }
        var release = facts.Movements.Where(x => x.InvoiceLineId == line.Id && x.Kind == MovementKind.LineToBilling).OrderByDescending(x => x.Id).FirstOrDefault();
        if (release is not null && facts.Held(line) == 0)
            Move(facts, operation, MovementKind.BillingToLine, release.Amount, line.LastFundingOn ?? release.EffectiveOn, line.Id);
    }

    private IEnumerable<InvoiceLine> OrderedLines(BillingFacts facts, IEnumerable<Invoice> invoices)
        => invoices.OrderBy(x => facts.Lines.Where(l => l.InvoiceId == x.Id && l.State == LineState.Open).Select(l => (DateOnly?)l.ChargeDueOn).Min())
            .ThenBy(x => x.IssuedAt).ThenBy(x => x.Id).SelectMany(invoice => facts.Lines.Where(x => x.InvoiceId == invoice.Id && x.State == LineState.Open)
                .OrderBy(x => x.Benefit == Benefit.Membership ? -1 : facts.Services.SingleOrDefault(s => s.Id == x.ServiceDefinitionId)?.Priority ?? int.MaxValue)
                .ThenBy(x => x.ChargeDueOn).ThenBy(x => x.Id)).ToList();

    private async Task<decimal> Fund(BillingFacts facts, BillingOperation operation, InvoiceLine line, decimal available, DateOnly on, long? receiptId, CancellationToken ct)
    {
        var effective = line.LastFundingOn is { } previous && previous > on ? previous : on;
        if (line.State != LineState.Open || effective >= line.ExpiresOn) return 0;
        if (line.ServiceEnrollmentId is { } enrollmentId && !facts.Enrollments.Any(x => x.Id == enrollmentId && x.EndedAt is null && x.RenewalStoppedAt is null)) return 0;
        var held = facts.Held(line);
        if (line.Purpose == PurchasePurpose.Resumption || (line.Purpose == PurchasePurpose.Initial && line.Benefit == Benefit.Service))
        {
            var pricing = line.PricingJson;
            if (effective < line.StartsOn && line.StartsOn > line.OriginalStartsOn)
            {
                var quotedOn = effective > line.OriginalStartsOn ? effective : line.OriginalStartsOn;
                pricing = await db.LineAdjustments.Where(x => x.InvoiceLineId == line.Id && x.OldStart <= quotedOn && quotedOn < x.OldEnd)
                    .OrderByDescending(x => x.Id).Select(x => x.PricingJson).FirstOrDefaultAsync(ct) ?? line.OriginalPricingJson;
            }
            var segments = JsonSerializer.Deserialize<List<PriceSegment>>(pricing)!;
            var quote = Pricing.Slice(segments, effective, facts.Periods(line.Benefit, line.ServiceDefinitionId));
            if (quote.Segments.Count == 0 || (line.Purpose == PurchasePurpose.Resumption && effective < line.OriginalStartsOn))
            {
                var group = facts.Groups.Single(x => x.Id == facts.Invoices.Single(i => i.Id == line.InvoiceId).BillingGroupId);
                var period = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, effective, false);
                var unquotedEnd = quote.Segments.Count == 0 || period.End < quote.Segments[0].Start ? period.End : quote.Segments[0].Start;
                var prefix = Pricing.Calculate(group.Anchor, group.IntervalMonths, new(effective, unquotedEnd), facts.Prices.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList(),
                    facts.Discounts.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList(), facts.Periods(line.Benefit, line.ServiceDefinitionId));
                quote = Pricing.FromSegments(prefix.Segments.Concat(quote.Segments.Where(x => x.Start < period.End)
                    .Select(x => x with { End = x.End > period.End ? period.End : x.End })));
            }
            if (quote.Segments.Count > 0 && held + available >= quote.Amount)
                Requote(facts, operation, line, quote, new(quote.Segments.Min(x => x.Start), quote.Segments.Max(x => x.End)));
        }
        var amount = Math.Min(available, Math.Max(0, line.Amount - facts.Held(line)));
        Move(facts, operation, receiptId is null ? MovementKind.BillingToLine : MovementKind.ReceiptToLine, amount, on, line.Id, receiptId);
        if (amount > 0) line.LastFundingOn = effective;
        if (facts.Held(line) == line.Amount) Complete(facts, operation, line, line.Amount == 0 ? null : effective);
        await db.SaveChangesAsync(ct);
        return amount;
    }

    private async Task AllocateCredit(BillingFacts facts, BillingOperation operation, DateOnly on, CancellationToken ct)
    {
        foreach (var line in OrderedLines(facts, facts.Invoices.Where(x => x.BillingGroupId is not null)))
        {
            var available = facts.Balances.BillingCredit;
            if (available <= 0) break;
            await Fund(facts, operation, line, available, on, null, ct);
        }
    }

    public async Task IgnoreReceipt(ClaimsPrincipal actor, long id, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Authority.RequireAdministrator(db, actor, ct);
        var receipt = await db.Receipts.FromSqlInterpolated($"SELECT * FROM \"Receipts\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if (receipt.State == ReceiptState.Posted) throw new ValidationException("Posted receipts cannot be ignored.");
        receipt.State = ReceiptState.Ignored; receipt.ReviewReason = $"Administrator #{Authority.Id(actor)}: {reason}";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }

    private async Task<long?> Change(ClaimsPrincipal actor, long accountId, Guid key, string kind, object input, string reason,
        bool administrator, Func<BillingFacts, BillingOperation, IScheduler, Task> change, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var facts = await Lock(accountId, ct);
        if (administrator) await Authority.RequireAdministrator(db, actor, ct);
        else await Authority.RequireOwnerOrAdministrator(db, actor, facts.Account.UserId ?? 0, ct);
        var operation = await Operation(facts, key, kind, input, Authority.Id(actor), reason, ct);
        if (operation is null) return (await db.BillingOperations.SingleAsync(x => x.Id == key, ct)).ResultId;
        await change(facts, operation, scheduler);
        await Finish(facts, scheduler, true, ct); await tx.CommitAsync(ct);
        return operation.ResultId;
    }

    public Task Transfer(ClaimsPrincipal actor, long accountId, Guid key, decimal amount, bool toPurchase, CancellationToken ct = default)
        => Change(actor, accountId, key, "transfer", new { amount, toPurchase }, "Member balance transfer", false, async (facts, operation, scheduler) =>
        {
            Live(facts);
            if (!Settings.PurchaseBalanceEnabled) throw new ValidationException("Purchase balance is disabled.");
            if (amount <= 0 || Pricing.Round(amount) != amount || amount > (toPurchase ? facts.Balances.BillingCredit : facts.Balances.PurchaseBalance))
                throw new ValidationException("Transfer requires a positive cent amount within the available balance.");
            Move(facts, operation, toPurchase ? MovementKind.BillingToPurchase : MovementKind.PurchaseToBilling, amount, clock.Today);
            if (!toPurchase) await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);

    public Task Refund(ClaimsPrincipal actor, long accountId, Guid key, decimal amount, bool purchase, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "record-refund", new { amount, purchase }, reason, true, (facts, operation, scheduler) =>
        {
            if (amount <= 0 || Pricing.Round(amount) != amount || amount > (purchase ? facts.Balances.PurchaseBalance : facts.Balances.BillingCredit))
                throw new ValidationException("Record only a confirmed refund within the available balance; held and spent funds are unavailable.");
            Move(facts, operation, purchase ? MovementKind.PurchaseRefund : MovementKind.BillingRefund, amount, clock.Today);
            if (facts.Balances is { BillingCredit: 0, PurchaseBalance: 0, Held: 0 }) { facts.Account.ClaimName = null; facts.Account.ClaimEmail = null; }
            return Task.CompletedTask;
        }, ct);
    }

    public Task UnassignReceipt(ClaimsPrincipal actor, long accountId, long receiptId, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, "unassign-receipt", new { receiptId }, reason, true, async (facts, operation, scheduler) =>
        {
            var receipt = await db.Receipts.FromSqlInterpolated($"SELECT * FROM \"Receipts\" WHERE \"Id\" = {receiptId} FOR UPDATE").SingleAsync(ct);
            if (receipt.State != ReceiptState.Posted || receipt.BillingAccountId != accountId) throw new ValidationException("Select a posted receipt assigned to this account.");
            var original = facts.Movements.Where(x => x.ReceiptId == receiptId && x.Kind is MovementKind.ReceiptToLine or MovementKind.ReceiptToBilling or MovementKind.ReceiptToPurchase)
                .Where(x => !facts.Movements.Any(r => r.ReversesMovementId == x.Id)).ToList();
            if (original.Any(x => x.InvoiceLineId is { } lineId && facts.Lines.Single(l => l.Id == lineId).State == LineState.Fulfilled))
                throw new ValidationException("The receipt funded a completed purchase. Treasurer review is required; this command does not revoke purchased time or create debt.");
            foreach (var lineId in original.Where(x => x.InvoiceLineId != null).Select(x => x.InvoiceLineId!.Value).Distinct())
                CloseLine(facts, operation, facts.Lines.Single(x => x.Id == lineId), LineState.Cancelled);
            var purchase = original.Where(x => x.Kind == MovementKind.ReceiptToPurchase).Sum(x => x.Amount);
            var billing = original.Where(x => x.Kind != MovementKind.ReceiptToPurchase).Sum(x => x.Amount);
            if (purchase > facts.Balances.PurchaseBalance || billing > facts.Balances.BillingCredit)
                throw new ValidationException("The receipt's money is no longer available. Treasurer reconciliation is required; no negative balance or clawback was created.");
            foreach (var movement in original)
                Move(facts, operation, movement.Kind == MovementKind.ReceiptToPurchase ? MovementKind.PurchaseReceiptReversal : MovementKind.BillingReceiptReversal,
                    movement.Amount, clock.Today, receipt: receiptId, reverses: movement.Id);
            receipt.BillingAccountId = null; receipt.State = ReceiptState.Review; receipt.ReviewReason = "Allocation reversed by administrator — explicit purpose assignment required";
            operation.ResultId = receiptId;
            if (facts.User is null && facts.Balances is { BillingCredit: 0, PurchaseBalance: 0, Held: 0 }) { facts.Account.ClaimName = null; facts.Account.ClaimEmail = null; }
        }, ct);
    }
}
