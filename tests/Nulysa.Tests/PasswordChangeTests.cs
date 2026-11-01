using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Communications;
using Nulysa.Data;

namespace Nulysa.Tests;

public sealed class PasswordChangeTests : BoardTest
{
    private const string ChangedPassword = "Changed-password-456!";

    [Fact]
    public async Task Password_change_checks_the_current_password_and_queues_one_required_notice_without_secrets()
    {
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        var token = await PasswordToken(app, id);
        await Run(app, services => services.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        var operation = Guid.NewGuid();
        await using var db = Context();
        var originalHash = (await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PasswordHash;

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), "wrong", ChangedPassword, operation)));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), Password, "weak", operation)));
        Assert.Equal(originalHash, (await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PasswordHash);
        Assert.False(await db.Deliveries.AnyAsync(delivery => delivery.Id == operation));
        await Run(app, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), Password, ChangedPassword, operation));
        await Run(app, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), Password, ChangedPassword, operation));
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), ChangedPassword, "Different-password-789!", operation)));
        var delivery = await db.Deliveries.SingleAsync(delivery => delivery.Id == operation);
        Assert.Equal(MailKind.PasswordChanged, delivery.Kind);
        Assert.Equal(id, delivery.UserId);
        await Run(app, services => services.GetRequiredService<MailDispatcher>().Dispatch(operation));
        var notice = Assert.Single(transport.Sent);
        Assert.Equal(Email, notice.To);
        Assert.Contains("Salasanasi on vaihdettu", notice.Subject);
        Assert.DoesNotContain(Password, notice.Text);
        Assert.DoesNotContain(ChangedPassword, notice.Text);
        Assert.DoesNotContain("token=", notice.Text);
        Assert.NotEqual(originalHash, (await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PasswordHash);
        await Run(app, services => services.GetRequiredService<Membership.MembershipCommands>().BeginDeletion(board, id, Guid.NewGuid(), "Test deletion"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), ChangedPassword, Password, Guid.NewGuid())));
    }

    [Fact]
    public async Task Failed_notification_persistence_rolls_back_the_password_change()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var token = await PasswordToken(app, id);
        await Run(app, services => services.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        await using var db = Context();
        var before = (await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PasswordHash;
        await using var failing = Build("commands", builder => builder.Services.AddDbContext<AppDbContext>(options => options.AddInterceptors(new RejectPasswordNotice())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(failing, services => services.GetRequiredService<AccountCommands>().ChangePassword(Actor(id), Password, ChangedPassword, Guid.NewGuid())));

        Assert.Equal(before, (await db.Users.AsNoTracking().SingleAsync(user => user.Id == id)).PasswordHash);
        Assert.False(await db.Deliveries.AnyAsync(delivery => delivery.Kind == MailKind.PasswordChanged));
    }

    [Fact]
    public async Task Password_change_http_form_validates_confirmation_preserves_sign_in_and_changes_login_credentials()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var token = await PasswordToken(app, id);
        await Run(app, services => services.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("Account/ChangePassword", new FormUrlEncodedContent([]))).StatusCode);
        var html = await client.GetStringAsync("profile");
        var input = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["Operation"] = Guid.NewGuid().ToString(),
            ["CurrentPassword"] = Password,
            ["NewPassword"] = ChangedPassword,
            ["ConfirmPassword"] = "different"
        };
        await client.PostAsync("Account/ChangePassword", new FormUrlEncodedContent(input));
        var failed = await client.GetStringAsync("profile");
        Assert.Contains("The new passwords do not match", failed);
        Assert.DoesNotContain(ChangedPassword, failed);
        input["ConfirmPassword"] = ChangedPassword;
        var changed = await client.PostAsync("Account/ChangePassword", new FormUrlEncodedContent(input));

        Assert.Equal(HttpStatusCode.Redirect, changed.StatusCode);
        Assert.Equal("/profile#account", changed.Headers.Location!.ToString());
        var profile = await client.GetStringAsync("profile");
        Assert.Contains("Password changed. A notification email has been queued", profile);
        Assert.DoesNotContain(ChangedPassword, profile);
        await using var db = Context();
        Assert.Single(await db.Deliveries.Where(delivery => delivery.Kind == MailKind.PasswordChanged).ToListAsync());
        var user = await db.Users.SingleAsync(user => user.Id == id);
        var hasher = new PasswordHasher<User>();
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, user.PasswordHash!, Password));
        Assert.NotEqual(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, user.PasswordHash!, ChangedPassword));
    }

    private sealed class RejectPasswordNotice : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Delivery>().Any(entry => entry.State == EntityState.Added && entry.Entity.Kind == MailKind.PasswordChanged))
                throw new InvalidOperationException("Synthetic notification failure");
            return ValueTask.FromResult(result);
        }
    }
}
