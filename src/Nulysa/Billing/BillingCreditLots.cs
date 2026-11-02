namespace Nulysa.Billing;

public sealed partial class BillingCommands
{
    // Receipt-derived credit keeps its original date. A missed renewal may be replayed from a lot
    // received before that renewal's cutoff, but the journal movement stays on the replay date.
    private async Task RecoverMissedRenewalsWithLots(BillingFacts facts, BillingOperation operation, DateOnly on, CancellationToken ct)
    {
        if (!facts.CreditLots.Any(x => x.RemainingAmount > 0))
            return;

        var items = facts.Items
            .Where(x => x.EndedAt is null
                || (x.ServiceEnrollmentId is { } enrollmentId
                    && IsAutomaticEndReason(facts.Enrollments.Single(e => e.Id == enrollmentId).EndReason)))
            .ToList();

        // Recover the earliest missed period first, then membership before services on the same bill.
        var remaining = items;
        for (var count = 0; count < 24 * items.Count; count++)
        {
            if (!facts.CreditLots.Any(x => x.RemainingAmount > 0))
                return;
            var next = remaining
                .Select(item => (item, start: NextMissedStart(facts, item)))
                .Where(x => x.start <= on)
                .OrderBy(x => x.start)
                .ThenBy(x => x.item.MembershipUserId is null)
                .ThenBy(x => x.item.MembershipUserId is null
                    ? facts.Services.Single(s => s.Id == facts.Fees.Single(f => f.Id == x.item.FeeDefinitionId).ServiceDefinitionId).Priority
                    : 0)
                .ThenBy(x => x.item.Id)
                .FirstOrDefault();
            if (next.item is null)
                return;
            var recovered = await RecoverOneMissedRenewal(facts, operation, next.item, on, ct);
            if (!recovered)
                remaining = remaining.Where(x => x.Id != next.item.Id).ToList();
        }
    }

    private static DateOnly NextMissedStart(BillingFacts facts, RecurringItem item)
    {
        var fee = facts.Fees.Single(x => x.Id == item.FeeDefinitionId);
        var benefit = item.MembershipUserId is null ? Benefit.Service : Benefit.Membership;
        var lastLine = facts.Lines
            .Where(x => x.RecurringItemId == item.Id)
            .OrderByDescending(x => x.StartsOn)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        if (lastLine is not null && lastLine.State is (LineState.Open or LineState.Expired)
            && lastLine.Purpose is (PurchasePurpose.Renewal or PurchasePurpose.Alignment))
            return lastLine.StartsOn;
        var nextStart = item.NextUnissuedPeriodStartsOn;
        foreach (var period in facts.Periods(benefit, fee.ServiceDefinitionId).OrderBy(x => x.Start))
            if (period.Start <= nextStart && nextStart < period.EndExclusive)
                nextStart = period.EndExclusive;
        return nextStart;
    }

