using Microsoft.EntityFrameworkCore;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Operations;

public sealed class FailureObservation
{
    public string Job { get; set; } = "";
    public DateTimeOffset LastFailedAt { get; set; }
}

public sealed class FailureRecorder(IServiceScopeFactory scopes) : IJobListener
{
    public async ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = context.JobDetail.Key.ToString();
        if (context.Outcome == ExecutionOutcome.Failed && !context.RetryScheduled)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "FailureObservations" ("Job", "LastFailedAt") VALUES ({job}, {context.FireTimeUtc})
                ON CONFLICT ("Job") DO UPDATE SET "LastFailedAt" = GREATEST("FailureObservations"."LastFailedAt", EXCLUDED."LastFailedAt")
                """, cancellationToken);
        // A late, stale success must not erase a newer failure observation.
        else if (context.Outcome == ExecutionOutcome.Succeeded)
            await db.FailureObservations.Where(x => x.Job == job && x.LastFailedAt <= context.FireTimeUtc).ExecuteDeleteAsync(cancellationToken);
    }
}
