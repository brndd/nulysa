using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Operations;
using Quartz;

namespace Nulysa.Membership;

public sealed class MembershipMaintenance(AppDbContext db, AssociationClock clock, IOptions<MembershipOptions> options, ISchedulerFactory schedulers, MembershipCommands membership)
{
    public async Task Scan(CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        using (scheduler.EnlistTransaction(tx.GetDbTransaction()))
        {
            var unverified = clock.Now.AddHours(-options.Value.UnverifiedRetentionHours);
            var rejected = clock.Now.AddDays(-options.Value.RejectionRetentionDays);
            var applications = await db.Applications
                .Where(x => (x.VerifiedAt == null && x.CreatedAt <= unverified) || x.RejectedAt <= rejected)
                .OrderBy(x => x.CreatedAt)
                .Select(x => x.Id)
                .Take(50)
                .ToListAsync(ct);
            var obsoleteJobs = new List<JobKey>();
            foreach (var id in applications)
            {
                // Recheck after the row lock: verification/rejection/approval may have raced the scan.
                var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM \"Applications\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
                var shouldRemove = application is not null
                    && ((application.VerifiedAt == null && application.CreatedAt <= unverified) || application.RejectedAt <= rejected);
                if (!shouldRemove)
                    continue;

                var deliveries = await db.Deliveries.Where(x => x.ApplicationId == id).ToListAsync(ct);
                foreach (var delivery in deliveries)
                {
                    obsoleteJobs.Add(MailQueue.JobKey(delivery.Id, delivery.QueueVersion));
                    if (delivery.State != DeliveryState.Accepted)
                        delivery.State = delivery.Kind == MailKind.Rejection ? DeliveryState.Failed : DeliveryState.Cancelled;
                }
                db.Applications.Remove(application!);
            }
            await db.SaveChangesAsync(ct);

            await db.Deliveries.Where(x => x.State == DeliveryState.Sending && x.StartedAt <= clock.Now.AddMinutes(-2))
                .ExecuteUpdateAsync(x => x.SetProperty(d => d.State, DeliveryState.Review).SetProperty(d => d.ProtectedTestMessage, (string?)null), ct);
            var terminal = db.Deliveries.Where(x => x.State != DeliveryState.Pending && x.State != DeliveryState.Sending
                && (x.AcceptedAt ?? x.StartedAt ?? x.CreatedAt) < clock.Now.AddDays(-30));
            var oldDeliveries = await terminal.OrderBy(x => x.CreatedAt).Select(x => x.Id).Take(100).ToListAsync(ct);
            await terminal.Where(x => oldDeliveries.Contains(x.Id)).ExecuteDeleteAsync(ct);

            // A batch of healthy campaign jobs must not hide a missing job farther down the queue.
            const int repairLimit = 50;
            const int lookupSize = 200;
            var missing = new List<Delivery>();
            var now = clock.Now;
            var pending = db.Deliveries.AsNoTracking()
                .Where(x => x.State == DeliveryState.Pending && (x.RetryNotBefore == null || x.RetryNotBefore <= now))
                .OrderBy(x => x.CampaignId != null).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id);
            for (var offset = 0; missing.Count < repairLimit; offset += lookupSize)
            {
                var batch = await pending.Skip(offset).Take(lookupSize).ToListAsync(ct);
                if (batch.Count == 0)
                    break;
                var scheduled = await scheduler.GetJobDetails(batch.Select(x => MailQueue.JobKey(x.Id, x.QueueVersion)).ToArray(), ct);
                var scheduledKeys = scheduled.Select(x => x.Key).ToHashSet();
                missing.AddRange(batch.Where(x => !scheduledKeys.Contains(MailQueue.JobKey(x.Id, x.QueueVersion)))
                    .Take(repairLimit - missing.Count));
                if (batch.Count < lookupSize)
                    break;
            }

            // Keep application/delivery row mutations ahead of scheduler writes.
            if (obsoleteJobs.Count > 0)
                await scheduler.DeleteJobs(obsoleteJobs, ct);
            await MailQueue.ScheduleBatch(scheduler, missing, ct);
            await tx.CommitAsync(ct);
        }

        // Do not acquire member locks while holding application, delivery or scheduler locks.
        var expiredEmails = await db.Users
            .Where(x => x.PendingEmailRequestedAt <= clock.Now.AddDays(-1))
            .OrderBy(x => x.PendingEmailRequestedAt)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(50)
            .ToListAsync(ct);
        foreach (var id in expiredEmails)
        {
            await using var cleanup = await db.Database.BeginTransactionAsync(ct);
            var user = await membership.LockUser(id, ct);
            if (user.PendingEmailRequestedAt <= clock.Now.AddDays(-1))
            {
                user.PendingEmail = null;
                user.PendingEmailVersion = null;
                user.PendingEmailRequestedAt = null;
                await db.SaveChangesAsync(ct);
            }
            await cleanup.CommitAsync(ct);
        }

        await Observations.Record(db, "membership-due-work", clock.Now, ct);
    }
}

[DisallowConcurrentExecution]
public sealed class MembershipDueWorkJob(MembershipMaintenance maintenance) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default) => await maintenance.Scan(cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class FinalDeletionJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<FinalDeletionJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        // Each erasure has its own transaction/scope; never hold association-wide member locks.
        List<long> ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ids = await db.Members.Where(x => x.DeletionEligibleAt <= clock.GetUtcNow()).OrderBy(x => x.UserId).Select(x => x.UserId).ToListAsync(cancellationToken);
        }
        var failures = new List<Exception>();
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MembershipCommands>().Erase(id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Final deletion failed for member {MemberId}.", id);
                failures.Add(error);
            }
        }
        await using var observation = scopes.CreateAsyncScope();
        await Observations.Record(observation.ServiceProvider.GetRequiredService<AppDbContext>(), "final-deletion", clock.GetUtcNow(), cancellationToken);
        if (failures.Count > 0)
            throw new AggregateException("Final deletion failed for one or more members; the rest were processed.", failures);
    }
}
