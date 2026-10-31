using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nulysa.Communications;
using Nulysa.Membership;
using Quartz;

namespace Nulysa.Tests;

public sealed class MailTransportTests
{
    [Fact]
    public async Task Smtp_sends_plain_text_with_delivery_header_and_accepts_provider_acknowledgement()
    {
        await using var server = new FakeSmtpServer("250 queued");
        var transport = Transport(server.Port);
        var id = Guid.NewGuid();
        var result = await transport.Send(id, new("member@example.test", "Subject", "Plain text; not executable <script>"), CancellationToken.None);
        Assert.Equal(SendOutcome.Accepted, result.Outcome);
        Assert.Null(result.ProviderId);
        var transcript = await server.Transcript;
        Assert.Contains($"X-Nulysa-Delivery-ID: {id:D}", transcript);
        Assert.Contains("Subject: Subject", transcript);
        Assert.Contains("Plain text; not executable <script>", transcript);
    }

    [Theory]
    [InlineData("450 mailbox busy", SendOutcome.Throttled)]
    [InlineData("550 mailbox unavailable", SendOutcome.Rejected)]
    public async Task Smtp_classifies_transient_and_permanent_rejections(string reply, SendOutcome expected)
    {
        await using var server = new FakeSmtpServer(reply);
        var result = await Transport(server.Port).Send(Guid.NewGuid(), new("member@example.test", "Subject", "Text"), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task Lost_SMTP_connection_is_uncertain_and_never_retried_by_the_adapter()
    {
        var result = await Transport(1).Send(Guid.NewGuid(), new("member@example.test", "Subject", "Text"), CancellationToken.None);
        Assert.Equal(SendOutcome.Uncertain, result.Outcome);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Stalled_SMTP_send_times_out_without_caller_cancellation()
    {
        await using var server = new FakeSmtpServer(null);
        var result = await Transport(server.Port).Send(Guid.NewGuid(), new("member@example.test", "Subject", "Text"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(SendOutcome.Uncertain, result.Outcome);
        Assert.Contains("Text", await server.Transcript);
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_a_stalled_SMTP_send()
    {
        await using var server = new FakeSmtpServer(null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await Transport(server.Port).Send(Guid.NewGuid(), new("member@example.test", "Subject", "Text"), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SendOutcome.Uncertain, result.Outcome);
        Assert.Contains("Text", await server.Transcript);
    }

    [Fact]
    public async Task Development_transport_prints_spools_and_still_sends_when_SMTP_is_configured()
    {
        var path = Path.Combine("/tmp/opencode", "nulysa-mail-" + Guid.NewGuid().ToString("N"));
        var inner = new RecordingMail();
        using var output = new StringWriter();
        var id = Guid.NewGuid();
        var transport = new DevelopmentMailTransport(inner,
            Options.Create(new MailOptions { Transport = "Smtp", SpoolPath = path }),
            new ConfigurationBuilder().Build(), new TestEnvironment { EnvironmentName = "Development" }, output);
        var result = await transport.Send(id, new("member@example.test", "Subject", "Body"), CancellationToken.None);
        Assert.Equal(SendOutcome.Accepted, result.Outcome);
        Assert.Single(inner.Sent);
        Assert.Contains("member@example.test", output.ToString());
        Assert.Contains("Body", output.ToString());
        var spooled = JsonSerializer.Deserialize<OutgoingMail>(await File.ReadAllTextAsync(Path.Combine(path, id.ToString("N") + ".json")))!;
        Assert.Equal("Subject", spooled.Subject);
        Directory.Delete(path, true);
    }

    [Fact]
    public async Task Development_transport_refuses_to_run_in_production()
    {
        var transport = new DevelopmentMailTransport(new RecordingMail(),
            Options.Create(new MailOptions { Transport = "Development" }), new ConfigurationBuilder().Build(), new TestEnvironment());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.Send(Guid.NewGuid(), new("member@example.test", "Subject", "Body"), CancellationToken.None));
    }

    private static SmtpMailTransport Transport(int port) => new(
        Options.Create(new MailOptions { Transport = "Smtp", Host = "127.0.0.1", Port = port, FromEmail = "from@example.test", EnableSsl = false }),
        new ConfigurationBuilder().Build());

    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task<string> transcript;
        public FakeSmtpServer(string? dataReply)
        {
            listener.Start();
            transcript = Run(dataReply);
        }
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public Task<string> Transcript => transcript;
        private async Task<string> Run(string? dataReply)
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
            var text = new StringBuilder();
            await writer.WriteLineAsync("220 localhost");
            while (await reader.ReadLineAsync() is { } line)
            {
                text.AppendLine(line);
                if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-localhost");
                    await writer.WriteLineAsync("250 8BITMIME");
                }
                else if (line.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250 OK");
                }
                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                    while (await reader.ReadLineAsync() is { } body)
                    {
                        text.AppendLine(body);
                        if (body == ".") break;
                    }
                    if (dataReply is not null) await writer.WriteLineAsync(dataReply);
                }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 Bye");
                    break;
                }
                else
                {
                    await writer.WriteLineAsync("250 OK");
                }
            }
            return text.ToString();
        }
        public async ValueTask DisposeAsync()
        {
            listener.Stop();
            try { await transcript.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is TimeoutException or SocketException or ObjectDisposedException) { }
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "/tmp/opencode";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

[Trait("Group", "Membership")]
public sealed class MailTests : MembershipTest
{
    [Fact]
    public async Task Welcome_and_restoration_survive_a_multi_day_worker_outage()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        await using var db = Context();
        var welcome = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.Welcome).Select(x => x.Id).SingleAsync();
        Clock.Now = Clock.Now.AddDays(3);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(welcome));
        var link = Link(Assert.Single(transport.Sent));
        await Run(app, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, link["token"]!, Password));
        Assert.Equal(DeliveryState.Accepted, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == welcome)).State);

        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Manual termination"));
        var restoration = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, restoration, "Verified request"));
        Clock.Now = Clock.Now.AddDays(3);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(restoration));
        Assert.Equal(2, transport.Sent.Count);
        Assert.Contains("peruttu", transport.Sent[^1].Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DeliveryState.Accepted, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == restoration)).State);
    }

    [Fact]
    public async Task Pending_deletion_is_rechecked_at_dispatch_and_stale_started_attempts_require_review()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        await using var db = Context();
        var welcome = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.Welcome).Select(x => x.Id).SingleAsync();
        await db.Deliveries.Where(x => x.Id == welcome).ExecuteUpdateAsync(x => x.SetProperty(d => d.State, DeliveryState.Sending).SetProperty(d => d.StartedAt, Clock.Now));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(welcome));
        Assert.Empty(transport.Sent);
        Assert.Equal(DeliveryState.Review, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == welcome)).State);
        var deletion = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, deletion, "Manual termination"));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, Guid.NewGuid(), "Verified request"));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(deletion));
        Assert.Empty(transport.Sent); // Restoration cancelled the obsolete warning.
        Assert.Equal(DeliveryState.Cancelled, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == deletion)).State);
    }

    [Fact]
    public async Task Provider_throttling_has_three_total_attempts_and_global_rate_limiting_defers_without_sending()
    {
        Settings["Mail:MaxPerMinute"] = "2";
        var board = await Board();
        var transport = new RecordingMail { Outcome = SendOutcome.Throttled };
        await using var app = CommandsApp(transport);
        var id = await Verified(app);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, id, null));
        for (var attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<JobExecutionException>(() => Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(id)));
        await Assert.ThrowsAsync<JobExecutionException>(() => Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(id)));
        Assert.Equal(2, transport.Sent.Count); // Retrying the SAME recipient still consumes provider rate capacity.
        Clock.Now = Clock.Now.AddMinutes(1);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(id));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(id));
        Assert.Equal(3, transport.Sent.Count);
        await using var db = Context();
        Assert.Equal(DeliveryState.Failed, (await db.Deliveries.SingleAsync(x => x.Id == id)).State);
        Settings["Mail:MaxPerMinute"] = "1";
        await using var limited = CommandsApp(transport);
        var pending = await Verified(limited, "second@example.test");
        await Run(limited, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, pending, null));
        await Assert.ThrowsAsync<JobExecutionException>(() => Run(limited, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(pending)));
        Assert.Equal(3, transport.Sent.Count);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task A_real_worker_delivers_verification_welcome_rejection_and_warning_from_persisted_identifiers()
    {
        var board = await Board();
        Settings["Mail:SpoolPath"] = Path.Combine(DirectoryPath, "mail");
        await using var app = CommandsApp();
        var application = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(application, Input()));
        var queuedKey = Assert.Single(await Scheduler.GetJobKeys(GroupMatcher<JobKey>.GroupEquals("work")));
        var queued = (await Scheduler.GetJobDetail(queuedKey))!;
        Assert.Equal(new[] { "delivery_id", "enqueued_at" }, queued.JobDataMap.Keys.Order());
        Assert.All(queued.JobDataMap.Values, value => Assert.DoesNotContain(Email, Assert.IsType<string>(value)));
        StartWorker();
        await using var db = Context();
        await Until(async () => await db.Deliveries.AnyAsync(x => x.ApplicationId == application && x.State == DeliveryState.Accepted));
        var verification = await db.Deliveries.AsNoTracking().SingleAsync(x => x.ApplicationId == application);
        var mail = JsonSerializer.Deserialize<OutgoingMail>(await File.ReadAllTextAsync(Path.Combine(Settings["Mail:SpoolPath"]!, verification.Id.ToString("N") + ".json")))!;
        var link = Link(mail);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Verify(application, link["token"]!));
        var id = await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, application));
        await Until(async () => await db.Deliveries.AnyAsync(x => x.Kind == MailKind.Welcome && x.State == DeliveryState.Accepted));
        var reject = await Verified(app, "reject@example.test");
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, reject, null));
        await Until(async () => !await db.Applications.AnyAsync(x => x.Id == reject));
        var warning = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, warning, "Manual termination"));
        await Until(async () => await db.Deliveries.AnyAsync(x => x.Id == warning && x.State == DeliveryState.Accepted));
        foreach (var kind in new[] { MailKind.Verification, MailKind.Welcome, MailKind.Rejection, MailKind.DeletionWarning })
            Assert.True(await db.Deliveries.AnyAsync(x => x.Kind == kind && x.State == DeliveryState.Accepted));
    }
}
