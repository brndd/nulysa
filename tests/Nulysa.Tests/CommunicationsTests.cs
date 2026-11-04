using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Communications;
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
    public async Task Automatic_template_requires_explicit_fallback_and_keeps_the_real_verification_link()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var template = new MailVariant("en", "{{association}}: {{subject}}",
            [new(MailBlockKind.Heading, [new("Welcome to the association")]), new(MailBlockKind.OperationalDetails, [])]);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<OperationalTemplates>()
            .Save(board, MailKind.Verification, [template], "fi")));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<OperationalTemplates>()
            .Save(board, MailKind.Verification, [Variant("en")], "en")));
        await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().Save(board, MailKind.Verification, [template], "en"));
        var templates = await Run(app, sp => sp.GetRequiredService<OperationalTemplates>().List(board));
        Assert.Equal("en", templates.Single(x => x.Kind == MailKind.Verification).FallbackLanguage);

        var id = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, Input())); // Finnish preference.
        await using var db = Context();
        await Dispatch(app, (await db.Deliveries.SingleAsync(x => x.ApplicationId == id)).Id);
        var sent = Assert.Single(transport.Sent);
        Assert.Contains("Verify your membership application", sent.Subject);
        Assert.Contains("<h2>Welcome to the association</h2>", sent.Html);
        Assert.Contains("Verify your email", sent.Text);
        Assert.Contains("<a href=", sent.Html);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Verify(id, Link(sent)["token"]!));
        Assert.NotNull((await db.Applications.AsNoTracking().SingleAsync(x => x.Id == id)).VerifiedAt);
    }
}
