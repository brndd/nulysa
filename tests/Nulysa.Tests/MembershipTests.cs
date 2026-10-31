using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;
using Nulysa.Operations;
using Nulysa.WorkerHarness;
using Quartz;

namespace Nulysa.Tests;

public abstract class MembershipTest : OidcTest
{
    protected WebApplication CommandsApp(IMailTransport? transport = null) => Build("commands", builder =>
    {
        builder.Services.AddSingleton<TimeProvider>(Clock);
        if (transport is not null)
            builder.Services.AddSingleton(transport);
    });

    protected static ClaimsPrincipal Actor(long id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test"));

    protected async Task<ClaimsPrincipal> Board()
    {
        await using var scope = Maintenance.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { Email = "board@example.test", UserName = "board@example.test", EmailConfirmed = true, Admitted = true, Name = "Board" };
        MembershipCommands.Check(await users.CreateAsync(user, Password));
        await Superusers.Set(scope.ServiceProvider.GetRequiredService<AppDbContext>(), users,
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>(), user.Id, true, "Administrator");
        return Actor(user.Id);
    }

    protected static async Task Run(WebApplication app, Func<IServiceProvider, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    protected static async Task<T> Run<T>(WebApplication app, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    protected static ApplicationInput Input(string email = Email) => new()
    {
        Email = email,
        Message = "I want to join.",
        AcceptTerms = true,
        Profile = new() { Name = "New Member", Municipality = "Helsinki", DateOfBirth = new DateOnly(2010, 6, 12), Language = "fi", Nickname = "New", MatrixId = "@new:example.test" }
    };

    protected async Task<Guid> Verified(WebApplication app, string email = Email)
    {
        var id = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, Input(email)));
        var token = await Run(app, async sp => sp.GetRequiredService<MembershipCommands>().VerificationToken(await sp.GetRequiredService<AppDbContext>().Applications.SingleAsync(x => x.Id == id)));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Verify(id, token));
        return id;
    }

    protected async Task<long> Admit(WebApplication app, ClaimsPrincipal board, string email = Email)
    {
        var application = await Verified(app, email);
        return await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, application));
    }

    protected static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Link(OutgoingMail mail)
        => QueryHelpers.ParseQuery(new Uri(Regex.Match(mail.Text, @"https?://\S+").Value).Query);

    protected async Task<string> PasswordToken(WebApplication app, long id)
        => await Run(app, async sp => await sp.GetRequiredService<UserManager<User>>().GeneratePasswordResetTokenAsync((await sp.GetRequiredService<UserManager<User>>().FindByIdAsync(id.ToString()))!));
}

public sealed class RecordingMail : IMailTransport
{
    public List<OutgoingMail> Sent { get; } = [];
    public SendOutcome Outcome { get; set; } = SendOutcome.Accepted;
    public Task<SendResult> Send(Guid id, OutgoingMail mail, CancellationToken ct)
    {
        Sent.Add(mail);
        return Task.FromResult(new SendResult(Outcome, Outcome == SendOutcome.Accepted ? "synthetic-provider-id" : null));
    }
}

[Trait("Group", "Membership")]
public sealed class MembershipTests : MembershipTest
{
    [Fact]
    public async Task Expired_email_cleanup_processes_oldest_requests_first_with_ID_ties_and_a_50_record_limit()
    {
        await using var app = CommandsApp();
        await using var db = Context();
        var users = Enumerable.Range(0, 53).Select(i => new User
        {
            UserName = $"pending-{i}",
            NormalizedUserName = $"PENDING-{i}",
            PendingEmail = $"pending-{i}@example.test",
            PendingEmailVersion = Guid.NewGuid(),
            PendingEmailRequestedAt = i == 52 ? Clock.Now.AddHours(-23) : Clock.Now.AddDays(i == 0 ? -2 : -3)
        }).ToList();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        var expected = users.Where(x => x.PendingEmailRequestedAt <= Clock.Now.AddDays(-1))
            .OrderBy(x => x.PendingEmailRequestedAt).ThenBy(x => x.Id).Take(50).Select(x => x.Id).ToHashSet();

        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());

