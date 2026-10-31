using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Communications;

public sealed class MailQueue(AppDbContext db, TimeProvider clock)
{
    public static JobKey JobKey(Guid id) => new("mail-" + id.ToString("N"), "work");

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
        db.Deliveries.Add(new() { Id = id, Kind = kind, UserId = userId, ApplicationId = applicationId, Version = version, CreatedAt = clock.GetUtcNow(), InvoiceId = invoiceId, InvoiceRevision = invoiceRevision });
        await db.SaveChangesAsync(ct);
        var job = JobBuilder.Create<MailDeliveryJob>().WithIdentity(JobKey(id)).RequestRecovery()
            .UsingJobData("delivery_id", id.ToString("D"))
            .UsingJobData("enqueued_at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)).Build();
        await scheduler.ScheduleJob(job, TriggerBuilder.Create<MailDeliveryJob>().ForJob(job.Key).StartNow()
            .WithRetryPolicy(RetryPolicy.Fixed(2, TimeSpan.FromSeconds(10))).Build(), cancellationToken: ct);
    }

    public async Task CancelForUser(IScheduler scheduler, long id, CancellationToken ct)
    {
        var pending = await db.Deliveries.Where(x => x.UserId == id && x.State == DeliveryState.Pending).ToListAsync(ct);
        foreach (var delivery in pending)
        {
            delivery.State = DeliveryState.Cancelled;
            await scheduler.DeleteJob(JobKey(delivery.Id), ct);
        }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class MailDeliveryJob(MailDispatcher dispatcher) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => await dispatcher.Dispatch(Guid.Parse(context.MergedJobDataMap.GetString("delivery_id")!), cancellationToken);
}
