using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Operations;

public sealed record ScheduleStatus(string Name, string State, DateTimeOffset? NextFire, DateTimeOffset? LastSuccess);
public sealed record SchedulerReport(WorkReport Work, List<ScheduleStatus> Schedules, List<FailureObservation> Failures);

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
        return new(work, schedules, failures);
    }
}