        var after = await db.Users.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(50, after.Count(x => x.PendingEmailRequestedAt is null));
        foreach (var user in after)
        {
            if (expected.Contains(user.Id))
            {
                Assert.Null(user.PendingEmail);
                Assert.Null(user.PendingEmailVersion);
                Assert.Null(user.PendingEmailRequestedAt);
            }
            else
            {
                var original = users.Single(x => x.Id == user.Id);
                Assert.Equal(original.PendingEmail, user.PendingEmail);
                Assert.Equal(original.PendingEmailVersion, user.PendingEmailVersion);
                Assert.Equal(original.PendingEmailRequestedAt, user.PendingEmailRequestedAt);
            }
        }
        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
        Assert.Equal(1, await db.Users.CountAsync(x => x.PendingEmailRequestedAt != null));
        Assert.Equal(users[52].Id, await db.Users.Where(x => x.PendingEmailRequestedAt != null).Select(x => x.Id).SingleAsync());
    }

    [Fact]
    public async Task Verification_and_terms_gate_review_and_login_and_resend_does_not_extend_retention()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = Guid.NewGuid();
        var input = Input();
        input.AcceptTerms = false;
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, input)));
        input.AcceptTerms = true;
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, input));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(id, input));
        await using var db = Context();
        Assert.Equal(1, await db.Applications.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync()); // Board only; applicants are not auth users.
        Assert.Empty(await Run(app, sp => sp.GetRequiredService<MembershipQueries>().Applications(board)));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, id)));
        var old = await Run(app, async sp => sp.GetRequiredService<MembershipCommands>().VerificationToken(await sp.GetRequiredService<AppDbContext>().Applications.SingleAsync()));
        Clock.Now = Clock.Now.AddHours(23);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().ResendVerification(Email.ToUpperInvariant()));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Verify(id, old)));
        var current = await Run(app, async sp => sp.GetRequiredService<MembershipCommands>().VerificationToken(await sp.GetRequiredService<AppDbContext>().Applications.SingleAsync()));
        Clock.Now = Clock.Now.AddHours(1);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Verify(id, current)));
        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
        Assert.Empty(await db.Applications.ToListAsync());
        var verified = await Verified(app);
        Assert.Equal(verified, Assert.Single(await Run(app, sp => sp.GetRequiredService<MembershipQueries>().Applications(board))).Id);
        Assert.Equal(verified, Assert.Single(await Run(app, sp => sp.GetRequiredService<MembershipQueries>().Applications(board, verified))).Id);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Concurrent_approval_creates_one_identity_anchor_account_request_welcome_and_ID_only_audit()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var application = await Verified(app);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, application))));
        Assert.Equal(results[0], results[1]);
        await using var db = Context();
        var user = await db.Users.SingleAsync(x => x.Id == results[0]);
        Assert.True(user.MayLogIn);
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
        Assert.Empty(await db.Applications.ToListAsync());
        Assert.Single(await db.Members.ToListAsync());
        var account = Assert.Single(await db.BillingAccounts.ToListAsync());
        Assert.Equal(user.Id, account.UserId);
        var request = Assert.Single(await db.InitialMembershipRequests.ToListAsync());
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Clock.Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki")).DateTime), request.Anchor);
        Assert.Null(request.CancelledAt);
        var approval = Assert.Single(await db.Approvals.ToListAsync());
        Assert.Equal(Authority.Id(board), approval.AdministratorId);
        Assert.Equal(user.Id, approval.UserId);
        Assert.Equal(Clock.Now, approval.ApprovedAt);
        Assert.Single(await db.Deliveries.Where(x => x.Kind == MailKind.Welcome).ToListAsync());
    }

    [Fact]
    public async Task Approval_crossing_local_midnight_keeps_one_joining_and_billing_date()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var application = await Verified(app);
        var approvedAt = Clock.Now = new DateTimeOffset(2026, 1, 15, 21, 59, 59, TimeSpan.Zero);
        await using var crossingMidnight = Build("commands", builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new MidnightAfterMembershipSave(Clock)));
        });

        var id = await Run(crossingMidnight, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, application));

        await using var db = Context();
        var member = await db.Members.SingleAsync(x => x.UserId == id);
        var approval = await db.Approvals.SingleAsync(x => x.UserId == id);
        var account = await db.BillingAccounts.SingleAsync(x => x.UserId == id);
        var request = await db.InitialMembershipRequests.SingleAsync(x => x.BillingAccountId == account.Id);
        Assert.Equal(new DateOnly(2026, 1, 15), member.JoinedOn);
        Assert.Equal(member.JoinedOn, request.Anchor);
        Assert.Equal(approvedAt, approval.ApprovedAt);
        Assert.Equal(approvedAt, account.CreatedAt);
        Assert.Equal(approvedAt, request.RequestedAt);
    }

    private sealed class MidnightAfterMembershipSave(ManualClock clock) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Member>().Any())
                clock.Now = new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task A_failed_welcome_scheduling_write_rolls_back_the_entire_admission()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Verified(app);
        await Scheduler.ScheduleJob(JobBuilder.Create<ProbeJob>().WithIdentity(MailQueue.JobKey(id)).Build(),
            TriggerBuilder.Create<ProbeJob>().StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build());
        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, id)));
        await using var db = Context();
        Assert.Single(await db.Applications.ToListAsync());
        Assert.Empty(await db.Members.ToListAsync());
        Assert.Empty(await db.BillingAccounts.ToListAsync());
        Assert.Empty(await db.Approvals.ToListAsync());
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.False(await db.Deliveries.AnyAsync(x => x.Kind == MailKind.Welcome));
    }

    [Fact]
    public async Task Deletion_scheduling_failure_rolls_back_gates_stamps_billing_cancellation_and_audit()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        await using var db = Context();
        var before = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == id)).SecurityStamp;
        var operation = Guid.NewGuid();
        await Scheduler.ScheduleJob(JobBuilder.Create<ProbeJob>().WithIdentity(MailQueue.JobKey(operation)).Build(),
            TriggerBuilder.Create<ProbeJob>().StartAt(DateTimeOffset.UtcNow.AddDays(1)).Build());
        await Assert.ThrowsAsync<ObjectAlreadyExistsException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, operation, "Manual termination")));
        var after = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.True(after.MayLogIn);
        Assert.Equal(before, after.SecurityStamp);
        Assert.Null((await db.Members.SingleAsync()).DeletionEligibleAt);
        Assert.Null((await db.InitialMembershipRequests.SingleAsync()).CancelledAt);
        Assert.Empty(await db.LifecycleActions.ToListAsync());
        Assert.False(await db.Deliveries.AnyAsync(x => x.Kind == MailKind.DeletionWarning));
    }

    [Fact]
    public async Task Concurrent_restoration_and_erasure_leave_either_a_live_restored_member_or_a_fully_erased_identity()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Manual termination"));
        Clock.Now = Clock.Now.AddDays(14);
        var restore = Task.Run(async () =>
        {
            try
            { await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, Guid.NewGuid(), "Verified request")); return true; }
            catch (ValidationException) { return false; }
        });
        var erase = Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(id));
        await Task.WhenAll(restore, erase);
        Assert.NotEqual(await restore, await erase);
        await using var db = Context();
        Assert.Equal(await restore, await db.Users.AnyAsync(x => x.Id == id && x.PendingDeletionAt == null));
        Assert.Equal(await restore, await db.Members.AnyAsync(x => x.UserId == id));
        Assert.Equal(await restore, await db.BillingAccounts.AnyAsync(x => x.UserId == id));
        Assert.Single(await db.Approvals.ToListAsync());
    }

    [Fact]
    public async Task Database_constraints_guard_decisions_accounts_and_financial_history_independently_of_commands()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().LockUser(id, CancellationToken.None)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(app, sp => sp.GetRequiredService<MailQueue>().Add(Scheduler, Guid.NewGuid(), MailKind.Welcome, id)));
        await using (var db = Context())
        {
            db.Applications.Add(new() { Id = Guid.NewGuid(), NormalizedEmail = "INVALID@EXAMPLE.TEST", RejectedAt = Clock.Now });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = Context())
        {
            db.BillingAccounts.Add(new() { UserId = id, CreatedAt = Clock.Now });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = Context())
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.BillingAccounts.ExecuteDeleteAsync());
    }

    [Theory]
    [InlineData(SendOutcome.Accepted, DeliveryState.Accepted)]
    [InlineData(SendOutcome.Rejected, DeliveryState.Failed)]
    [InlineData(SendOutcome.Uncertain, DeliveryState.Review)]
    public async Task Rejection_blocks_approval_and_erases_after_acceptance_or_a_bounded_failure_deadline(SendOutcome outcome, DeliveryState state)
    {
        var board = await Board();
        var transport = new RecordingMail { Outcome = outcome };
        await using var app = CommandsApp(transport);
        var id = await Verified(app);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, id, "Optional explanation"));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, id, "Different explanation")));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, id)));
        Assert.Empty(await Run(app, sp => sp.GetRequiredService<MembershipQueries>().Applications(board)));
        await using var db = Context();
        Assert.True(await db.Applications.AnyAsync(x => x.Id == id)); // Enqueued is not delivered.
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(id));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(id));
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, id, "Optional explanation"));
        Assert.Single(transport.Sent);
        Assert.Contains("Optional explanation", transport.Sent[0].Text);
        Assert.Equal(state, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == id)).State);
        Assert.Equal(outcome != SendOutcome.Accepted, await db.Applications.AnyAsync(x => x.Id == id));
        if (outcome != SendOutcome.Accepted)
        {
            Assert.Contains("mail_requires_review", (await WorkStatus.Read(db, Scheduler, Clock)).Issues);
            Clock.Now = Clock.Now.AddDays(7);
            await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
            Assert.Empty(await db.Applications.ToListAsync());
            Assert.Null((await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == id)).ApplicationId);
        }
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task Profile_and_verified_email_change_preserve_identity_and_check_live_uniqueness_and_ownership()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");
        await using var db = Context();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        var subject = user.OidcSubject;
        var originalUsername = user.UserName;
        var profile = Input().Profile;
        profile.Name = "Edited Name";
        profile.DateOfBirth = new DateOnly(1995, 2, 28);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<AccountCommands>().UpdateProfile(Actor(other), id, profile)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<MembershipQueries>().Registry(Actor(id), "")));
        await Run(app, sp => sp.GetRequiredService<AccountCommands>().UpdateProfile(Actor(id), id, profile));
        var operation = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<AccountCommands>().RequestEmailChange(Actor(id), id, "changed@example.test", operation));
        Assert.Equal(Email, (await db.Users.AsNoTracking().SingleAsync(x => x.Id == id)).Email);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(operation));
        var link = Link(Assert.Single(transport.Sent));
        Assert.Equal("changed@example.test", transport.Sent[0].To);
        // Another account takes the address after the verification request, but before confirmation.
        await db.Users.Where(x => x.Id == other).ExecuteUpdateAsync(x => x.SetProperty(u => u.Email, "changed@example.test").SetProperty(u => u.NormalizedEmail, "CHANGED@EXAMPLE.TEST"));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<AccountCommands>().ConfirmEmailChange(id, operation, link["token"]!)));
        await db.Users.Where(x => x.Id == other).ExecuteUpdateAsync(x => x.SetProperty(u => u.Email, "other@example.test").SetProperty(u => u.NormalizedEmail, "OTHER@EXAMPLE.TEST"));
        await Run(app, sp => sp.GetRequiredService<AccountCommands>().ConfirmEmailChange(id, operation, link["token"]!));
        var changed = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(subject, changed.OidcSubject);
        Assert.Equal("changed@example.test", changed.Email);
        Assert.Equal(originalUsername, changed.UserName);
        Assert.Equal("Edited Name", changed.Name);
        Assert.Null(changed.PendingEmail);
    }

    [Fact]
    public async Task Approved_account_without_password_can_request_a_fresh_setup_link_after_one_minute()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        await using var db = Context();
        await Run(app, sp => sp.GetRequiredService<AccountCommands>().RequestPasswordReset(Email));
        Assert.False(await db.Deliveries.AnyAsync(x => x.UserId == id && x.Kind == MailKind.PasswordReset));
        Clock.Now = Clock.Now.AddMinutes(1).AddSeconds(1);
        await Run(app, sp => sp.GetRequiredService<AccountCommands>().RequestPasswordReset(Email));
        var reset = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.PasswordReset).Select(x => x.Id).SingleAsync();
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(reset));
        var link = Link(Assert.Single(transport.Sent));
        await Run(app, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, link["token"]!, Password));
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync(id.ToString()))!, Password));
    }

    [Fact]
    public async Task Real_resignation_blocks_existing_cookies_codes_refresh_userinfo_and_password_reset_until_admin_restoration()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var commands = CommandsApp(transport);
        var id = await Admit(commands, board);
        var setup = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, setup, Password));
        await Assert.ThrowsAsync<ValidationException>(() => Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, setup, Password)));
        await CreateClient();
        await using var web = await StartProvider();
        using var client = Client();
        var tokens = await Tokens(client);
        var code = await Code(client);
        var reset = await PasswordToken(commands, id);
        var operation = Guid.NewGuid();
        await Run(commands, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(Actor(id), id, operation, "Voluntary resignation"));
        Assert.Equal("/Account/Login", (await client.GetAsync("/")).Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, (await Login(client)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Redeem(client, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(client, tokens)).StatusCode);
        using var info = new HttpRequestMessage(HttpMethod.Get, "connect/userinfo");
        info.Headers.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await client.SendAsync(info)).StatusCode);
        await Assert.ThrowsAsync<ValidationException>(() => Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, reset, Password)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(commands, sp => sp.GetRequiredService<MembershipCommands>().Restore(Actor(id), id, Guid.NewGuid(), "Self restore")));
        // Restoration remains possible after eligibility, until actual erasure.
        Clock.Now = Clock.Now.AddDays(15);
        await Run(commands, sp => sp.GetRequiredService<MembershipCommands>().Restore(board, id, Guid.NewGuid(), "Verified member request"));
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("profile")).StatusCode);
        await using var db = Context();
        Assert.NotNull((await db.InitialMembershipRequests.SingleAsync()).CancelledAt);
        Assert.False(await db.Deliveries.AnyAsync(x => x.Kind == MailKind.DeletionWarning && x.State == DeliveryState.Pending));
        var restoration = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.Restoration).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(restoration));
        Assert.Contains("peruttu", transport.Sent[^1].Subject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.LifecycleActions.CountAsync());
    }

    [Fact]
    public async Task Final_erasure_obeys_local_calendar_boundary_and_rejoining_never_relinks_retained_facts()
    {
        Clock.Now = new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero); // Helsinki 25 Oct, before DST fall-back.
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var subject = await Run(app, async sp => (await sp.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Id == id)).OidcSubject);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Manual termination"));
        Clock.Now = new DateTimeOffset(2026, 11, 7, 21, 59, 59, TimeSpan.Zero); // Helsinki 7 Nov, 23:59:59.
        Assert.False(await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(id)));
        Clock.Now = Clock.Now.AddSeconds(1); // Helsinki 8 Nov, midnight: still before the original marking wall time.
        Assert.False(await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(id)));
        Clock.Now = Clock.Now.AddMinutes(90); // Helsinki 8 Nov, 01:30: exactly 14 calendar days, spanning DST.
        await Run(app, async sp => await ActivatorUtilities.CreateInstance<FinalDeletionJob>(sp).Execute(null!));
        await using var db = Context();
        Assert.False(await db.Users.AnyAsync(x => x.Id == id));
        Assert.Empty(await db.Members.ToListAsync());
        Assert.Null((await db.BillingAccounts.SingleAsync()).UserId);
        Assert.Single(await db.Approvals.ToListAsync());
        Assert.False(await db.Deliveries.AnyAsync(x => x.UserId == id));
        var rejoined = await Admit(app, board);
        Assert.True(rejoined > id);
        Assert.NotEqual(subject, (await db.Users.AsNoTracking().SingleAsync(x => x.Id == rejoined)).OidcSubject);
        Assert.Equal(2, await db.BillingAccounts.CountAsync());
        Assert.Equal(2, await db.Approvals.CountAsync());
        Assert.Equal(1, await db.BillingAccounts.CountAsync(x => x.UserId == null));
    }

    [Fact]
    public async Task Live_board_authority_is_required_by_queries_and_commands_even_with_a_stale_cookie_role()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Verified(app);
        await using (var db = Context())
            await db.UserRoles.Where(x => x.UserId == Authority.Id(board)).ExecuteDeleteAsync();
        ((ClaimsIdentity)board.Identity!).AddClaim(new Claim(ClaimTypes.Role, "Administrator"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<MembershipQueries>().Applications(board)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, id)));
    }

    [Fact]
    public async Task Reusing_an_application_key_after_admission_or_rejection_is_rejected()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var admitted = await Verified(app);
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Approve(board, admitted));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(admitted, Input("other@example.test"))));

        var rejected = await Verified(app, "rejected@example.test");
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Reject(board, rejected, "Not this time"));
        Clock.Now = Clock.Now.AddDays(7);
        await Run(app, sp => sp.GetRequiredService<MembershipMaintenance>().Scan());
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<MembershipCommands>().Apply(rejected, Input("fresh@example.test"))));
    }

    [Fact]
    public async Task Admitted_username_is_a_guid_and_cannot_be_changed()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        await using var db = Context();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Matches("^[0-9a-f]{32}$", user.UserName!);
        await Assert.ThrowsAsync<DbUpdateException>(() => Run(app, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<User>>();
            var tracked = await users.FindByIdAsync(id.ToString());
            MembershipCommands.Check(await users.SetUserNameAsync(tracked!, "different-name"));
        }));
        Assert.Equal(user.UserName, (await db.Users.AsNoTracking().SingleAsync(x => x.Id == id)).UserName);
    }

    [Fact]
    public async Task Erasure_deletes_tokens_and_authorizations_for_the_subject()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var subject = await Run(app, async sp => (await sp.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Id == id)).OidcSubject);
        await using (var db = Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "OpenIddictTokens" ("Id", "ConcurrencyToken", "Subject", "Type", "Status")
                VALUES ({Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()}, {subject}, 'access_token', 'valid');
                INSERT INTO "OpenIddictAuthorizations" ("Id", "ConcurrencyToken", "Subject", "Type", "Status")
                VALUES ({Guid.NewGuid().ToString()}, {Guid.NewGuid().ToString()}, {subject}, 'permanent', 'valid');
                """);
        }
        await Run(app, sp => sp.GetRequiredService<MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Manual termination"));
        Clock.Now = Clock.Now.AddDays(14);
        Assert.True(await Run(app, sp => sp.GetRequiredService<MembershipCommands>().Erase(id)));
        await using var check = Context();
        Assert.Equal(0, await check.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM \"OpenIddictTokens\" WHERE \"Subject\" = {subject}").SingleAsync());
        Assert.Equal(0, await check.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM \"OpenIddictAuthorizations\" WHERE \"Subject\" = {subject}").SingleAsync());
    }
}
