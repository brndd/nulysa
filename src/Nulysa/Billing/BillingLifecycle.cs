using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Nulysa.Accounts;
using Nulysa.Communications;
using Nulysa.Operations;
using Nulysa.Services;
using Quartz;

namespace Nulysa.Billing;

public sealed partial class BillingCommands
{
    public async Task Scan(long accountId, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var facts = await Lock(accountId, ct);
        if (facts.User is not { MayLogIn: true })
            return;

        var request = await db.InitialMembershipRequests.SingleOrDefaultAsync(x => x.BillingAccountId == accountId, ct);
        if (request is { InvoiceId: null, CancelledAt: null })
        {
            await Initialize(facts.Account, request.Anchor, request.RequestedAt, scheduler, ct);
            await tx.CommitAsync(ct);
            return;
        }

        // A scan is a wake-up; logical purchase keys and persisted dates prevent replayed cycles.
        var operation = (await Operation(facts, Guid.NewGuid(), "due-work", new { on = clock.Today }, null, "Dated billing maintenance", ct))!;
        foreach (var item in facts.Items.Where(x => x.EndedAt is null).OrderBy(x => x.MembershipId is null).ToList())
        {
            var fee = facts.Fees.Single(x => x.Id == item.FeeDefinitionId);
            if (fee.Retired)
                continue;

            var enrollmentAuthorizesPurchase = item.ServiceEnrollmentId is not { } enrollmentId
                || facts.Enrollments.Any(x => x.Id == enrollmentId && x.EndedAt is null && x.RenewalStoppedAt is null && x.FirstPaidOn != null);
            if (!enrollmentAuthorizesPurchase)
                continue;

            var group = facts.Groups.Single(x => x.Id == item.BillingGroupId);
            // Bounded catch-up also establishes missed fully discounted periods without fake payments.
            for (var count = 0; count < 24; count++)
            {
                if (facts.Lines.Any(x => x.RecurringItemId == item.Id && x.State == LineState.Open))
                    break;

                var nextStart = item.NextOn;
                var coveredPeriods = facts.Periods(item.MembershipId is null ? Benefit.Service : Benefit.Membership, fee.ServiceDefinitionId).OrderBy(x => x.Start).ToList();
                foreach (var period in coveredPeriods)
                    if (period.Start <= nextStart && nextStart < period.End)
                        nextStart = period.End;

                if (nextStart > clock.Today.AddDays(Settings.InvoiceLeadDays))
                    break;

                var previousLine = facts.Lines.Where(x => x.RecurringItemId == item.Id).OrderByDescending(x => x.Id).FirstOrDefault();
                var previousPeriodHasGap = previousLine is { State: LineState.Expired or LineState.Cancelled }
                    && BillingCalendar.Uncovered(new(previousLine.StartsOn, previousLine.EndsOn), coveredPeriods).Count > 0;
                if (previousPeriodHasGap)
                    break;

                var membershipIsActive = facts.Membership(clock.Today).Active;
                if (item.MembershipId is null && !membershipIsActive)
                    break;

                var prices = facts.Prices.Where(x => x.FeeDefinitionId == fee.Id).ToList();
                var discounts = facts.Discounts.Where(x => x.FeeDefinitionId == fee.Id).ToList();
                var billingPeriod = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, nextStart, true);
                var quote = Pricing.Calculate(group.Anchor, group.IntervalMonths, billingPeriod, prices, discounts, coveredPeriods);
                var graceDays = item.MembershipId is not null
                    ? Settings.MembershipRenewalGraceDays
                    : facts.Services.Single(x => x.Id == fee.ServiceDefinitionId).RenewalGraceDays;
                var renewalExpiresOn = BillingCalendar.ExclusiveExpiry(nextStart, graceDays);
                var shouldStopCatchUp = quote.Amount > 0 && (!membershipIsActive || renewalExpiresOn <= clock.Today);
                if (shouldStopCatchUp)
                    break;

                var isPeriodStart = BillingCalendar.Containing(group.Anchor, group.IntervalMonths, nextStart).Start == nextStart;
                var purpose = isPeriodStart ? PurchasePurpose.Renewal : PurchasePurpose.Alignment;
                await Issue(facts, operation, item, nextStart, purpose, renewalExpiresOn, ct);
            }
        }

