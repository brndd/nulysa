using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Accounts;
using Nulysa.Data;
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
            await DatabaseLocks.AcquireForIdentity(db, sourceLockKey, ct);

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

    public async Task AssignReceipt(ClaimsPrincipal actor, long receiptId, long accountId, ReceiptDestination destination,
        Guid operation, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        await Match(receiptId, new(actor, accountId, destination, operation, reason), ct);
    }

    private sealed record ReceiptAssignment(ClaimsPrincipal Actor, long AccountId, ReceiptDestination Destination, Guid OperationKey, string Reason);
    private sealed record ReceiptRoute(long AccountId, ReceiptDestination Destination);

    private async Task<ReceiptRoute?> ResolveReference(string reference, CancellationToken ct)
    {
        var normalizedReference = PaymentReference.Normalize(reference);
        var group = await db.BillingGroups.AsNoTracking().SingleOrDefaultAsync(x => x.PaymentReference == normalizedReference, ct);
        var oneOff = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.OneOffReference == normalizedReference, ct);
        var deposit = await db.BillingAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.PurchaseDepositReference == normalizedReference, ct);
        return group is not null ? new(group.BillingAccountId, new ReceiptDestination.RecurringGroup(group.Id))
            : oneOff is not null ? new(oneOff.BillingAccountId, new ReceiptDestination.OneOffInvoice(oneOff.Id))
            : deposit is not null ? new(deposit.Id, new ReceiptDestination.PurchaseDeposit()) : null;
    }

    private async Task Match(long receiptId, ReceiptAssignment? assignment = null, CancellationToken ct = default)
    {
        var actor = assignment?.Actor;
        var sourceReceipt = await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == receiptId, ct);
        if (actor is null && sourceReceipt.State != ReceiptState.New)
            return;

        var route = await ResolveReference(sourceReceipt.Reference, ct);
        long? accountId = assignment?.AccountId ?? route?.AccountId;
        var destination = assignment?.Destination ?? route?.Destination;
        var operationKey = assignment?.OperationKey ?? Guid.Empty;
        var reason = assignment?.Reason ?? "Reference matching";

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

        var operationInput = destination?.OperationInput(receiptId, accountId);

        if (receipt.State == ReceiptState.Posted)
        {
            // Posted source facts are immutable; only an explicit reversal may free the allocation.
            if (actor is not null && (facts is null || await Operation(facts, operationKey, "receipt", operationInput!, Authority.Id(actor), reason, ct) is not null))
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

            var reviewReason = receipt switch
            {
                { Amount: < 0 } => "Outgoing bank transaction — no member funds allocated",
                { Currency: not "EUR" } => $"Unsupported currency: {receipt.Currency} — only EUR can be allocated",
                _ when facts is null => "Unknown payment reference — no matching bill, billing group or deposit reference",
                _ when facts.User is { PendingDeletionAt: not null } => "Account pending deletion — manual repair required after restoration",
                _ => "Matched account is unavailable — no live admitted member"
            };
            await Review(reviewReason);
            return;
        }

        var billingFacts = facts!;
        ValidateDestination(billingFacts, destination!);
        var depositDisabled = destination is ReceiptDestination.PurchaseDeposit && !Settings.PurchaseBalanceEnabled;
        if (depositDisabled)
        {
            if (actor is not null)
                throw new ValidationException("Purchase deposits are unavailable or conflict with a group selection.");

            await Review("Purchase balance is disabled");
            return;
        }

        var operation = await Operation(billingFacts, actor is null ? Guid.NewGuid() : operationKey, "receipt", operationInput!, actor is null ? null : Authority.Id(actor), reason, ct);
        if (operation is null)
            return;
        operation.ResultId = receiptId;

        var requestsAreReady = destination is ReceiptDestination.PurchaseDeposit
            || await PrepareRequestsForReceipt(billingFacts, operation, receipt, destination!, scheduler, ct);
        if (!requestsAreReady)
        {
            await Review("Timely delayed receipt conflicts with later allocation or explicit lifecycle changes");
            return;
        }

        await PostReceipt(billingFacts, operation, receipt, destination!, scheduler, ct);

        await Finish(billingFacts, scheduler, false, ct);
        await tx.CommitAsync(ct);
    }

    private static void ValidateDestination(BillingFacts facts, ReceiptDestination destination)
    {
        var invalidOneOff = destination is ReceiptDestination.OneOffInvoice oneOff
            && !facts.Invoices.Any(invoice => invoice.Id == oneOff.InvoiceId && invoice.BillingGroupId is null);
        if (invalidOneOff)
            throw new ValidationException("Select a one-off invoice belonging to this account, without a group or deposit destination.");
        var foreignGroup = destination is ReceiptDestination.RecurringGroup group && !facts.Groups.Any(candidate => candidate.Id == group.GroupId);
        if (foreignGroup)
            throw new ValidationException("The selected group belongs to another account.");
    }

    private async Task<bool> PrepareRequestsForReceipt(BillingFacts facts, BillingOperation operation, Receipt receipt,
        ReceiptDestination destination, IScheduler scheduler, CancellationToken ct)
    {
        var recoverableLines = facts.Lines
            .Where(x => x.State == LineState.Expired && receipt.ReceivedOn < x.ExpiresOn && destination.Includes(facts.Invoices.Single(invoice => invoice.Id == x.InvoiceId)))
            .ToList();
        if (recoverableLines.Any(line => !AssessRecovery(facts, line).Allowed))
            return false;
        foreach (var line in recoverableLines)
            Recover(facts, operation, line);

        // Preserve these requests through today's expiry pass: the bank date is still timely.
        var timelyLineIds = facts.Lines
            .Where(x => x.State == LineState.Open && receipt.ReceivedOn < x.ExpiresOn && destination.Includes(facts.Invoices.Single(invoice => invoice.Id == x.InvoiceId)))
            .Select(x => x.Id)
            .ToHashSet();
        // Existing credit lots must repair missed renewals before expiry or resumption decisions.
        await RecoverMissedRenewalsWithLots(facts, operation, clock.Today, ct);
        await Expire(facts, operation, scheduler, ct, timelyLineIds, notifyInactiveWithoutLine: true);

        // A timely receipt can settle a renewal the worker has not issued yet.
        if (destination is ReceiptDestination.RecurringGroup { GroupId: var matchedGroup })
        {
            ReopenEndedServicesForTimelyReceipt(facts, operation, receipt.ReceivedOn, matchedGroup);
            await IssueDueRenewals(facts, operation, receipt.ReceivedOn, matchedGroup, ct);
        }

        var groupId = (destination as ReceiptDestination.RecurringGroup)?.GroupId;
        var needsMembershipResumption = groupId is not null
            && Settings.AutomaticMembershipReactivation
            && !facts.Membership(receipt.ReceivedOn).Active
            && !facts.Lines.Any(x => x.Benefit == Benefit.Membership && x.State == LineState.Open);
        if (needsMembershipResumption)
        {
            var membership = facts.Items
                .Where(item => item.MembershipUserId is not null)
                .OrderByDescending(item => item.Id)
                .FirstOrDefault();
            if (membership is not null && membership.BillingGroupId == groupId)
            {
                membership.EndedAt = null;
                await Issue(facts, operation, membership, receipt.ReceivedOn, PurchasePurpose.Resumption,
                    receipt.ReceivedOn.AddDays(Settings.InitialMembershipGraceDays), ct);
            }
        }

        return true;
    }

    private async Task PostReceipt(BillingFacts facts, BillingOperation operation, Receipt receipt,
        ReceiptDestination destination, IScheduler scheduler, CancellationToken ct)
    {
        receipt.BillingAccountId = facts.Account.Id;
        receipt.State = ReceiptState.Posted;
        receipt.ReviewReason = null;

        if (destination is ReceiptDestination.PurchaseDeposit)
            Move(facts, operation, MovementKind.ReceiptToPurchase, receipt.Amount, receipt.ReceivedOn, receipt: receipt.Id);
        else
        {
            var remaining = receipt.Amount;
            var destinationInvoices = facts.Invoices.Where(destination.Includes).ToList();
            foreach (var line in OrderedLines(facts, destinationInvoices))
            {
                if (remaining == 0)
                    break;
                remaining -= await Fund(facts, operation, line, remaining, receipt.ReceivedOn, receipt.Id, ct);
            }

            // Overpayment remains billing credit; it never disappears from the journal.
            Move(facts, operation, MovementKind.ReceiptToBilling, remaining, receipt.ReceivedOn, receipt: receipt.Id);
            await RecoverMissedRenewalsWithLots(facts, operation, clock.Today, ct);
            await Expire(facts, operation, scheduler, ct, notifyInactiveWithoutLine: true);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }
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
