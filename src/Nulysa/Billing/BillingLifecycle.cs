using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
            if (item.ServiceEnrollmentId is { } id && !facts.Enrollments.Any(x => x.Id == id && x.EndedAt is null && x.RenewalStoppedAt is null && x.FirstPaidOn != null))
                continue;
            var group = facts.Groups.Single(x => x.Id == item.BillingGroupId);
            // Bounded catch-up also establishes missed fully discounted periods without fake payments.
            for (var count = 0; count < 24; count++)
            {
                if (facts.Lines.Any(x => x.RecurringItemId == item.Id && x.State == LineState.Open))
                    break;
                var start = item.NextOn;
                var covered = facts.Periods(item.MembershipId is null ? Benefit.Service : Benefit.Membership, fee.ServiceDefinitionId).OrderBy(x => x.Start).ToList();
                foreach (var period in covered)
                    if (period.Start <= start && start < period.End)
                        start = period.End;
                if (start > clock.Today.AddDays(Settings.InvoiceLeadDays))
                    break;
                var last = facts.Lines.Where(x => x.RecurringItemId == item.Id).OrderByDescending(x => x.Id).FirstOrDefault();
                if (last is { State: LineState.Expired or LineState.Cancelled } && BillingCalendar.Uncovered(new(last.StartsOn, last.EndsOn), covered).Count > 0)
                    break;
                var active = facts.Membership(clock.Today).Active;
                if (item.MembershipId is null && !active)
                    break;
                var quote = Pricing.Calculate(group.Anchor, group.IntervalMonths, BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, start, true),
                    facts.Prices.Where(x => x.FeeDefinitionId == fee.Id).ToList(), facts.Discounts.Where(x => x.FeeDefinitionId == fee.Id).ToList(), covered);
                var grace = item.MembershipId is not null ? Settings.MembershipRenewalGraceDays : facts.Services.Single(x => x.Id == fee.ServiceDefinitionId).RenewalGraceDays;
                if (quote.Amount > 0 && (!active || start.AddDays(grace) <= clock.Today))
                    break;
                var purpose = BillingCalendar.Containing(group.Anchor, group.IntervalMonths, start).Start == start ? PurchasePurpose.Renewal : PurchasePurpose.Alignment;
                await Issue(facts, operation, item, start, purpose, start.AddDays(grace), ct);
            }
        }
        await Expire(facts, operation, scheduler, ct);
        await AllocateCredit(facts, operation, clock.Today, ct);
        await Finish(facts, scheduler, true, ct);
        // Avoid a journal of empty minute ticks.
        if (operation.ResultId is null && !db.ChangeTracker.Entries().Any(x => (x.State is EntityState.Added or EntityState.Modified) && x.Entity is not BillingOperation) &&
            !facts.Movements.Any(x => x.OperationId == operation.Id) && !facts.Coverage.Any(x => x.OperationId == operation.Id) &&
            !await db.LineAdjustments.AnyAsync(x => x.OperationId == operation.Id, ct) && !await db.ServiceChanges.AnyAsync(x => x.OperationId == operation.Id, ct))
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
                if (line.Benefit == Benefit.Membership && line.ExpiryNotifiedAt is null)
                {
                    await mail.Add(scheduler, Guid.NewGuid(), MailKind.MembershipInactive, facts.Account.UserId, invoiceId: line.InvoiceId, ct: ct);
                    line.ExpiryNotifiedAt = clock.Now;
                }
            }
            else if (line.Benefit == Benefit.Membership && line.Purpose != PurchasePurpose.Initial && line.Purpose != PurchasePurpose.Resumption &&
                clock.Today >= line.ChargeDueOn && line.GraceNotifiedAt is null)
            {
                await mail.Add(scheduler, Guid.NewGuid(), MailKind.MembershipGrace, facts.Account.UserId, invoiceId: line.InvoiceId, ct: ct);
                line.GraceNotifiedAt = clock.Now;
            }
            else if (line.Benefit == Benefit.Service && line.Purpose == PurchasePurpose.Initial && clock.Today >= line.EndsOn)
            {
                var group = facts.Groups.Single(x => x.Id == facts.Invoices.Single(i => i.Id == line.InvoiceId).BillingGroupId);
                var period = BillingCalendar.Containing(group.Anchor, group.IntervalMonths, clock.Today);
                var quote = Pricing.Calculate(group.Anchor, group.IntervalMonths, period, facts.Prices.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList(),
                    facts.Discounts.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList(), facts.Periods(line.Benefit, line.ServiceDefinitionId));
                Requote(facts, operation, line, quote, period);
                // Rollover is not a new funding event; partial funds cannot buy time just by waiting.
                if (quote.Amount == 0 && quote.Segments.Count > 0)
                    Complete(facts, operation, line, null);
            }
        }
        foreach (var enrollment in facts.Enrollments.Where(x => x.EndedAt is null).ToList())
        {
            if (facts.Lines.Any(x => x.ServiceEnrollmentId == enrollment.Id && preserved?.Contains(x.Id) == true))
                continue;
            var periods = facts.Periods(Benefit.Service, enrollment.ServiceDefinitionId).ToList();
            var hasRemainingCoverage = periods.Any(x => x.End > clock.Today);
            var validInitial = facts.Lines.Any(x => x.ServiceEnrollmentId == enrollment.Id && x.Purpose == PurchasePurpose.Initial && x.State == LineState.Open && clock.Today < x.ExpiresOn);
            var grace = facts.ServiceGraceEnd(enrollment, clock.Today);
            if (!hasRemainingCoverage && !validInitial && (grace is null || grace <= clock.Today))
                EndService(facts, operation, enrollment, enrollment.RenewalStoppedAt is null ? "nonpayment" : "stopped", LineState.Expired);
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
            var board = await Authority.IsAdministrator(db, actor, ct);
            if ((!service.SelfSubscriptionAllowed || grantConfiguredDays) && !board)
                throw new UnauthorizedAccessException();
            if (!facts.Membership(clock.Today).Active)
                throw new ValidationException("Active membership is required to subscribe.");
            if (facts.Enrollments.Any(x => x.ServiceDefinitionId == serviceId && x.EndedAt is null))
                throw new ValidationException("Already subscribed.");
            var first = !facts.Enrollments.Any(x => x.ServiceDefinitionId == serviceId);
            var enrollment = new ServiceEnrollment { UserId = facts.User!.Id, ServiceDefinitionId = serviceId, StartedAt = clock.Now };
            facts.Enrollments.Add(enrollment);
            db.ServiceEnrollments.Add(enrollment);
            await db.SaveChangesAsync(ct);
            operation.ResultId = enrollment.Id;
            db.ServiceChanges.Add(new() { ServiceEnrollmentId = enrollment.Id, OperationId = operation.Id, Action = grantConfiguredDays ? "subscribe-admin-free-days" : "subscribe" });
            if ((first || grantConfiguredDays) && service.InitialFreeDays > 0)
                Grant(facts, operation, Benefit.Service, serviceId, new(clock.Today, clock.Today.AddDays(service.InitialFreeDays)),
                    first ? CoverageSource.InitialFree : CoverageSource.AdministratorGrant,
                    first ? $"intro:{accountId}:{serviceId}" : $"admin-intro:{key}");
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
        var start = clock.Today;
        foreach (var period in facts.Periods(Benefit.Membership, null).OrderBy(x => x.Start))
            if (period.Start <= start && start < period.End)
                start = period.End;
        if (start == clock.Today)
            return;
        var previous = facts.Items.Where(x => x.MembershipId == facts.User!.Id).OrderByDescending(x => x.Id).FirstOrDefault();
        if (previous is null || previous.EndedAt is null)
            return;
        var operation = await Operation(facts, key, "restore-membership-billing", new { accountId }, actor, reason, ct);
        if (operation is null)
            return;
        var item = new RecurringItem
        {
            BillingGroupId = previous.BillingGroupId,
            FeeDefinitionId = previous.FeeDefinitionId,
            MembershipId = previous.MembershipId,
            StartsOn = clock.Today,
            NextOn = start
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
public sealed class BillingDueWorkJob(IServiceScopeFactory scopes, TimeProvider time, Nulysa.Data.AppDbContext db, Nulysa.Integrations.BankFeed bank) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        long after = 0;
        while (true)
        {
            var accounts = await db.BillingAccounts.AsNoTracking().Where(x => x.UserId != null && x.Id > after).OrderBy(x => x.Id).Select(x => x.Id).Take(50).ToListAsync(cancellationToken);
            if (accounts.Count == 0)
                break;
            foreach (var id in accounts)
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BillingCommands>().Scan(id, cancellationToken);
            }
            after = accounts[^1];
        }
        await bank.CheckStale(cancellationToken);
        await Observations.Record(db, "billing-due-work", time.GetUtcNow(), cancellationToken);
    }
}