        await Expire(facts, operation, scheduler, ct);
        await AllocateCredit(facts, operation, clock.Today, ct);
        await Finish(facts, scheduler, true, ct);

        // Avoid a journal of empty minute ticks.
        var hasJournaledWork = operation.ResultId is not null
            || db.ChangeTracker.Entries().Any(x => (x.State is EntityState.Added or EntityState.Modified) && x.Entity is not BillingOperation)
            || facts.Movements.Any(x => x.OperationId == operation.Id)
            || facts.Coverage.Any(x => x.OperationId == operation.Id)
            || await db.LineAdjustments.AnyAsync(x => x.OperationId == operation.Id, ct)
            || await db.ServiceChanges.AnyAsync(x => x.OperationId == operation.Id, ct);
        if (!hasJournaledWork)
            db.BillingOperations.Remove(operation);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task Expire(BillingFacts facts, BillingOperation operation, IScheduler scheduler, CancellationToken ct, HashSet<long>? preserved = null)
    {
        foreach (var line in facts.Lines.Where(x => x.State == LineState.Open).ToList())
        {
            if (preserved?.Contains(line.Id) == true)
                continue;

            if (clock.Today >= line.ExpiresOn)
            {
                CloseLine(facts, operation, line, LineState.Expired);
                var needsInactiveNotice = line.Benefit == Benefit.Membership && line.ExpiryNotifiedAt is null;
                if (needsInactiveNotice)
                {
                    await mail.Add(scheduler, Guid.NewGuid(), MailKind.MembershipInactive, facts.Account.UserId, invoiceId: line.InvoiceId, ct: ct);
                    line.ExpiryNotifiedAt = clock.Now;
                }
                continue;
            }

            var needsGraceNotice = line.Benefit == Benefit.Membership
                && line.Purpose is not (PurchasePurpose.Initial or PurchasePurpose.Resumption)
                && clock.Today > line.ChargeDueOn
                && line.GraceNotifiedAt is null;
            if (needsGraceNotice)
            {
                await mail.Add(scheduler, Guid.NewGuid(), MailKind.MembershipGrace, facts.Account.UserId, invoiceId: line.InvoiceId, ct: ct);
                line.GraceNotifiedAt = clock.Now;
                continue;
            }

            var initialServicePeriodEnded = line.Benefit == Benefit.Service
                && line.Purpose == PurchasePurpose.Initial
                && clock.Today >= line.EndsOn;
            if (!initialServicePeriodEnded)
                continue;

            var group = facts.Groups.Single(x => x.Id == facts.Invoices.Single(i => i.Id == line.InvoiceId).BillingGroupId);
            var period = BillingCalendar.Containing(group.Anchor, group.IntervalMonths, clock.Today);
            var quote = Pricing.Calculate(group.Anchor, group.IntervalMonths, period, facts.Prices.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList(),
                facts.Discounts.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList(), facts.Periods(line.Benefit, line.ServiceDefinitionId));
            Requote(facts, operation, line, quote, period);
            // Rollover is not a new funding event; partial funds cannot buy time just by waiting.
            var isFullyDiscounted = quote.Amount == 0 && quote.Segments.Count > 0;
            if (isFullyDiscounted)
                Complete(facts, operation, line, null);
        }

        foreach (var enrollment in facts.Enrollments.Where(x => x.EndedAt is null).ToList())
        {
            if (facts.Lines.Any(x => x.ServiceEnrollmentId == enrollment.Id && preserved?.Contains(x.Id) == true))
                continue;

            var coveredPeriods = facts.Periods(Benefit.Service, enrollment.ServiceDefinitionId).ToList();
            var hasRemainingCoverage = coveredPeriods.Any(x => x.End > clock.Today);
            var hasValidInitial = facts.Lines.Any(x => x.ServiceEnrollmentId == enrollment.Id && x.Purpose == PurchasePurpose.Initial && x.State == LineState.Open && clock.Today < x.ExpiresOn);
            var graceEnd = facts.ServiceGraceEnd(enrollment, clock.Today);
            var graceExpired = graceEnd is null || graceEnd <= clock.Today;
            if (hasRemainingCoverage || hasValidInitial || !graceExpired)
                continue;

            var endReason = enrollment.RenewalStoppedAt is null ? "nonpayment" : "stopped";
            EndService(facts, operation, enrollment, endReason, LineState.Expired);
        }
    }

