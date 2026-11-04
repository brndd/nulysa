using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;
using Nulysa.Operations;
using Quartz;
using Quartz.Extensibility;
using Quartz.Impl;

namespace Nulysa.Tests;

public sealed class CommunicationsTests : MembershipTest
{
    private static MailVariant Variant(string language, string subject = "Monthly news", string body = "First edition") =>
        new(language, subject, [new(MailBlockKind.Paragraph, [new(body, Bold: true, Link: "https://example.test/events?a=1&b=2")])]);
    private static readonly IReadOnlyDictionary<string, string> NoFallbacks = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string> FinnishFallback = new Dictionary<string, string> { ["fi"] = "en" };
    private static Campaigns Campaign(IServiceProvider sp) => sp.GetRequiredService<Campaigns>();
    private static NewsletterPreferences Preferences(IServiceProvider sp) => sp.GetRequiredService<NewsletterPreferences>();
    private static Task Dispatch(WebApplication app, Guid deliveryId, int queueVersion = 0) =>
        Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(deliveryId, queueVersion: queueVersion));

    [Fact]
    public async Task Campaign_failures_and_old_backlog_do_not_raise_operational_mail_alarms()
    {
        var board = await Board();
        var transport = new RecordingMail { Outcome = SendOutcome.Rejected };
        await using var app = CommandsApp(transport);
        var id = Guid.NewGuid();
        var draft = await Run(app, sp => Campaign(sp).Save(board, id, null, "Monitoring", CampaignKind.Newsletter, [Variant("en")]));
        var failed = await Run(app, sp => Campaign(sp).SendTest(board, id, draft.Revision, "en", "bad@example.test"));
        await Run(app, sp => Campaign(sp).SendTest(board, id, draft.Revision, "en", "pending@example.test"));
        await Dispatch(app, failed);

        Clock.Now = Clock.Now.AddMinutes(10);
        await Schedules.Install(Scheduler, Clock, TimeZoneInfo.Utc);
        await using var db = Context();
        foreach (var schedule in Schedules.Names)
            await Observations.Record(db, schedule, Clock.Now, CancellationToken.None);
        var status = await WorkStatus.Read(db, Scheduler, Clock);
        Assert.True(status.Healthy, string.Join(", ", status.Issues));
        Assert.True(status.OldestWorkAgeSeconds >= 600);
        Assert.Equal(status.OldestWorkAgeSeconds, status.OldestCampaignWorkAgeSeconds);
        Assert.Null(status.OldestOperationalWorkAgeSeconds);
        var attention = await Run(app, sp => sp.GetRequiredService<BoardQueries>().Attention(board, 30));
        Assert.Equal(0, attention.EssentialMailProblems);
        var mail = await Run(app, sp => sp.GetRequiredService<MembershipQueries>().MailFailures(board, essentialOnly: false));
        Assert.Equal(0, mail.Total);
        var progress = await Run(app, sp => Campaign(sp).Progress(board, id));
        Assert.Contains(progress.Recipients, row => row.Id == failed && row.State == DeliveryState.Failed);
    }

    [Fact]
    public async Task Definite_rejection_survives_a_failed_retry_trigger_write()
    {
        var transport = new RecordingMail { Outcome = SendOutcome.Throttled };
        // PostgreSQL stores microseconds, while the application clock can report 100 ns ticks.
        Clock.Now = Clock.Now.AddTicks(1);
        await using var app = CommandsApp(transport);
        var application = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(application, Input()));
        await using var db = Context();
        var id = await db.Deliveries.Where(x => x.ApplicationId == application).Select(x => x.Id).SingleAsync();
        var key = MailQueue.JobKey(id);
        var detail = (await Scheduler.GetJobDetail(key))!;
        var trigger = TriggerBuilder.Create<MailDeliveryJob>().ForJob(key).StartNow().Build();
        var bundle = new TriggerFiredBundle
        {
            JobDetail = detail, Trigger = (IOperableTrigger)trigger, Calendar = null, Recovering = false,
            FireTimeUtc = Clock.Now, ScheduledFireTimeUtc = Clock.Now, PreviousFireTimeUtc = null, NextFireTimeUtc = null
        };
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE qrtz_triggers ADD CONSTRAINT test_reject_retry_trigger
            CHECK (job_group <> 'work') NOT VALID
            """);
        await Assert.ThrowsAnyAsync<SchedulerException>(() => Run(app, async sp =>
        {
            var job = new MailDeliveryJob(sp.GetRequiredService<MailDispatcher>());
            using var context = new JobExecutionContextImpl(Scheduler, bundle, job);
            await job.Execute(context);
        }));

        var pending = await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(DeliveryState.Pending, pending.State);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(Clock.Now.AddSeconds(30).AddTicks(-1), pending.RetryNotBefore);
        await Dispatch(app, id);
        Assert.Single(transport.Sent);

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE qrtz_triggers DROP CONSTRAINT test_reject_retry_trigger");
        await Scheduler.DeleteJob(key);
        Clock.Now = Clock.Now.AddSeconds(30);
        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
        Assert.NotNull(await Scheduler.GetJobDetail(key));
        transport.Outcome = SendOutcome.Accepted;
        await Dispatch(app, id);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(DeliveryState.Accepted, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == id)).State);
    }

    [Fact]
    public async Task Mail_repair_scans_past_a_full_healthy_batch_and_caps_repairs()
    {
        await using var app = CommandsApp();
        await using var db = Context();
        var deliveries = Enumerable.Range(0, 261).Select(index => new Delivery
        {
            Id = Guid.NewGuid(), Kind = MailKind.Verification, CreatedAt = Clock.Now.AddSeconds(index - 261)
        }).ToArray();
        db.Deliveries.AddRange(deliveries);
        await db.SaveChangesAsync();

        var scheduled = deliveries.Take(200).ToDictionary(
            delivery => JobBuilder.Create<MailDeliveryJob>().WithIdentity(MailQueue.JobKey(delivery.Id)).Build(),
            delivery => (IReadOnlyCollection<ITrigger>)[TriggerBuilder.Create<MailDeliveryJob>()
                .ForJob(MailQueue.JobKey(delivery.Id)).StartNow().Build()]);
        await Scheduler.ScheduleJobs(scheduled);
        var pagesRead = 0;
        await using var scanning = Build("commands", builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new BeforeReader(sql =>
                sql.Contains("ORDER BY", StringComparison.Ordinal) && sql.Contains("RetryNotBefore", StringComparison.Ordinal)
                    && ++pagesRead == 2, async () =>
                {
                    await using var worker = Context();
                    await worker.Deliveries.Where(x => x.Id == deliveries[0].Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(row => row.State, DeliveryState.Cancelled));
                })));
        });
        await Run(scanning, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());

        var repaired = (await Scheduler.GetJobDetails(deliveries.Skip(200).Select(x => MailQueue.JobKey(x.Id)).ToArray()))
            .Select(job => job.Key).ToHashSet();
        Assert.Equal(50, repaired.Count);
        Assert.All(deliveries.Skip(200).Take(50), delivery => Assert.Contains(MailQueue.JobKey(delivery.Id), repaired));

        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
        Assert.Equal(261, (await Scheduler.GetJobDetails(deliveries.Select(x => MailQueue.JobKey(x.Id)).ToArray())).Count);
    }

    [Fact]
    public async Task Erasure_waiting_for_a_failed_delivery_leaves_the_quartz_lock_available()
    {
        var board = await Board();
        var transport = new RecordingMail { Outcome = SendOutcome.Rejected };
        await using var app = CommandsApp(transport);
        var member = await Admit(app, board);
        var campaign = Guid.NewGuid();
        await Run(app, sp => Campaign(sp).Save(board, campaign, null, "Notice", CampaignKind.OfficialNotice, [Variant("fi")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, campaign, NoFallbacks));
        await Run(app, sp => Campaign(sp).Send(board, campaign, NoFallbacks, preview.Confirmation!));
        await using var db = Context();
        var delivery = await db.Deliveries.Where(x => x.CampaignId == campaign).Select(x => x.Id).SingleAsync();
        await Dispatch(app, delivery);
        var warning = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, member, warning, "Resignation"));
        Clock.Now = Clock.Now.AddDays(15);

        // Hold the same delivery row lock a campaign retry holds before entering Quartz.
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Deliveries\" WHERE \"Id\" = {delivery} FOR UPDATE");
        var blocker = await db.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var erase = Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(member));
        try
        {
            await using var observer = Context();
            await Until(() => observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                    AND {blocker} = ANY(pg_blocking_pids(pid))
                ) AS "Value"
                """).SingleAsync(), seconds: 5);
            var schedulerLock = await db.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM qrtz_locks WHERE sched_name = {Scheduler.SchedulerName}
                AND lock_name = 'TRIGGER_ACCESS' FOR UPDATE NOWAIT
                """).ToListAsync();
            Assert.Single(schedulerLock);
        }
        finally
        {
            await tx.RollbackAsync();
        }
        Assert.True(await erase.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await db.Deliveries.AnyAsync(x => x.UserId == member));
        Assert.Null(await Scheduler.GetJobDetail(MailQueue.JobKey(warning)));
    }

    [Fact]
    public async Task Provider_throttle_pauses_other_deliveries_without_spending_their_attempts()
    {
        var transport = new RecordingMail { Outcome = SendOutcome.ProviderThrottled };
        await using var app = CommandsApp(transport);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(first, Input()));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(second, Input("other@example.test")));
        await using var db = Context();
        var firstId = await db.Deliveries.Where(x => x.ApplicationId == first).Select(x => x.Id).SingleAsync();
        var secondId = await db.Deliveries.Where(x => x.ApplicationId == second).Select(x => x.Id).SingleAsync();

        await Dispatch(app, firstId);
        transport.Outcome = SendOutcome.Accepted;
        await Dispatch(app, secondId);
        Assert.Single(transport.Sent);
        Assert.Equal(0, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == secondId)).Attempts);
        Assert.Equal(Clock.Now.AddMinutes(1), (await db.MailRateWindows.SingleAsync()).ProviderPausedUntil);

        Clock.Now = Clock.Now.AddMinutes(1);
        await Dispatch(app, secondId);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal(DeliveryState.Accepted, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == secondId)).State);
    }

    [Fact]
    public async Task Newsletter_confirmation_binds_the_preview_and_dispatch_rechecks_the_audience()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var inactive = await Admit(app, board, "inactive@example.test");
        var lateOptOut = await Admit(app, board, "late-opt-out@example.test");
        var lateDeletion = await Admit(app, board, "late-deletion@example.test");
        var earlyOptOut = await Admit(app, board, "early-opt-out@example.test");
        var earlyDeletion = await Admit(app, board, "early-deletion@example.test");
        Clock.Now = Clock.Now.AddMonths(2); // None of these members paid their initial invoice.
        await Run(app, sp => Preferences(sp).Set(Actor(earlyOptOut), false));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, earlyDeletion, Guid.NewGuid(), "Resignation"));
        var id = Guid.NewGuid();
        var first = await Run(app, sp => Campaign(sp).Save(board, id, null, "November", CampaignKind.Newsletter, [Variant("en")]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => Campaign(sp).Preview(Actor(inactive), id, NoFallbacks)));
        var missing = await Run(app, sp => Campaign(sp).Preview(board, id, NoFallbacks));
        Assert.Null(missing.Confirmation);
        Assert.Equal(new CampaignLanguageGroup("fi", 3, null, null), Assert.Single(missing.Groups));

        var preview = await Run(app, sp => Campaign(sp).Preview(board, id, FinnishFallback));
        var group = Assert.Single(preview.Groups);
        Assert.Equal("en", group.OutgoingLanguage);
        Assert.Contains("Unsubscribe from newsletters", group.Content!.Text);
        Assert.DoesNotContain("token", group.Content.Html);
        await Run(app, sp => Campaign(sp).Save(board, id, first.Revision, "November", CampaignKind.Newsletter, [Variant("en", body: "Updated edition")]));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => Campaign(sp).Send(board, id, FinnishFallback, preview.Confirmation!)));

        var changedAudience = await Run(app, sp => Campaign(sp).Preview(board, id, FinnishFallback));
        await Run(app, sp => Preferences(sp).Set(Actor(earlyOptOut), true));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => Campaign(sp).Send(board, id, FinnishFallback, changedAudience.Confirmation!)));
        await Run(app, sp => Preferences(sp).Set(Actor(earlyOptOut), false));

        var confirmed = await Run(app, sp => Campaign(sp).Preview(board, id, FinnishFallback));
        // Reject the new Quartz jobs after EF has written the campaign and recipient rows.
        await using (var rollback = Context())
        {
            await rollback.Database.ExecuteSqlRawAsync("""
                ALTER TABLE qrtz_job_details ADD CONSTRAINT test_reject_campaign_jobs
                CHECK (job_group <> 'work') NOT VALID
                """);
            await Assert.ThrowsAnyAsync<SchedulerException>(() =>
                Run(app, sp => Campaign(sp).Send(board, id, FinnishFallback, confirmed.Confirmation!)));
            Assert.False(await rollback.Deliveries.AnyAsync(x => x.CampaignId == id));
            Assert.Null((await rollback.MailCampaigns.SingleAsync(x => x.Id == id)).QueuedAt);
            await rollback.Database.ExecuteSqlRawAsync("ALTER TABLE qrtz_job_details DROP CONSTRAINT test_reject_campaign_jobs");
        }
        var submissions = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => Run(app, sp => Campaign(sp).Send(board, id, FinnishFallback, confirmed.Confirmation!))));
        Assert.All(submissions, sent => Assert.Equal(3, sent.RecipientCount));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp =>
            Campaign(sp).Save(board, id, confirmed.Campaign.Revision, "Edit after send", CampaignKind.OfficialNotice, [Variant("en")])));

        await Run(app, sp => Preferences(sp).Set(Actor(lateOptOut), false));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, lateDeletion, Guid.NewGuid(), "Resignation"));
        await using var db = Context();
        var deliveries = await db.Deliveries.AsNoTracking().Where(x => x.CampaignId == id).ToArrayAsync();
        Assert.Equal(3, deliveries.Length);
        var queued = await Scheduler.GetJobDetail(MailQueue.JobKey(deliveries.Single(x => x.UserId == inactive).Id));
        Assert.NotNull(queued);
        Assert.Equal(new[] { "campaign", "delivery_id", "enqueued_at" }, queued.JobDataMap.Keys.Order());
        foreach (var delivery in deliveries)
            await Dispatch(app, delivery.Id);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal("inactive@example.test", sent.To);
        Assert.Contains("Updated edition", sent.Text);
        Assert.NotNull(sent.UnsubscribeUrl);

        var progress = await Run(app, sp => Campaign(sp).Progress(board, id));
        Assert.Equal(1, progress.Counts[DeliveryState.Accepted]);
        Assert.Equal(2, progress.Counts[DeliveryState.Cancelled]);
        Assert.All(progress.Recipients, row => Assert.Equal(("fi", "en"), (row.PreferredLanguage, row.OutgoingLanguage)));
    }

    [Fact]
    public async Task Draft_pagination_detects_a_full_final_page()
    {
        var board = await Board();
        await using var app = CommandsApp();
        await using var db = Context();
        db.MailCampaigns.AddRange(Enumerable.Range(0, Campaigns.PageSize).Select(i => new MailCampaign
        {
            Id = Guid.NewGuid(), Title = $"Draft {i}", UpdatedAt = Clock.Now
        }));
        await db.SaveChangesAsync();
        var full = await Run(app, sp => Campaign(sp).ListDrafts(board));
        Assert.Equal(Campaigns.PageSize, full.Entries.Count);
        Assert.False(full.HasNext);

        db.MailCampaigns.Add(new() { Id = Guid.NewGuid(), Title = "One more draft", UpdatedAt = Clock.Now });
        await db.SaveChangesAsync();
        Assert.True((await Run(app, sp => Campaign(sp).ListDrafts(board))).HasNext);
        var last = await Run(app, sp => Campaign(sp).ListDrafts(board, 2));
        Assert.Single(last.Entries);
        Assert.False(last.HasNext);
    }

    [Fact]
    public async Task Concurrent_draft_saves_cannot_both_use_the_same_revision()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = Guid.NewGuid();
        var draft = await Run(app, sp => Campaign(sp).Save(board, id, null, "Original", CampaignKind.Newsletter, [Variant("en")]));
        var errors = await Task.WhenAll(new[] { "First", "Second" }.Select(title => Record.ExceptionAsync(() =>
            Run(app, sp => Campaign(sp).Save(board, id, draft.Revision, title, CampaignKind.Newsletter, [Variant("en")])))));

        Assert.Single(errors, error => error is null);
        Assert.Equal("The draft has changed. Reload it before editing.",
            Assert.IsType<ValidationException>(Assert.Single(errors, error => error is not null)).Message);
    }

    [Fact]
    public async Task Concurrent_save_and_send_cannot_both_use_the_same_revision()
    {
        var board = await Board();
        await using var app = CommandsApp();
        await Admit(app, board);
        var id = Guid.NewGuid();
        var draft = await Run(app, sp => Campaign(sp).Save(board, id, null, "Original", CampaignKind.Newsletter, [Variant("fi")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, id, NoFallbacks));
        Assert.NotNull(preview.Confirmation);
        var errors = await Task.WhenAll(
            Record.ExceptionAsync(() => Run(app, sp => Campaign(sp).Save(board, id, draft.Revision, "Edited", CampaignKind.Newsletter, [Variant("fi")]))),
            Record.ExceptionAsync(() => Run(app, sp => Campaign(sp).Send(board, id, NoFallbacks, preview.Confirmation))));

        Assert.Single(errors, error => error is null);
        var current = await Run(app, sp => Campaign(sp).Get(board, id));
        if (current.Campaign.QueuedAt is null)
        {
            Assert.Equal("Edited", current.Campaign.Title);
            Assert.Equal("The draft, audience or fallbacks changed. Preview and confirm again.", Assert.IsType<ValidationException>(errors[1]).Message);
        }
        else
        {
            Assert.Equal("Original", current.Campaign.Title);
            Assert.Equal("This campaign has been queued. Copy it to write a new draft.", Assert.IsType<ValidationException>(errors[0]).Message);
        }
    }

    [Fact]
    public async Task Campaign_progress_uses_one_snapshot_when_acceptance_lands_between_queries()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        await Admit(app, board);
        var id = Guid.NewGuid();
        await Run(app, sp => Campaign(sp).Save(board, id, null, "Snapshot", CampaignKind.Newsletter, [Variant("fi")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, id, NoFallbacks));
        await Run(app, sp => Campaign(sp).Send(board, id, NoFallbacks, preview.Confirmation!));
        await using var db = Context();
        var delivery = await db.Deliveries.Where(x => x.CampaignId == id).Select(x => x.Id).SingleAsync();

        await using var reading = Build("commands", builder => builder.Services.AddDbContext<AppDbContext>(options =>
            options.AddInterceptors(new BeforeReader(sql => sql.Contains("GROUP BY", StringComparison.Ordinal) &&
                sql.Contains("\"Deliveries\"", StringComparison.Ordinal), () => Dispatch(app, delivery)))));
        var snapshot = await Run(reading, sp => Campaign(sp).Progress(board, id));
        Assert.Equal(0, snapshot.Campaign.AcceptedRecipients);
        Assert.Equal(1, snapshot.Counts[DeliveryState.Pending]);
        Assert.Equal(DeliveryState.Pending, Assert.Single(snapshot.Recipients).State);
        var fresh = await Run(app, sp => Campaign(sp).Progress(board, id));
        Assert.Equal(1, fresh.Campaign.AcceptedRecipients);
        Assert.Equal(DeliveryState.Accepted, Assert.Single(fresh.Recipients).State);
    }

    private sealed class BeforeReader(Func<string, bool> matches, Func<Task> beforeRead) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (matches(command.CommandText))
                await beforeRead();
            return result;
        }
    }

    [Fact]
    public async Task A_test_uses_its_saved_snapshot_and_never_includes_a_working_unsubscribe_link()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = Guid.NewGuid();
        const string markup = "<img src=x onerror=alert(1)>";
        var saved = await Run(app, sp => Campaign(sp).Save(board, id, null, "Draft", CampaignKind.Newsletter, [Variant("en", body: markup)]));
        var unsafeVariant = new MailVariant("en", "Unsafe", [new(MailBlockKind.Paragraph, [new("Click", Link: "jAvAsCrIpT:alert(1)")])]);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => Campaign(sp).Save(board, id, saved.Revision, "Draft", CampaignKind.Newsletter, [unsafeVariant])));
        var test = await Run(app, sp => Campaign(sp).SendTest(board, id, saved.Revision, "en", "reviewer@example.test"));
        await Run(app, sp => Campaign(sp).Save(board, id, saved.Revision, "Draft", CampaignKind.Newsletter, [Variant("en", body: "Later edit")]));

        await using var db = Context();
        var payload = (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == test)).ProtectedTestMessage;
        Assert.NotNull(payload);
        Assert.DoesNotContain("reviewer@example.test", payload);
        await Dispatch(app, test);

        var sent = Assert.Single(transport.Sent);
        Assert.Equal("reviewer@example.test", sent.To);
        Assert.StartsWith("[TEST]", sent.Subject);
        Assert.Contains(markup, sent.Text);
        Assert.Contains("&lt;img", sent.Html);
        Assert.DoesNotContain("<img", sent.Html);
        Assert.Contains("a=1&amp;b=2", sent.Html);
        Assert.Null(sent.UnsubscribeUrl);
        Assert.Null((await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == test)).ProtectedTestMessage);
        var progress = await Run(app, sp => Campaign(sp).Progress(board, id));
        Assert.Empty(progress.Counts);
        Assert.True(Assert.Single(progress.Recipients).Test);
    }

    [Fact]
    public async Task Official_notices_ignore_optional_preferences_and_only_definite_failures_can_be_retried()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var recipients = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            var member = await Admit(app, board, $"recipient{i}@example.test");
            recipients.Add(member);
            await Run(app, sp => Preferences(sp).Set(Actor(member), false));
        }
        await using var db = Context();
        await db.BillingAccounts.Where(x => x.UserId == recipients[0]).ExecuteUpdateAsync(x => x.SetProperty(a => a.EmailInvoices, false));

        var id = Guid.NewGuid();
        await Run(app, sp => Campaign(sp).Save(board, id, null, "Meeting", CampaignKind.OfficialNotice, [Variant("fi", "Kokouskutsu")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, id, NoFallbacks));
        await Run(app, sp => Campaign(sp).Send(board, id, NoFallbacks, preview.Confirmation!));
        var deliveries = await db.Deliveries.AsNoTracking().Where(x => x.CampaignId == id).OrderBy(x => x.UserId).ToArrayAsync();
        Assert.Equal(3, deliveries.Length);

        var outcomes = new[] { SendOutcome.Accepted, SendOutcome.Rejected, SendOutcome.Uncertain };
        for (var i = 0; i < outcomes.Length; i++)
        {
            transport.Outcome = outcomes[i];
            await Dispatch(app, deliveries[i].Id);
        }
        var requested = deliveries.Select(x => x.Id).ToArray();
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => Campaign(sp).RetryFailed(board, id, requested, "Corrected mailbox", false)));
        var retried = await Run(app, sp => Campaign(sp).RetryFailed(board, id, requested, "Corrected mailbox", true));
        Assert.Equal(1, retried);
        var failed = deliveries[1].Id;
        Assert.NotNull(await Scheduler.GetJobDetail(MailQueue.JobKey(failed, 1)));

        transport.Outcome = SendOutcome.Accepted;
        await Dispatch(app, failed); // The old job generation is no longer allowed to send.
        Assert.Equal(3, transport.Sent.Count);
        await Dispatch(app, failed, 1);
        await Dispatch(app, failed, 1); // A duplicated job cannot repeat acceptance.
        Assert.Equal(4, transport.Sent.Count);
        Assert.All(transport.Sent, mail => Assert.Null(mail.UnsubscribeUrl));
        var progress = await Run(app, sp => Campaign(sp).Progress(board, id));
        Assert.Equal(2, progress.Counts[DeliveryState.Accepted]);
        Assert.Equal(1, progress.Counts[DeliveryState.Review]);
        Assert.Equal(2, progress.Recipients.Single(x => x.Id == failed).Attempts);
        Assert.Equal("Corrected mailbox", (await db.MailDeliveryRetries.SingleAsync()).Reason);

        var reviewPage = await Run(app, sp => Campaign(sp).Progress(board, id, 999, DeliveryState.Review));
        Assert.Equal(1, reviewPage.Page);
        Assert.Equal(deliveries[2].Id, Assert.Single(reviewPage.Recipients).Id);

        Assert.Empty((await Run(app, sp => Campaign(sp).ListDrafts(board))).Entries);
        Clock.Now = Clock.Now.AddDays(31);
        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
        Assert.False(await db.Deliveries.AnyAsync(x => x.CampaignId == id));
        var history = await Run(app, sp => Campaign(sp).ListHistory(board));
        var entry = Assert.Single(history.Entries);
        Assert.False(entry.Processing);
        Assert.Equal(3, entry.Campaign.RecipientCount);
        Assert.Equal(2, entry.Campaign.AcceptedRecipients);
    }

    [Fact]
    public async Task Newsletter_unsubscribe_uses_a_signed_post_and_does_not_change_invoice_preference()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var user = await Admit(app, board);
        Assert.True(await Run(app, sp => Preferences(sp).Get(Actor(user))));
        var id = Guid.NewGuid();
        await Run(app, sp => Campaign(sp).Save(board, id, null, "News", CampaignKind.Newsletter, [Variant("fi")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, id, NoFallbacks));
        await Run(app, sp => Campaign(sp).Send(board, id, NoFallbacks, preview.Confirmation!));
        await using var db = Context();
        await Dispatch(app, (await db.Deliveries.SingleAsync(x => x.CampaignId == id)).Id);
        var unsubscribe = new Uri(Assert.Single(transport.Sent).UnsubscribeUrl!);
        Assert.Equal(NewsletterPreferences.UnsubscribePath, unsubscribe.AbsolutePath);
        Assert.DoesNotContain(Email, unsubscribe.Query);

        await using var web = await StartWeb();
        using var client = new HttpClient { BaseAddress = new Uri(Settings["Oidc:Issuer"]!) };
        await client.GetAsync(unsubscribe.PathAndQuery); // Link scanners may fetch it; GET never opts anyone out.
        Assert.True(await Run(app, sp => Preferences(sp).Get(Actor(user))));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(unsubscribe.AbsolutePath + "?token=forged", null)).StatusCode);
        using var request = new FormUrlEncodedContent(new Dictionary<string, string> { ["List-Unsubscribe"] = "One-Click" });
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(unsubscribe.PathAndQuery, request)).StatusCode);
        Assert.False(await Run(app, sp => Preferences(sp).Get(Actor(user))));
        Assert.True((await db.BillingAccounts.SingleAsync(x => x.UserId == user)).EmailInvoices);

        await Run(app, sp => Preferences(sp).Set(Actor(user), true));
        await db.Users.Where(x => x.Id == user).ExecuteUpdateAsync(x => x.SetProperty(u => u.Email, "replacement@example.test"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync(unsubscribe.PathAndQuery, null)).StatusCode);
        Assert.True(await Run(app, sp => Preferences(sp).Get(Actor(user))));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_later_verification_sends_before_the_bulk_campaign_finishes()
    {
        Settings["Mail:MaxPerMinute"] = "4";
        Settings["Mail:SpoolPath"] = Path.Combine(DirectoryPath, "paced-campaign-mail");
        var board = await Board();
        await using var app = CommandsApp();
        await using var db = Context();
        // Seed the audience without queuing approval mail.
        var recipients = Enumerable.Range(1, 3).Select(number => new User
        {
            UserName = $"bulk-{number}@example.test", Email = $"bulk-{number}@example.test",
            EmailConfirmed = true, Admitted = true, Language = "en"
        }).ToArray();
        db.Users.AddRange(recipients);
        await db.SaveChangesAsync();
        db.Members.AddRange(recipients.Select(user => new Member { UserId = user.Id, JoinedOn = new DateOnly(2026, 1, 1) }));
        await db.SaveChangesAsync();

        var campaignId = Guid.NewGuid();
        await Run(app, sp => Campaign(sp).Save(board, campaignId, null, "Paced campaign", CampaignKind.Newsletter, [Variant("en")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, campaignId, NoFallbacks));
        await Run(app, sp => Campaign(sp).Send(board, campaignId, NoFallbacks, preview.Confirmation!));
        var bulk = await db.Deliveries.Where(x => x.CampaignId == campaignId).Select(x => x.Id).ToArrayAsync();
        var bulkStarts = new List<DateTimeOffset>();
        foreach (var id in bulk)
            bulkStarts.Add(Assert.Single(await Scheduler.GetTriggersOfJob(MailQueue.JobKey(id))).StartTimeUtc);
        bulkStarts.Sort();
        Assert.Equal(3, bulkStarts.Count);
        Assert.Equal(TimeSpan.FromSeconds(15), bulkStarts[1] - bulkStarts[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), bulkStarts[2] - bulkStarts[1]);

        var application = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(application, Input()));
        var verification = await db.Deliveries.Where(x => x.ApplicationId == application).Select(x => x.Id).SingleAsync();

        StartWorker();
        await Until(() => db.Deliveries.AnyAsync(x => x.Id == verification && x.State == DeliveryState.Accepted));
        Assert.True(await db.Deliveries.AnyAsync(x => x.CampaignId == campaignId && x.State == DeliveryState.Pending));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Persisted_campaign_dispatch_defers_when_the_provider_window_is_full()
    {
        Settings["Mail:MaxPerMinute"] = "1";
        Settings["Mail:SpoolPath"] = Path.Combine(DirectoryPath, "campaign-mail");
        var board = await Board();
        await using var app = CommandsApp();
        await using var db = Context();
        // Isolate campaign scheduling from approval mail for this worker test.
        var user = new User
        {
            UserName = Email, Email = Email, EmailConfirmed = true, Admitted = true, Language = "en"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.Members.Add(new() { UserId = user.Id, JoinedOn = new DateOnly(2026, 1, 1) });
        await db.SaveChangesAsync();

        var id = Guid.NewGuid();
        await Run(app, sp => Campaign(sp).Save(board, id, null, "Worker campaign", CampaignKind.Newsletter, [Variant("en")]));
        var preview = await Run(app, sp => Campaign(sp).Preview(board, id, NoFallbacks));
        await Run(app, sp => Campaign(sp).Send(board, id, NoFallbacks, preview.Confirmation!));
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync(x => x.CampaignId == id);
        var key = MailQueue.JobKey(delivery.Id);
        var initialTrigger = Assert.Single((await Scheduler.QueryTriggers(new() { Job = key })).Items).Key;
        db.MailRateWindows.Add(new() { StartedAt = DateTimeOffset.UtcNow.AddSeconds(-45), Attempts = 1 });
        await db.SaveChangesAsync();

        StartWorker();
        await Until(async () => ((await Scheduler.QueryTriggers(new() { Job = key })).Items).Any(x => x.Key != initialTrigger));
        var pending = await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == delivery.Id);
        Assert.Equal(DeliveryState.Pending, pending.State);
        Assert.Equal(0, pending.Attempts);

        await Until(async () => await db.Deliveries.AnyAsync(x => x.Id == delivery.Id && x.State == DeliveryState.Accepted));
        var mail = JsonSerializer.Deserialize<OutgoingMail>(await File.ReadAllTextAsync(
            Path.Combine(Settings["Mail:SpoolPath"]!, delivery.Id.ToString("N") + ".json")))!;
        Assert.Equal(Email, mail.To);
        Assert.Contains("<strong>First edition</strong>", mail.Html);
        Assert.NotNull(mail.UnsubscribeUrl);
    }

    [Fact]
    public async Task Automatic_overrides_validate_required_fields_and_deliver_the_real_link()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var template = new OperationalTemplateSource("fi", @"{% raw %}{{literal}}{% endraw %} — {{association}}",
            "## Kiitos hakemuksestasi\n\nVahvista sähköpostisi viimeistään {{verification_expires_at}}: <{{verification_url}}>");
        Task Save(OperationalTemplateSource source, string fallback) => Run(app, sp => sp.GetRequiredService<OperationalTemplates>()
            .Save(board, MailKind.Verification, source, fallback));
        await Assert.ThrowsAsync<ValidationException>(() => Save(template, "sv"));
        var missing = await Assert.ThrowsAsync<ValidationException>(() => Save(template with { Body = "Missing verification link." }, "en"));
        Assert.Contains("{{verification_url}}", missing.Message);
        await Save(template, "en");
        await Save(new("en", "Verify", "<{{verification_url}}> by {{verification_expires_at}}."), "en");

        var templates = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        Assert.Equal("en", templates.Single(x => x.Kind == MailKind.Verification).FallbackLanguage);
        Assert.All(templates.Single(x => x.Kind == MailKind.Verification).Languages, x => Assert.True(x.Custom));

        var id = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, Input())); // Finnish preference.
        await using var db = Context();
        await Dispatch(app, (await db.Deliveries.SingleAsync(x => x.ApplicationId == id)).Id);
        var sent = Assert.Single(transport.Sent);
        Assert.StartsWith("{{literal}}", sent.Subject);
        Assert.Contains("<h2>Kiitos hakemuksestasi</h2>", sent.Html);
        Assert.Contains("<a href=", sent.Html);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Verify(id, Link(sent)["token"]!));
        Assert.NotNull((await db.Applications.AsNoTracking().SingleAsync(x => x.Id == id)).VerifiedAt);
    }

    [Fact]
    public async Task Liquid_override_requires_operational_fields_in_every_branch()
    {
        var board = await Board();
        await using var app = CommandsApp(new RecordingMail());
        var template = new OperationalTemplateSource("en", "Deletion warning", """
            Typical deletion window: {{deletion_window_start}}–{{deletion_window_end}}.

            {% if unspent_funds %}
            Earliest deletion: {{deletion_eligible_at}}. Remaining funds: {{unspent_funds}}.
            {% else %}
            Contact the board.
            {% endif %}
            """);
        var error = await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<OperationalTemplates>()
            .Save(board, MailKind.DeletionWarning, template, "en")));
        Assert.Contains("{{deletion_eligible_at}}", error.Message);
    }

    [Fact]
    public async Task Broken_automatic_override_uses_same_language_default_and_appears_on_admin_overview_until_reset()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        const string brokenBody = "{{retired_field}}";
        await using var db = Context();
        db.OperationalMailTemplates.Add(new()
        {
            Kind = MailKind.Verification,
            FallbackLanguage = "en",
            Content = JsonSerializer.Serialize(new Dictionary<string, OperationalTemplateSource>
            {
                ["fi"] = new("fi", "Broken subject", brokenBody),
                ["en"] = new("en", "Custom English", "<{{verification_url}}> by {{verification_expires_at}}.")
            }),
            UpdatedBy = Authority.Id(board),
            UpdatedAt = Clock.Now
        });
        await db.SaveChangesAsync();

        var attention = await Run(app, sp => sp.GetRequiredService<BoardQueries>().Attention(board, 30));
        Assert.Equal(new(MailKind.Verification, "fi"), Assert.Single(attention.TemplateProblems));
        Assert.Equal(0, attention.EssentialMailProblems);
        var list = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        var broken = list.Single(x => x.Kind == MailKind.Verification).Languages.Single(x => x.Source.Language == "fi");
        Assert.Contains("retired_field", broken.Problem);
        Assert.Equal(brokenBody, broken.Source.Body);

        var id = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, Input()));
        await Dispatch(app, (await db.Deliveries.SingleAsync(x => x.ApplicationId == id)).Id);
        Assert.Contains("Vahvista jäsenhakemuksesi", Assert.Single(transport.Sent).Subject);

        await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().Reset(board, MailKind.Verification, "fi"));
        attention = await Run(app, sp => sp.GetRequiredService<BoardQueries>().Attention(board, 30));
        Assert.Empty(attention.TemplateProblems);
        list = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        var remaining = Assert.Single(list.Single(x => x.Kind == MailKind.Verification).Languages, x => x.Custom);
        Assert.Equal("Custom English", remaining.Source.Subject);
    }

    [Fact]
    public async Task Previous_markup_opens_as_liquid_and_keeps_its_condition_and_literals()
    {
        var board = await Board();
        await using var app = CommandsApp(new RecordingMail());
        await using var db = Context();
        db.OperationalMailTemplates.Add(new()
        {
            Kind = MailKind.Rejection,
            FallbackLanguage = "en",
            Content = JsonSerializer.Serialize(new Dictionary<string, OperationalTemplateSource>
            {
                ["en"] = new("en", @"Decision \{{literal}} \{\{other\}\}",
                    "Decision text.\n\n{{#if rejection_reason}}\n{{rejection_reason}}\n{{/if}}")
            }),
            UpdatedBy = Authority.Id(board),
            UpdatedAt = Clock.Now
        });
        await db.SaveChangesAsync();

        var list = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        var converted = Assert.Single(list.Single(x => x.Kind == MailKind.Rejection).Languages, x => x.Custom);
        Assert.Null(converted.Problem);
        Assert.Contains("{% if rejection_reason %}", converted.Source.Body);
        var preview = OperationalTemplates.Preview(MailKind.Rejection, converted.Source);
        Assert.Equal("Decision {{literal}} {{other}}", preview.Subject);
        Assert.Contains("currently at capacity", preview.Text);

        await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().Save(board, MailKind.Rejection, converted.Source, "en"));
        list = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        Assert.Equal(converted, Assert.Single(list.Single(x => x.Kind == MailKind.Rejection).Languages, x => x.Custom));
    }

    [Fact]
    public async Task Changing_the_automatic_fallback_does_not_override_shipped_wording()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var templates = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        var shipped = templates.Single(x => x.Kind == MailKind.Verification).Languages.Single(x => x.Source.Language == "en");
        await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().Save(board, MailKind.Verification, shipped.Source, "fi"));

        templates = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        var saved = templates.Single(x => x.Kind == MailKind.Verification);
        Assert.Equal("fi", saved.FallbackLanguage);
        Assert.All(saved.Languages, language => Assert.False(language.Custom));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Previous_wrapper_can_be_opened_as_a_full_markdown_override(bool hasOriginalMarkdown)
    {
        var board = await Board();
        await using var app = CommandsApp(new RecordingMail());
        await using var db = Context();
        var literal = "Text with {{operational_details}}, {% if old %} and {{{%: " + new string('>', 20_001);
        var wrapper = new MailVariant("en", "{{association}}: {{subject}}",
            [new(MailBlockKind.Heading, [new("Welcome to the association")]), new(MailBlockKind.Paragraph, [new(literal)]),
                new(MailBlockKind.OperationalDetails, [])])
        {
            Markdown = hasOriginalMarkdown ? "# Welcome to the association\n\n" + literal + "\n\n{{operational_details}}" : null
        };
        db.OperationalMailTemplates.Add(new()
        {
            Kind = MailKind.Verification,
            FallbackLanguage = "en",
            Content = JsonSerializer.Serialize<MailVariant[]>([wrapper]),
            UpdatedBy = Authority.Id(board),
            UpdatedAt = Clock.Now
        });
        await db.SaveChangesAsync();

        var list = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        var converted = Assert.Single(list.Single(x => x.Kind == MailKind.Verification).Languages, x => x.Custom);
        Assert.Null(converted.Problem);
        Assert.Equal("{{association}}: Verify your membership application", converted.Source.Subject);
        Assert.StartsWith(hasOriginalMarkdown ? "# Welcome to the association" : "## Welcome to the association", converted.Source.Body);
        Assert.Contains("<{{verification_url}}>", converted.Source.Body);
        Assert.Equal(!hasOriginalMarkdown, converted.Source.Body.Length > MailMarkdown.SourceLimit);
        var content = OperationalTemplates.Preview(MailKind.Verification, converted.Source);
        Assert.Contains(literal, content.Text);
        await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().Save(board, MailKind.Verification, converted.Source, "en"));
    }
}
