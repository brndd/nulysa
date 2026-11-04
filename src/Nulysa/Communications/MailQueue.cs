using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Communications;

public sealed class MailQueue(AppDbContext db, TimeProvider clock)
{
    public const int AttemptsPerRound = 3;
    public static JobKey JobKey(Guid id, int version = 0) => new(
        "mail-" + id.ToString("N") + (version == 0 ? "" : "-" + version.ToString(CultureInfo.InvariantCulture)), "work");

    // Caller owns the outer EF transaction AND Quartz enlistment scope.
    public async Task Add(IScheduler scheduler, Guid id, MailKind kind, long? userId = null, Guid? applicationId = null,
        Guid version = default, CancellationToken ct = default, long? invoiceId = null, int? invoiceRevision = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Mail scheduling requires a command transaction.");

        var existing = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (existing is not null)
        {
            if (existing.Kind != kind || existing.UserId != userId || existing.ApplicationId != applicationId || existing.Version != version || existing.InvoiceId != invoiceId || existing.InvoiceRevision != invoiceRevision)
                throw new ValidationException("This delivery key was already used for another action.");
            return;
        }

        var delivery = new Delivery { Id = id, Kind = kind, UserId = userId, ApplicationId = applicationId, Version = version,
            CreatedAt = clock.GetUtcNow(), InvoiceId = invoiceId, InvoiceRevision = invoiceRevision };
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(ct);
        await Schedule(scheduler, delivery, ct);
    }

    internal static async Task Schedule(IScheduler scheduler, Delivery delivery, CancellationToken ct)
    {
        var job = CreateJob(delivery);
        await scheduler.ScheduleJob(job, Trigger(job.Key, Priority(delivery.Kind)).StartNow().Build(), cancellationToken: ct);
    }

    internal static async Task ScheduleBatch(IScheduler scheduler, IReadOnlyCollection<Delivery> deliveries, CancellationToken ct)
    {
        if (deliveries.Count == 0)
            return;
        var start = DateTimeOffset.UtcNow;
        await scheduler.ScheduleJobs(deliveries.ToDictionary(CreateJob, delivery =>
            (IReadOnlyCollection<ITrigger>)[Trigger(JobKey(delivery.Id, delivery.QueueVersion), Priority(delivery.Kind)).StartAt(start).Build()]),
            cancellationToken: ct);
    }

    internal static int Priority(MailKind kind) => kind switch
    {
        MailKind.Newsletter or MailKind.OfficialNotice => 1,
        MailKind.CampaignTest or MailKind.Invoice => 5,
        _ => 10
    };

    internal static TriggerBuilder<MailDeliveryJob> Trigger(JobKey key, int priority) => TriggerBuilder.Create<MailDeliveryJob>()
        .ForJob(key).WithPriority(priority).WithRetryPolicy(RetryPolicy.Fixed(2, TimeSpan.FromSeconds(10)));

    private static IJobDetail CreateJob(Delivery delivery)
    {
        var job = JobBuilder.Create<MailDeliveryJob>().WithIdentity(JobKey(delivery.Id, delivery.QueueVersion)).RequestRecovery()
            .UsingJobData("delivery_id", delivery.Id.ToString("D"))
            .UsingJobData("enqueued_at", delivery.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        if (delivery.QueueVersion != 0)
            job.UsingJobData("queue_version", delivery.QueueVersion.ToString(CultureInfo.InvariantCulture));
        return job.Build();
    }

    public async Task CancelForUser(IScheduler scheduler, long id, CancellationToken ct)
    {
        var pending = await db.Deliveries.FromSqlInterpolated($"""
            SELECT * FROM "Deliveries" WHERE "UserId" = {id} AND "State" = 'Pending' ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
        foreach (var delivery in pending)
            delivery.State = DeliveryState.Cancelled;
        await db.SaveChangesAsync(ct);
        foreach (var delivery in pending)
            await scheduler.DeleteJob(JobKey(delivery.Id, delivery.QueueVersion), ct);
    }
}

[DisallowConcurrentExecution]
public sealed class MailDeliveryJob(MailDispatcher dispatcher) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var version = context.MergedJobDataMap.GetString("queue_version") is { } value
            ? int.Parse(value, CultureInfo.InvariantCulture) : 0;
        await dispatcher.Dispatch(Guid.Parse(context.MergedJobDataMap.GetString("delivery_id")!), version, async (transaction, delay, ct) =>
        {
            using var enlistment = context.Scheduler.EnlistTransaction(transaction);
            var trigger = MailQueue.Trigger(context.JobDetail.Key, context.Trigger.Priority).StartAt(DateTimeOffset.UtcNow + delay).Build();
            await context.Scheduler.ScheduleJob(trigger, cancellationToken: ct);
        }, cancellationToken);
    }
}
