using Microsoft.EntityFrameworkCore;
using Nulysa.Data;
using OpenIddict.Abstractions;
using Quartz;

namespace Nulysa.Operations;

[DisallowConcurrentExecution]
public sealed class HeartbeatJob(AppDbContext db, TimeProvider clock) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
        => await Observations.Record(db, "heartbeat", clock.GetUtcNow(), cancellationToken);
}

[DisallowConcurrentExecution]
public sealed class AuthenticationCleanupJob(
    AppDbContext db, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, TimeProvider clock) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        // OpenIddict bounds pruning and preserves authorizations with usable token chains.
        var threshold = clock.GetUtcNow() - TimeSpan.FromDays(1);
        await tokens.PruneAsync(threshold, cancellationToken);
        await authorizations.PruneAsync(threshold, cancellationToken);
        await Observations.Record(db, "authentication-cleanup", clock.GetUtcNow(), cancellationToken);
    }
}

public static class Observations
{
    public static Task Record(AppDbContext db, string name, DateTimeOffset now, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Observations" ("Name", "LastSeen") VALUES ({name}, {now})
            ON CONFLICT ("Name") DO UPDATE SET "LastSeen" = GREATEST("Observations"."LastSeen", EXCLUDED."LastSeen")
            """, ct);
}

public sealed class RedactJobFailures : IJobExecutionMiddleware
{
    public async ValueTask Invoke(IJobExecutionContext context, JobExecutionDelegate next, CancellationToken cancellationToken = default)
    {
        try
        { await next(context, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // History and ordinary logs must not archive SQL parameters or provider payloads.
            throw new JobExecutionException($"Job failed ({error.GetType().Name}). Investigate using the operation ID; payload redacted.");
        }
    }
}
