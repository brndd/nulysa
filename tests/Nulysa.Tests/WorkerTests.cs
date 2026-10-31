using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Operations;
using Nulysa.WorkerHarness;
using Quartz;

namespace Nulysa.Tests;

[Trait("Group", "Worker")]
public sealed class WorkerTests : DatabaseTest
{
    [Fact]
    public async Task Shared_transaction_is_invisible_until_commit_and_rollback_removes_both_writes()
    {
        await using var db = Context();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe.ScheduleInTransaction(db, Scheduler, "no-tx"));
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            using (Scheduler.EnlistTransaction(tx.GetDbTransaction()))
            {
                await Probe.ScheduleInTransaction(db, Scheduler, "rolled-back");
                await using var other = Context();

                Assert.Equal(0, await other.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM probe_operations").SingleAsync());
                Assert.Equal(0, await other.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_job_details WHERE job_group = 'work'").SingleAsync());
                await tx.RollbackAsync();
            }
        }

        Assert.Null(await Scheduler.GetJobDetail(new JobKey("rolled-back", "work")));
        await Probe.Enqueue(db, Scheduler, "committed");

        Assert.NotNull(await Scheduler.GetJobDetail(new JobKey("committed", "work")));
        StartWorker();
        await Until(async () => await Effects("committed") == 1);
    }

    [Fact]
    public async Task Scheduling_failure_rolls_back_the_domain_write()
    {
        var job = JobBuilder.Create<ProbeJob>().WithIdentity("occupied", "work").Build();
        await Scheduler.ScheduleJob(job, TriggerBuilder.Create<ProbeJob>().WithIdentity("occupied", "work").StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build());
        await using var db = Context();

        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Probe.Enqueue(db, Scheduler, "occupied"));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM probe_operations").SingleAsync());
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Queued_work_survives_worker_restarts_and_competing_instances()
    {
        await using var db = Context();
        var first = StartWorker();
        await Probe.Enqueue(db, Scheduler, "first");
        await Until(async () => await Effects("first") == 1);
        await first.Stop();
        await Probe.Enqueue(db, Scheduler, "queued");
        StartWorker();
        StartWorker();
        await Until(async () => await Effects("queued") == 1);

        Assert.Equal(1, await Attempts("queued"));
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("crash-before", false)]
    [InlineData("crash-after", false)]
    [InlineData("crash-before", true)]
    [InlineData("crash-after", true)]
    public async Task Process_death_recovers_on_restart_or_surviving_peer_without_duplicate_effects(string mode, bool peer)
    {
        await using var db = Context();
        if (peer)
        {
            await Probe.Enqueue(db, Scheduler, "crash", mode: mode == "crash-after" ? "block-after" : "block-before");
            var first = StartWorker();
            await Until(async () => await Attempts("crash") == 1);
            if (mode == "crash-after")
                await Until(async () => await Effects("crash") == 1);
            StartWorker();
            await Until(async () => await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_scheduler_state").SingleAsync() == 2);
            await first.Signal("KILL");
        }
        else
        {
            await Probe.Enqueue(db, Scheduler, "crash", mode: mode);
            var first = StartWorker();
            await Until(() => Task.FromResult(first.HasExited));
            StartWorker();
        }
        await Until(async () => await Attempts("crash") == 2 && await Effects("crash") == 1);

        Assert.Equal(2, await Attempts("crash"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Graceful_shutdown_cancels_and_hands_back_recoverable_firing()
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "shutdown", mode: "block-before");
        var first = StartWorker();
        await Until(async () => await Attempts("shutdown") == 1);
        await first.Stop();

        Assert.Equal(0, await Effects("shutdown"));
        StartWorker();
        await Until(async () => await Effects("shutdown") == 1);

        Assert.Equal(2, await Attempts("shutdown"));
    }

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData("fail")]
    [InlineData("timeout")]
    public async Task Ordinary_failures_and_cooperative_timeouts_have_bounded_retries_and_redacted_shared_history(string mode)
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "failure", mode: mode);
        StartWorker();
        await Until(async () => await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_execution_history WHERE job_name = 'failure' AND retry_scheduled = false").SingleAsync() == 1);

        Assert.Equal(3, await Attempts("failure")); // Initial attempt plus two retries.
        Assert.Equal(0, await Effects("failure"));
        var messages = await db.Database.SqlQuery<string>($"SELECT error_message AS \"Value\" FROM qrtz_execution_history WHERE job_name = 'failure'").ToListAsync();

        Assert.Equal(3, messages.Count);
        Assert.All(messages, x => Assert.DoesNotContain("private-test", x));
        // History and the operational observation deliberately commit independently.
        await Until(async () => await db.FailureObservations.AnyAsync());
        var status = await WorkStatus.Read(db, Scheduler, TimeProvider.System);

        Assert.Contains("failed_work", status.Issues);
    }

    [Fact]
    public async Task Concurrent_retries_compare_inputs_and_commit_effect_at_most_once()
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "duplicate", 5);
        await Probe.Enqueue(db, Scheduler, "duplicate", 5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe.Enqueue(db, Scheduler, "duplicate", 6));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var other = Context();
            await Probe.Complete(other, "duplicate", CancellationToken.None);
        }));

        Assert.Equal(1, await Effects("duplicate"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task False_failure_detection_and_late_completion_cannot_duplicate_or_lose_work()
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "stale", mode: "overlap");
        var old = StartWorker();
        await Until(async () => await Attempts("stale") == 1);
        await old.Signal("STOP");
        try
        {
            StartWorker();
            await Until(async () => await Effects("stale") == 1);
            await Release("stale");
        }
        finally { await old.Signal("CONT"); }
        await Probe.Enqueue(db, Scheduler, "later");
        await Until(async () => await Effects("later") == 1);
        await Until(async () => await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_fired_triggers WHERE job_name = 'stale'").SingleAsync() == 0);

        Assert.Equal(1, await Effects("stale"));
        Assert.Equal(2, await Attempts("stale"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Cancellation_is_cooperative_and_cannot_kill_a_handler_that_ignores_it()
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "uncancellable", mode: "ignore-cancellation");
        var key = new JobKey("uncancellable", "work");
        var original = (await Scheduler.GetJobDetail(key))!;
        var marker = Path.Combine(DirectoryPath, "cancelled");
        var job = original.GetJobBuilder().UsingJobData("cancellation_marker", marker).Build();
        await Scheduler.AddJob(job, AddJobOptions.ReplacingAndStoringNonDurable);
        StartWorker();
        await Until(async () => await Attempts("uncancellable") == 1);

        Assert.True(await Scheduler.Interrupt(key));
        await Until(() => Task.FromResult(File.Exists(marker)));

        Assert.Equal(0, await Effects("uncancellable"));
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_fired_triggers WHERE job_name = 'uncancellable'").SingleAsync());
        await Release("uncancellable");
        await Until(async () => await Effects("uncancellable") == 1);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Repeated_process_death_is_visible_from_original_work_age_without_failure_history()
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "loop", mode: "crash-always", created: DateTimeOffset.UtcNow.AddMinutes(-10));
        var one = StartWorker();
        await Until(() => Task.FromResult(one.HasExited));
        var two = StartWorker();
        await Until(() => Task.FromResult(two.HasExited));

        Assert.Equal(2, await Attempts("loop"));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_execution_history").SingleAsync());
        var report = await WorkStatus.Read(db, Scheduler, TimeProvider.System);

        Assert.Contains("aged_work", report.Issues);
        Assert.True(report.OldestWorkAgeSeconds >= 600);
    }
}