    private async Task<bool> RecoverOneMissedRenewal(BillingFacts facts, BillingOperation operation, RecurringItem item, DateOnly on, CancellationToken ct)
    {
        var fee = facts.Fees.Single(x => x.Id == item.FeeDefinitionId);
        var service = fee.ServiceDefinitionId is { } serviceId ? facts.Services.Single(x => x.Id == serviceId) : null;
        var benefit = item.MembershipUserId is null ? Benefit.Service : Benefit.Membership;
        var group = facts.Groups.Single(x => x.Id == item.BillingGroupId);
        var lastLine = facts.Lines
            .Where(x => x.RecurringItemId == item.Id)
            .OrderByDescending(x => x.StartsOn)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        if (lastLine is null)
            return false;

        var line = lastLine.State is (LineState.Open or LineState.Expired)
            && lastLine.Purpose is (PurchasePurpose.Renewal or PurchasePurpose.Alignment)
            ? lastLine : null;
        DateOnly nextStart;
        DateOnly cutoff;
        decimal amount;
        if (line is not null)
        {
            nextStart = line.StartsOn;
            cutoff = line.PaymentCutoffExclusive;
            if (cutoff > on)
                return false;
            amount = line.State == LineState.Open ? line.Amount - facts.Held(line) : line.Amount;
            if (amount <= 0)
                return false;
            if (line.State == LineState.Expired && !AssessRecovery(facts, line).Allowed)
                return false;
        }
        else
        {
            if (lastLine.State is LineState.Cancelled or LineState.Open or LineState.Expired)
                return false;
            if (fee.Retired || service is { Retired: true })
                return false;

            nextStart = item.NextUnissuedPeriodStartsOn;
            foreach (var period in facts.Periods(benefit, fee.ServiceDefinitionId).OrderBy(x => x.Start))
                if (period.Start <= nextStart && nextStart < period.EndExclusive)
                    nextStart = period.EndExclusive;
            if (nextStart > on)
                return false;

            var graceDays = item.MembershipUserId is not null ? Settings.MembershipRenewalGraceDays : service!.RenewalGraceDays;
            cutoff = BillingCalendar.PaymentCutoffExclusive(nextStart, graceDays);
            if (cutoff > on)
                return false;

            var billingPeriod = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, nextStart, true);
            var quote = Pricing.Calculate(group.Anchor, group.IntervalMonths, billingPeriod,
                facts.Prices.Where(x => x.FeeDefinitionId == fee.Id).ToList(),
                facts.Discounts.Where(x => x.FeeDefinitionId == fee.Id).ToList(),
                facts.Periods(benefit, fee.ServiceDefinitionId));
            amount = quote.Amount;
            if (amount <= 0)
                return false;
        }

        // Today's balance is insufficient evidence: the required lots must predate the cutoff.
        var replay = ReplayDate(facts, nextStart, cutoff, amount);
        if (replay is null)
            return false;

        if (item.MembershipUserId is null)
        {
            var enrollment = facts.Enrollments.Single(x => x.Id == item.ServiceEnrollmentId);
            if (enrollment.RenewalStoppedAt is not null)
                return false;
            if (enrollment.EndedAt is not null)
            {
                var hasLifecycleConflict = !IsAutomaticEndReason(enrollment.EndReason)
                    || HasNewerEnrollment(facts, enrollment)
                    || facts.Lines.Any(x => x.Id != line?.Id && x.ServiceEnrollmentId == enrollment.Id && x.State is LineState.Expired or LineState.Cancelled);
                if (hasLifecycleConflict)
                    return false;
                ReopenService(facts, operation, enrollment, item.Id);
            }
            if (!facts.Membership(replay.Value).Active)
                return false;
        }

        if (line is { State: LineState.Expired })
            Recover(facts, operation, line);
        else if (line is null)
            line = await Issue(facts, operation, item, nextStart,
                BillingCalendar.Containing(group.Anchor, group.IntervalMonths, nextStart).Start == nextStart
                    ? PurchasePurpose.Renewal : PurchasePurpose.Alignment,
                cutoff, ct);

        var toFund = line.State == LineState.Open ? Math.Min(amount, line.Amount - facts.Held(line)) : 0;
        if (toFund <= 0)
            return false;
        await Fund(facts, operation, line, toFund, replay.Value, null, ct);
        return true;
    }

    private DateOnly? ReplayDate(BillingFacts facts, DateOnly nextStart, DateOnly cutoff, decimal amount)
    {
        var replay = nextStart.AddDays(-Settings.InvoiceLeadDays);
        var remaining = amount;
        DateOnly? latest = null;
        foreach (var lot in facts.CreditLots.Where(x => x.RemainingAmount > 0 && x.ReceivedOn < cutoff)
            .OrderBy(x => x.ReceivedOn).ThenBy(x => x.Id))
        {
            if (remaining <= 0)
                break;
            var used = Math.Min(lot.RemainingAmount, remaining);
            remaining -= used;
            latest = lot.ReceivedOn;
        }
        if (remaining > 0 || latest is null)
            return null;
        var replayed = replay > latest ? replay : latest.Value;
        return replayed < cutoff ? replayed : null;
    }
}
