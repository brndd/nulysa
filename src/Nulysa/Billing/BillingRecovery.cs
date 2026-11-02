using Nulysa.Services;

namespace Nulysa.Billing;

public sealed partial class BillingCommands
{
    private static bool IsAutomaticEndReason(ServiceEndReason? reason) => reason is ServiceEndReason.Nonpayment or ServiceEndReason.ServiceRetired;

    private void ReopenEndedServicesForTimelyReceipt(BillingFacts facts, BillingOperation operation, DateOnly receivedOn, long groupId)
    {
        var enrollmentIds = facts.Enrollments
            .Where(enrollment => CanReopenUnissuedService(facts, enrollment, receivedOn, groupId))
            .Select(enrollment => enrollment.Id)
            .ToList();

        foreach (var id in enrollmentIds)
            ReopenService(facts, operation, facts.Enrollments.Single(x => x.Id == id));
    }

    private static bool HasNewerEnrollment(BillingFacts facts, ServiceEnrollment enrollment)
        => facts.Enrollments.Any(newer => newer.Id != enrollment.Id
            && newer.ServiceDefinitionId == enrollment.ServiceDefinitionId
            && newer.StartedAt >= enrollment.StartedAt);

    private bool CanReopenUnissuedService(BillingFacts facts, ServiceEnrollment enrollment, DateOnly receivedOn, long groupId)
    {
        var automaticallyEnded = enrollment.EndedAt is not null
            && enrollment.EndReason == ServiceEndReason.Nonpayment
            && enrollment.RenewalStoppedAt is null;
        if (!automaticallyEnded)
            return false;

        var belongsToGroup = facts.Items.Any(item => item.ServiceEnrollmentId == enrollment.Id && item.BillingGroupId == groupId);
        if (!belongsToGroup)
            return false;

        // Existing expired requests use AssessRecovery; this path repairs a renewal never issued.
        var hasClosedRequests = facts.Lines.Any(line => line.ServiceEnrollmentId == enrollment.Id
            && line.State is LineState.Expired or LineState.Cancelled);
        if (hasClosedRequests || HasNewerEnrollment(facts, enrollment))
            return false;

        var serviceIsOffered = !facts.Services.Single(service => service.Id == enrollment.ServiceDefinitionId).Retired
            && facts.Fees.SingleOrDefault(fee => fee.ServiceDefinitionId == enrollment.ServiceDefinitionId) is { Retired: false };
        if (!serviceIsOffered)
            return false;

        return facts.ServiceGraceEndExclusive(enrollment, receivedOn) is { } cutoff
            && receivedOn < cutoff
            && facts.Membership(receivedOn).Active;
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

    private enum RecoveryBlocker { MembershipEnded, LaterPurchase, SubscriptionClosed, NewerSubscription, ReleasedFundsUnavailable }
    private sealed record RecoveryDecision(RecoveryBlocker? Blocker = null)
    {
        public bool Allowed => Blocker is null;
    }

    // Assess first, restore second: a timely bank fact must not undo a later deliberate decision.
    private RecoveryDecision AssessRecovery(BillingFacts facts, InvoiceLine line)
    {
        var membershipItemEnded = line.Benefit == Benefit.Membership
            && line.RecurringItemId is { } membershipItem
            && facts.Items.Single(x => x.Id == membershipItem).EndedAt is not null;
        if (membershipItemEnded)
            return new(RecoveryBlocker.MembershipEnded);

        var laterLineExists = facts.Lines.Any(x => x.Id != line.Id
            && x.RecurringItemId == line.RecurringItemId
            && x.RecurringItemId != null
            && x.StartsOn >= line.StartsOn
            && x.State is LineState.Open or LineState.Fulfilled);
        if (laterLineExists)
            return new(RecoveryBlocker.LaterPurchase);

        if (line.ServiceEnrollmentId is { } id)
        {
            var enrollment = facts.Enrollments.Single(x => x.Id == id);
            var enrollmentClosed = enrollment.RenewalStoppedAt is not null
                || (enrollment.EndedAt is not null && !IsAutomaticEndReason(enrollment.EndReason));
            var newerEnrollmentExists = HasNewerEnrollment(facts, enrollment);
            if (enrollmentClosed)
                return new(RecoveryBlocker.SubscriptionClosed);
            if (newerEnrollmentExists)
                return new(RecoveryBlocker.NewerSubscription);
        }

        var release = facts.Movements
            .Where(x => x.InvoiceLineId == line.Id && x.Kind == MovementKind.LineToBilling)
            .OrderByDescending(x => x.Id)
            .FirstOrDefault();
        if (release is null)
            return new();

        // Recovery must not spend money that was already reallocated away from this line.
        var moneyStillAvailable = facts.Balances.BillingCredit >= release.Amount;
        var laterSpendingExists = facts.Movements.Any(x => x.Id > release.Id
            && x.Kind is MovementKind.BillingToLine or MovementKind.BillingToPurchase or MovementKind.BillingRefund);
        return moneyStillAvailable && !laterSpendingExists ? new() : new(RecoveryBlocker.ReleasedFundsUnavailable);
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
}
