using Nulysa.Membership;
using Quartz;

namespace Nulysa.Operations;

public static class Schedules
{
    public const string Group = "maintenance";
    public static readonly string[] Names = ["heartbeat", "authentication-cleanup", "membership-due-work", "final-deletion"];

    public static async Task Install(IScheduler scheduler, TimeProvider clock, TimeZoneInfo zone, CancellationToken ct = default)
    {
        var heartbeat = TriggerBuilder.Create<HeartbeatJob>().WithIdentity("heartbeat", Group)
            .ForJob("heartbeat", Group).StartAt(clock.GetUtcNow())
            .WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromSeconds(30)).RepeatForever()
                .WithMisfireInstruction(SimpleTriggerMisfireInstruction.NextWithRemainingCount))
            .WithRetryPolicy(RetryPolicy.Fixed(2, TimeSpan.FromSeconds(2))).Build();
        var cleanup = TriggerBuilder.Create<AuthenticationCleanupJob>().WithIdentity("authentication-cleanup", Group)
            .ForJob("authentication-cleanup", Group).StartAt(clock.GetUtcNow())
            .WithCronSchedule("0 0 0 * * ?", x => x.InTimeZone(zone).WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .WithRetryPolicy(RetryPolicy.Fixed(2, TimeSpan.FromSeconds(10))).Build();
        await InstallOne(scheduler, JobBuilder.Create<HeartbeatJob>().WithIdentity("heartbeat", Group).RequestRecovery().Build(), heartbeat, ct);
        await InstallOne(scheduler, JobBuilder.Create<AuthenticationCleanupJob>().WithIdentity("authentication-cleanup", Group).RequestRecovery().Build(), cleanup, ct);
        var due = TriggerBuilder.Create<MembershipDueWorkJob>().WithIdentity("membership-due-work", Group).ForJob("membership-due-work", Group)
            .StartAt(clock.GetUtcNow()).WithSimpleSchedule(x => x.WithInterval(TimeSpan.FromMinutes(1)).RepeatForever()
                .WithMisfireInstruction(SimpleTriggerMisfireInstruction.FireNow)).Build();
        await InstallOne(scheduler, JobBuilder.Create<MembershipDueWorkJob>().WithIdentity("membership-due-work", Group).RequestRecovery().Build(), due, ct);
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime).AddDays(1);
        var deletion = Fortnightly<FinalDeletionJob>(tomorrow, zone).GetTriggerBuilder()
            .WithIdentity("final-deletion", Group).ForJob("final-deletion", Group).Build();
        await InstallOne(scheduler, JobBuilder.Create<FinalDeletionJob>().WithIdentity("final-deletion", Group).RequestRecovery().Build(), deletion, ct);
    }

    private static async Task InstallOne(IScheduler scheduler, IJobDetail job, ITrigger trigger, CancellationToken ct)
    {
        var existingJob = await scheduler.GetJobDetail(job.Key, ct);
        var existing = await scheduler.GetTrigger(trigger.Key, ct);
        if (existingJob is not null || existing is not null)
        {
            var sameSchedule = (existing, trigger) switch
            {
                (ISimpleTrigger a, ISimpleTrigger b) => a.RepeatInterval == b.RepeatInterval && a.RepeatCount == b.RepeatCount,
                (ICronTrigger a, ICronTrigger b) => a.CronExpressionString == b.CronExpressionString && a.TimeZone.Id == b.TimeZone.Id,
                (ICalendarIntervalTrigger a, ICalendarIntervalTrigger b) => a.RepeatInterval == b.RepeatInterval && a.RepeatIntervalUnit == b.RepeatIntervalUnit &&
                    a.TimeZone.Id == b.TimeZone.Id && a.PreserveHourOfDayAcrossDaylightSavings == b.PreserveHourOfDayAcrossDaylightSavings &&
                    a.SkipDayIfHourDoesNotExist == b.SkipDayIfHourDoesNotExist,
                _ => false
            };
            if (existingJob?.JobType != job.JobType || existing?.JobKey != job.Key || !sameSchedule ||
                existing.MisfireInstructionCode != trigger.MisfireInstructionCode || existing.RetryPolicy != trigger.RetryPolicy ||
                existing.CalendarName != trigger.CalendarName || !existingJob.RequestsRecovery)
                throw new InvalidOperationException($"Conflicting schedule: {trigger.Key}.");
            return;
        }
        await scheduler.ScheduleJob(job, trigger, cancellationToken: ct);
    }

    public static ITrigger Fortnightly<TJob>(DateOnly anchor, TimeZoneInfo zone) where TJob : IJob
    {
        var local = anchor.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var start = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        return TriggerBuilder.Create<TJob>().StartAt(start)
            .WithCalendarIntervalSchedule(x => x.WithInterval(14, IntervalUnit.Day).InTimeZone(zone)
                .PreserveHourOfDayAcrossDaylightSavings().SkipDayIfHourDoesNotExist()
                .WithMisfireInstruction(CalendarIntervalTriggerMisfireInstruction.FireAndProceed)).Build();
    }
}
