using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Operations;

public sealed record ScheduleStatus(string Name, string State, DateTimeOffset? NextFire, DateTimeOffset? LastSuccess);
public sealed record DeliveryDiagnostic(Guid Id, MailKind Kind, DeliveryState State, int Attempts, DateTimeOffset CreatedAt, string? ProviderId);
public sealed record BillingDiagnostic(int OpenInvoices, int ReviewReceipts, List<BillingOperation> RecentOperations);
public sealed record SchedulerReport(WorkReport Work, List<ScheduleStatus> Schedules, List<FailureObservation> Failures, List<DeliveryDiagnostic> Deliveries, BillingDiagnostic Billing);

public sealed class SchedulerDiagnostics(IAuthorizationService authorization, AppDbContext db, ISchedulerFactory schedulers, TimeProvider clock)
{
    public async Task<SchedulerReport> Read(ClaimsPrincipal user, CancellationToken ct = default)
    {
        if (!(await authorization.AuthorizeAsync(user, null, "Superuser")).Succeeded) throw new UnauthorizedAccessException();
        var scheduler = await schedulers.GetScheduler(ct);
        var work = await WorkStatus.Read(db, scheduler, clock, ct);
        var schedules = new List<ScheduleStatus>();
        foreach (var name in Schedules.Names)
        {
            var key = new TriggerKey(name, Schedules.Group);
            var trigger = await scheduler.GetTrigger(key, ct);
            var observation = await db.Observations.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct);
            schedules.Add(new(name, trigger is null ? "Missing" : (await scheduler.GetTriggerState(key, ct)).ToString(),
                trigger?.NextFireTimeUtc, observation?.LastSeen));
        }
        var failures = await db.FailureObservations.AsNoTracking().OrderByDescending(x => x.LastFailedAt).Take(50).ToListAsync(ct);
        var deliveries = await db.Deliveries.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(50)
            .Select(x => new DeliveryDiagnostic(x.Id, x.Kind, x.State, x.Attempts, x.CreatedAt, x.ProviderId)).ToListAsync(ct);
        var billing = new BillingDiagnostic(await db.Invoices.CountAsync(x => x.ClosedAt == null, ct),
            await db.Receipts.CountAsync(x => x.State == ReceiptState.Review || x.State == ReceiptState.New, ct),
            await db.BillingOperations.AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(25).ToListAsync(ct));
        return new(work, schedules, failures, deliveries, billing);
    }
}
