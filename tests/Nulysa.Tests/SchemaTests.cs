using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;
using Nulysa.Operations;
using Nulysa.WorkerHarness;
using Quartz;

namespace Nulysa.Tests;

[Trait("Group", "Worker")]
public sealed class SchemaTests : DatabaseTest
{
    [Fact]
    public async Task Quartz_45_shape_upgrade_preserves_existing_work()
    {
        await using var db = Context();
        await Probe.Enqueue(db, Scheduler, "upgrade");
        var trigger = await Scheduler.GetTrigger(new TriggerKey("upgrade", "work"));
        // Reconstruct just the 4.5 -> 4.6 schema delta in our disposable database.
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE qrtz_fired_triggers DROP COLUMN interrupt_requested_time;
            ALTER TABLE qrtz_scheduler_state DROP COLUMN quartz_version;
            ALTER TABLE qrtz_triggers DROP COLUMN misfire_threshold;
            DROP INDEX idx_qrtz_eh_fire_inst;
            DROP INDEX idx_qrtz_eh_trig_time;
            """);
        await QuartzSchema.Upgrade(db);
        await QuartzSchema.Upgrade(db);

        Assert.Equal(trigger!.NextFireTimeUtc, (await Scheduler.GetTrigger(trigger.Key))!.NextFireTimeUtc);
        StartWorker();
        await Until(async () => await Effects("upgrade") == 1);
    }

    [Fact]
    public async Task Database_enforces_email_and_subject_uniqueness_and_does_not_reuse_deleted_numeric_IDs()
    {
        await using var db = Context();
        db.Users.Add(new() { UserName = "one", NormalizedUserName = "ONE", Email = "one@example.test", NormalizedEmail = "ONE@EXAMPLE.TEST", OidcSubject = "subject-one", Name = "One" });
        await db.SaveChangesAsync();
        var oldId = (await db.Users.SingleAsync()).Id;
        await using (var other = Context())
        {
            other.Users.Add(new() { UserName = "two", NormalizedUserName = "TWO", NormalizedEmail = "ONE@EXAMPLE.TEST", OidcSubject = "subject-two" });

            await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        }
        await using (var other = Context())
        {
            other.Users.Add(new() { UserName = "three", NormalizedUserName = "THREE", NormalizedEmail = "TWO@EXAMPLE.TEST", OidcSubject = "subject-one" });

            await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        }
        await db.Users.ExecuteDeleteAsync();
        db.ChangeTracker.Clear();
        db.Users.Add(new() { UserName = "four", NormalizedUserName = "FOUR", OidcSubject = "subject-new" });
        await db.SaveChangesAsync();

        Assert.True((await db.Users.SingleAsync()).Id > oldId);
    }

    [Fact]
    public async Task Diagnostics_detect_missing_overdue_unreadable_and_failed_work_even_after_history_is_trimmed()
    {
        await using var db = Context();

        Assert.Contains("missing_heartbeat", (await WorkStatus.Read(db, Scheduler, TimeProvider.System)).Issues);
        await Schedules.Install(Scheduler, TimeProvider.System, TimeZoneInfo.Utc);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE qrtz_triggers SET next_fire_time = {DateTimeOffset.UtcNow.AddHours(-1).UtcTicks}");
        db.FailureObservations.Add(new() { Job = "synthetic", LastFailedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await Scheduler.ScheduleJob(JobBuilder.Create<ProbeJob>().WithIdentity("unreadable", "work").Build(),
            TriggerBuilder.Create<ProbeJob>().StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build());
        await Scheduler.ScheduleJob(JobBuilder.Create<ProbeJob>().WithIdentity("aged", "work")
                .UsingJobData("enqueued_at", DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O", CultureInfo.InvariantCulture)).Build(),
            TriggerBuilder.Create<ProbeJob>().StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build());
        await db.Database.ExecuteSqlRawAsync("DELETE FROM qrtz_execution_history; DELETE FROM qrtz_job_status;");
        var report = await WorkStatus.Read(db, Scheduler, TimeProvider.System);

        Assert.Contains("failed_work", report.Issues);
        Assert.Contains("unreadable_work", report.Issues);
        Assert.Contains("aged_work", report.Issues);
        Assert.True(report.OldestWorkAgeSeconds >= 600);
        Assert.Contains("overdue_schedule", report.Issues);
        Assert.Equal(1, report.FinalFailures);
    }
}
