using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Communications;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Operations;

public sealed record WorkReport(bool Healthy, string[] Issues, int PendingWork, int FinalFailures, double? OldestWorkAgeSeconds);

public static class WorkStatus
{
    public static async Task<WorkReport> Read(AppDbContext db, IScheduler scheduler, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var issues = new HashSet<string>();
        foreach (var name in Schedules.Names)
        {
            var observation = await db.Observations.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct);
            var maxAge = name switch { "heartbeat" or "membership-due-work" or "billing-due-work" => TimeSpan.FromMinutes(3), "final-deletion" => TimeSpan.FromDays(15), _ => TimeSpan.FromHours(26) };
            if (observation is null && name != "final-deletion")
                issues.Add($"missing_{name}");
            else if (observation is not null && now - observation.LastSeen > maxAge)
                issues.Add($"stale_{name}");
            var key = new TriggerKey(name, Schedules.Group);
            var trigger = await scheduler.GetTrigger(key, ct);
            if (trigger is null)
                issues.Add("missing_schedule");
            else
            {
                var state = await scheduler.GetTriggerState(key, ct);
                if (state is TriggerState.Paused or TriggerState.Error or TriggerState.Complete || trigger.NextFireTimeUtc is null)
                    issues.Add("stopped_schedule");
                else if (trigger.NextFireTimeUtc < now - TimeSpan.FromMinutes(2))
                    issues.Add("overdue_schedule");
            }
        }

        // Original enqueue time lives on the immutable job detail, not the replaced recovery trigger.
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("work"), ct);
        var jobs = await scheduler.GetJobDetails(keys, ct);
        DateTimeOffset? oldest = null;
        foreach (var job in jobs)
        {
            if (!DateTimeOffset.TryParse(job.JobDataMap.GetString("enqueued_at"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var created))
                issues.Add("unreadable_work");
            else if (oldest is null || created < oldest)
                oldest = created;
        }
        var age = oldest is { } at ? (double?)(now - at).TotalSeconds : null;
        if (age > 300)
            issues.Add("aged_work");
        var failures = await db.FailureObservations.CountAsync(ct);
        if (failures > 0)
            issues.Add("failed_work");
        if (await db.Deliveries.AnyAsync(x => x.State == DeliveryState.Review || x.State == DeliveryState.Failed, ct))
            issues.Add("mail_requires_review");
        if (await db.Deliveries.AnyAsync(x => x.State == DeliveryState.Pending && x.CreatedAt < now.AddMinutes(-5), ct))
            issues.Add("aged_mail");
        return new(issues.Count == 0, [.. issues.Order()], keys.Count, failures, age);
    }
}
