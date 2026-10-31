using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Data;
using Quartz;

namespace Nulysa.WorkerHarness;

// Synthetic domain command: this is test machinery, never registered by the release application.
public static class Probe
{
    public static async Task Enqueue(AppDbContext db, IScheduler scheduler, string key, int input = 1, string mode = "ok", DateTimeOffset? created = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        using (scheduler.EnlistTransaction(tx.GetDbTransaction()))
        {
            await ScheduleInTransaction(db, scheduler, key, input, mode, created);
            await tx.CommitAsync();
        }
    }

    public static async Task ScheduleInTransaction(AppDbContext db, IScheduler scheduler, string key, int input = 1, string mode = "ok", DateTimeOffset? created = null)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An outer transaction is required.");
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO probe_operations (id, input) VALUES ({key}, {input}) ON CONFLICT DO NOTHING
            """);
        var actual = await db.Database.SqlQuery<int>($"SELECT input AS \"Value\" FROM probe_operations WHERE id = {key}").SingleAsync();
        if (actual != input)
            throw new InvalidOperationException("Conflicting operation input.");
        if (inserted == 0)
            return;
        var job = JobBuilder.Create<ProbeJob>().WithIdentity(key, "work").RequestRecovery()
            .UsingJobData("operation", key).UsingJobData("mode", mode)
            .UsingJobData("enqueued_at", (created ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture)).Build();
        await scheduler.ScheduleJob(job, TriggerBuilder.Create<ProbeJob>().WithIdentity(key, "work").StartNow().Build());
    }

    public static async Task Complete(AppDbContext db, string key, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var completed = await db.Database.SqlQuery<bool>($"SELECT completed AS \"Value\" FROM probe_operations WHERE id = {key} FOR UPDATE").SingleAsync(ct);
        if (!completed)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO probe_effects (id) VALUES ({key})", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_operations SET completed = true WHERE id = {key}", ct);
        }
        await tx.CommitAsync(ct);
    }
}

public sealed class ProbeJob(AppDbContext db) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var key = context.JobDetail.JobDataMap.GetString("operation")!;
        var mode = context.JobDetail.JobDataMap.GetString("mode");
        var attempt = await db.Database.SqlQuery<int>($"UPDATE probe_operations SET attempts = attempts + 1 WHERE id = {key} RETURNING attempts AS \"Value\"").ToListAsync(cancellationToken);
        var first = attempt.Single() == 1;
        if (first && mode == "ignore-cancellation")
        {
            using var observed = cancellationToken.Register(() =>
                File.WriteAllText(context.JobDetail.JobDataMap.GetString("cancellation_marker")!, "cancelled"));
            await WaitForRelease(db, key, CancellationToken.None);
            await Probe.Complete(db, key, CancellationToken.None);
            return;
        }
        if (mode == "crash-always" || (first && mode == "crash-before"))
            Crash();
        if (mode == "fail")
            throw new InvalidOperationException("private-test-provider-payload");
        if (mode == "timeout")
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(150));
            await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        }
        if (first && mode is "block-before" or "overlap")
            await WaitForRelease(db, key, cancellationToken);
        await Probe.Complete(db, key, cancellationToken);
        if (first && mode == "crash-after")
            Crash();
        if (first && mode == "block-after")
            await WaitForRelease(db, key, cancellationToken);
    }

    // SIGKILL simulates process death without SIGABRT, which desktop crash reporters flag.
    private static void Crash()
    {
        using var process = Process.GetCurrentProcess();
        process.Kill();
        Environment.FailFast("Synthetic worker crash fallback.");
    }

    private static async Task WaitForRelease(AppDbContext db, string key, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        while (!await db.Database.SqlQuery<bool>($"SELECT released AS \"Value\" FROM probe_operations WHERE id = {key}").SingleAsync(deadline.Token))
            await Task.Delay(100, deadline.Token);
    }
}
