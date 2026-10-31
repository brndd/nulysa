using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Nulysa.Membership;
using Nulysa.Operations;
using Quartz;

namespace Nulysa.Tests;

public sealed class CalendarTests
{
    [Theory]
    [InlineData(2026, 3, 15, 1, 30, 2026, 3, 29, 1, 0)]
    [InlineData(2026, 10, 11, 0, 30, 2026, 10, 25, 1, 30)]
    public void Deletion_waiting_preserves_wall_time_and_never_chooses_the_early_DST_resolution(
        int year, int month, int day, int hour, int minute, int endYear, int endMonth, int endDay, int endHour, int endMinute)
    {
        var clock = new ManualClock { Now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero) };
        var association = new AssociationClock(clock, new ConfigurationBuilder().Build());
        Assert.Equal(new DateTimeOffset(endYear, endMonth, endDay, endHour, endMinute, 0, TimeSpan.Zero), association.AfterCalendarDays(14));
    }

    [Theory]
    [InlineData(2026, 3, 28)]
    [InlineData(2026, 10, 24)]
    public void Helsinki_midnight_and_anchored_fortnights_survive_both_DST_changes(int year, int month, int day)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        var anchor = new DateOnly(year, month, day);
        var trigger = Schedules.Fortnightly<HeartbeatJob>(anchor, zone);
        var time = trigger.StartTimeUtc.AddSeconds(-1);
        for (var i = 0; i < 5; i++)
        {
            time = trigger.GetFireTimeAfter(time)!.Value;
            var local = TimeZoneInfo.ConvertTime(time, zone);
            Assert.Equal(anchor.AddDays(14 * i), DateOnly.FromDateTime(local.DateTime));
            Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
        }
        var cron = new CronExpression("0 0 0 * * ?", zone);
        time = trigger.StartTimeUtc;
        for (var i = 1; i <= 4; i++)
        {
            time = cron.GetNextValidTimeAfter(time)!.Value;
            var local = TimeZoneInfo.ConvertTime(time, zone);
            Assert.Equal(anchor.AddDays(i), DateOnly.FromDateTime(local.DateTime));
            Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
        }
    }
}

[Trait("Group", "Worker")]
public sealed class ScheduleTests : DatabaseTest
{
    [Fact]
    public async Task Persisted_calendar_phase_is_not_reset_by_installation_or_worker_restart()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        var anchor = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime).AddDays(1);
        var key = new JobKey("fortnight-proof", "calendar-tests");
        var trigger = Schedules.Fortnightly<HeartbeatJob>(anchor, zone).GetTriggerBuilder()
            .WithIdentity("fortnight-proof", "calendar-tests").ForJob(key).Build();
        await Scheduler.ScheduleJob(JobBuilder.Create<HeartbeatJob>().WithIdentity(key).Build(), trigger);
        var stored = (ICalendarIntervalTrigger)(await Scheduler.GetTrigger(trigger.Key))!;
        await Schedules.Install(Scheduler, TimeProvider.System, zone);
        var first = StartWorker();
        await using var db = Context();
        await Until(async () => await db.Observations.AnyAsync(x => x.Name == "heartbeat"));
        await first.Stop();
        StartWorker();
        await Schedules.Install(Scheduler, TimeProvider.System, zone);
        var after = (ICalendarIntervalTrigger)(await Scheduler.GetTrigger(trigger.Key))!;
        Assert.Equal(stored.StartTimeUtc, after.StartTimeUtc);
        Assert.Equal(stored.NextFireTimeUtc, after.NextFireTimeUtc);
        Assert.Equal(14, after.RepeatInterval);
        Assert.Equal(IntervalUnit.Day, after.RepeatIntervalUnit);
        Assert.Equal(zone.Id, after.TimeZone.Id);
    }

    [Fact]
    public async Task Release_install_is_idempotent_and_schema_reapply_preserves_jobs_and_deadlines()
    {
        await Schedules.Install(Scheduler, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki"));
        var key = new TriggerKey("heartbeat", Schedules.Group);
        var before = await Scheduler.GetTrigger(key);
        await using (var db = Context()) await db.Database.MigrateAsync();
        await Schedules.Install(Scheduler, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki"));
        var after = await Scheduler.GetTrigger(key);
        Assert.Equal(before!.NextFireTimeUtc, after!.NextFireTimeUtc);
        Assert.Equal(before.StartTimeUtc, after.StartTimeUtc);
        var bad = TriggerBuilder.Create<HeartbeatJob>().WithIdentity("heartbeat", Schedules.Group)
            .ForJob("heartbeat", Schedules.Group).WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromHours(1)).RepeatForever()).Build();
        await Scheduler.RescheduleJob(key, bad);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Schedules.Install(Scheduler, TimeProvider.System, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task Actual_timer_persists_heartbeat_and_cleanup_and_reports_stalled_schedules()
    {
        await Schedules.Install(Scheduler, TimeProvider.System, TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki"));
        // A missed daily wakeup is coalesced by Quartz, not replayed once per missed day.
        await using var db = Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE qrtz_triggers SET next_fire_time = {DateTimeOffset.UtcNow.AddDays(-3).UtcTicks} WHERE trigger_name IN ('authentication-cleanup', 'bank-sync')");
        var worker = StartWorker();
        await Until(async () => await db.Observations.CountAsync() == Schedules.Names.Length - 1, seconds: 45);
        await Until(async () => await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_execution_history WHERE job_name = 'authentication-cleanup'").SingleAsync() == 1);
        Assert.True((await WorkStatus.Read(db, Scheduler, TimeProvider.System)).Healthy);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM qrtz_execution_history WHERE job_name = 'authentication-cleanup'").SingleAsync());
        await worker.Stop();
        await Scheduler.PauseTrigger(new TriggerKey("heartbeat", Schedules.Group));
        var stopped = await WorkStatus.Read(db, Scheduler, TimeProvider.System);
        Assert.Contains("stopped_schedule", stopped.Issues);
        await db.Observations.ExecuteUpdateAsync(x => x.SetProperty(o => o.LastSeen, DateTimeOffset.UtcNow.AddDays(-2)));
        Assert.Contains("stale_heartbeat", (await WorkStatus.Read(db, Scheduler, TimeProvider.System)).Issues);
    }
}