    private void EndService(BillingFacts facts, BillingOperation operation, ServiceEnrollment enrollment, string cause, LineState lineState)
    {
        enrollment.EndedAt = clock.Now;
        enrollment.EndReason = cause;

        foreach (var item in facts.Items.Where(x => x.ServiceEnrollmentId == enrollment.Id))
            item.EndedAt = clock.Now;
        foreach (var line in facts.Lines.Where(x => x.ServiceEnrollmentId == enrollment.Id))
            CloseLine(facts, operation, line, lineState);

        db.ServiceChanges.Add(new() { ServiceEnrollmentId = enrollment.Id, OperationId = operation.Id, Action = cause });
    }

    public Task<long?> Subscribe(ClaimsPrincipal actor, long accountId, long serviceId, int months, bool grantConfiguredDays, Guid key, CancellationToken ct = default)
        => Change(actor, accountId, key, "subscribe", new { serviceId, months, grantConfiguredDays }, "Service subscription", false, async (facts, operation, scheduler) =>
        {
            Live(facts);
            var service = facts.Services.SingleOrDefault(x => x.Id == serviceId && !x.Retired) ?? throw new ValidationException("Service is unavailable.");

            var isAdministrator = await Authority.IsAdministrator(db, actor, ct);
            if ((!service.SelfSubscriptionAllowed || grantConfiguredDays) && !isAdministrator)
                throw new UnauthorizedAccessException();
            if (!facts.Membership(clock.Today).Active)
                throw new ValidationException("Active membership is required to subscribe.");
            if (facts.Enrollments.Any(x => x.ServiceDefinitionId == serviceId && x.EndedAt is null))
                throw new ValidationException("Already subscribed.");

            var isFirstEnrollment = !facts.Enrollments.Any(x => x.ServiceDefinitionId == serviceId);
            var enrollment = new ServiceEnrollment { UserId = facts.User!.Id, ServiceDefinitionId = serviceId, StartedAt = clock.Now };
            facts.Enrollments.Add(enrollment);
            db.ServiceEnrollments.Add(enrollment);
            await db.SaveChangesAsync(ct);

            operation.ResultId = enrollment.Id;
            db.ServiceChanges.Add(new() { ServiceEnrollmentId = enrollment.Id, OperationId = operation.Id, Action = grantConfiguredDays ? "subscribe-admin-free-days" : "subscribe" });
            if ((isFirstEnrollment || grantConfiguredDays) && service.InitialFreeDays > 0)
                Grant(facts, operation, Benefit.Service, serviceId, new(clock.Today, clock.Today.AddDays(service.InitialFreeDays)),
                    isFirstEnrollment ? CoverageSource.InitialFree : CoverageSource.AdministratorGrant,
                    isFirstEnrollment ? $"intro:{accountId}:{serviceId}" : $"admin-intro:{key}");

            var anchor = (await db.Members.SingleAsync(x => x.UserId == facts.User.Id, ct)).JoinedOn;
            var group = await Group(facts, months, anchor, ct);
            var fee = facts.Fees.Single(x => x.ServiceDefinitionId == serviceId);
            var item = new RecurringItem { BillingGroupId = group.Id, FeeDefinitionId = fee.Id, ServiceEnrollmentId = enrollment.Id, StartsOn = clock.Today, NextOn = clock.Today };
            facts.Items.Add(item);
            db.RecurringItems.Add(item);
            await db.SaveChangesAsync(ct);

            await Issue(facts, operation, item, clock.Today, PurchasePurpose.Initial, clock.Today.AddDays(Settings.ServicePaymentWindowDays), ct);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);

    public Task StopService(ClaimsPrincipal actor, long accountId, long enrollmentId, bool immediate, Guid key, string reason, CancellationToken ct = default)
    {
        Reason(reason);
        return Change(actor, accountId, key, immediate ? "remove-service" : "stop-renewal", new { enrollmentId, immediate }, reason, immediate, async (facts, operation, scheduler) =>
        {
            Live(facts);
            var enrollment = facts.Enrollments.SingleOrDefault(x => x.Id == enrollmentId && x.EndedAt is null) ?? throw new ValidationException("Subscription is no longer current.");

            if (immediate)
                EndService(facts, operation, enrollment, "administrator-removal", LineState.Cancelled);
            else
            {
                enrollment.RenewalStoppedAt = clock.Now;
                foreach (var item in facts.Items.Where(x => x.ServiceEnrollmentId == enrollmentId))
                    item.EndedAt = clock.Now;
                foreach (var line in facts.Lines.Where(x => x.ServiceEnrollmentId == enrollmentId))
                    CloseLine(facts, operation, line, LineState.Cancelled);

                db.ServiceChanges.Add(new() { ServiceEnrollmentId = enrollmentId, OperationId = operation.Id, Action = "stop-renewal" });
                if (!facts.Periods(Benefit.Service, enrollment.ServiceDefinitionId).Any(x => x.End > clock.Today))
                    EndService(facts, operation, enrollment, "stopped", LineState.Cancelled);
            }

            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);
    }

    public Task ChangeInterval(ClaimsPrincipal actor, long accountId, long itemId, int months, Guid key, CancellationToken ct = default)
        => Change(actor, accountId, key, "interval", new { itemId, months }, "Billing interval selection", false, async (facts, operation, scheduler) =>
        {
            Live(facts);
            var item = facts.Items.SingleOrDefault(x => x.Id == itemId && x.EndedAt is null) ?? throw new ValidationException("Billing instruction is unavailable.");

            var anchor = (await db.Members.SingleAsync(x => x.UserId == facts.User!.Id, ct)).JoinedOn;
            item.BillingGroupId = (await Group(facts, months, anchor, ct)).Id;
        }, ct);

    public Task ResumeMembership(ClaimsPrincipal actor, long accountId, Guid key, CancellationToken ct = default)
        => Change(actor, accountId, key, "resume-membership", new { accountId }, "Return request", !Settings.AutomaticMembershipReactivation, async (facts, operation, scheduler) =>
        {
            Live(facts);
            await Expire(facts, operation, scheduler, ct);
            if (facts.Membership(clock.Today).Active || facts.Lines.Any(x => x.Benefit == Benefit.Membership && x.State == LineState.Open))
                throw new ValidationException("Membership is active or already has a return request.");

            var item = facts.Items.Where(x => x.MembershipId == facts.User!.Id).OrderByDescending(x => x.Id).First();
            item.EndedAt = null;

            await Issue(facts, operation, item, clock.Today, PurchasePurpose.Resumption, clock.Today.AddDays(Settings.InitialMembershipGraceDays), ct);
            await AllocateCredit(facts, operation, clock.Today, ct);
        }, ct);

    public async Task CancelForDeletion(long accountId, Guid key, long actor, string reason, IScheduler scheduler, CancellationToken ct)
    {
        var facts = await BillingFacts.Load(db, accountId, ct);
        var operation = await Operation(facts, key, "membership-ended", new { accountId }, actor, reason, ct);
        if (operation is null)
            return;

        foreach (var enrollment in facts.Enrollments.Where(x => x.EndedAt is null))
            EndService(facts, operation, enrollment, "membership-ended", LineState.Cancelled);
        foreach (var item in facts.Items)
            item.EndedAt ??= clock.Now;
        foreach (var line in facts.Lines)
            CloseLine(facts, operation, line, LineState.Cancelled);

        await Finish(facts, scheduler, false, ct);
    }

    // Restoration owns the account/user locks and outer transaction.
    public async Task RestoreMembershipBilling(long accountId, Guid key, long actor, string reason, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Restoration requires an outer transaction.");

        var facts = await BillingFacts.Load(db, accountId, ct);
        Live(facts);

        var nextUncoveredStart = clock.Today;
        foreach (var period in facts.Periods(Benefit.Membership, null).OrderBy(x => x.Start))
            if (period.Start <= nextUncoveredStart && nextUncoveredStart < period.End)
                nextUncoveredStart = period.End;
        if (nextUncoveredStart == clock.Today)
            return;

        var previousItem = facts.Items.Where(x => x.MembershipId == facts.User!.Id).OrderByDescending(x => x.Id).FirstOrDefault();
        if (previousItem is null || previousItem.EndedAt is null)
            return;

        var operation = await Operation(facts, key, "restore-membership-billing", new { accountId }, actor, reason, ct);
        if (operation is null)
            return;

        // Restoration re-arms billing, not coverage; the next invoice starts after paid time.
        var item = new RecurringItem
        {
            BillingGroupId = previousItem.BillingGroupId,
            FeeDefinitionId = previousItem.FeeDefinitionId,
            MembershipId = previousItem.MembershipId,
            StartsOn = clock.Today,
            NextOn = nextUncoveredStart
        };
        db.RecurringItems.Add(item);
        await db.SaveChangesAsync(ct);
        operation.ResultId = item.Id;
    }

    public async Task ErasePersonalRecords(long accountId, CancellationToken ct)
    {
        var facts = await BillingFacts.Load(db, accountId, ct);
        if (facts.Balances.BillingCredit + facts.Balances.PurchaseBalance > 0)
        {
            // Keep only what is needed to honour an unspent refund claim.
            facts.Account.ClaimName = facts.User!.Name;
            facts.Account.ClaimEmail = facts.User.Email;
        }

        db.RecurringItems.RemoveRange(facts.Items);
        await db.SaveChangesAsync(ct);
        db.ServiceEnrollments.RemoveRange(facts.Enrollments);
        await db.SaveChangesAsync(ct);
    }
}

[DisallowConcurrentExecution]
public sealed class BillingDueWorkJob(IServiceScopeFactory scopes, TimeProvider time, Nulysa.Data.AppDbContext db,
    Nulysa.Integrations.BankFeed bank, ILogger<BillingDueWorkJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        long after = 0;
        var failures = new List<Exception>();
        while (true)
        {
            var accounts = await db.BillingAccounts.AsNoTracking().Where(x => x.UserId != null && x.Id > after).OrderBy(x => x.Id).Select(x => x.Id).Take(50).ToListAsync(cancellationToken);
            if (accounts.Count == 0)
                break;
            foreach (var id in accounts)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<BillingCommands>().Scan(id, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    // One broken account must not stop due work for every account after it.
                    logger.LogError(error, "Billing due work failed for account {AccountId}.", id);
                    failures.Add(error);
                }
            }
            after = accounts[^1];
        }
        await bank.CheckStale(cancellationToken);
        await Observations.Record(db, "billing-due-work", time.GetUtcNow(), cancellationToken);
        if (failures.Count > 0)
            throw new AggregateException("Billing due work failed for one or more accounts; the rest were processed.", failures);
    }
}
