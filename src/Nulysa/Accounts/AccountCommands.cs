using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;
using Quartz;

namespace Nulysa.Accounts;

public sealed class AccountCommands(AppDbContext db, UserManager<User> users, MembershipCommands membership,
    MailQueue mail, ISchedulerFactory schedulers, AssociationClock clock)
{
    public async Task UpdateProfile(ClaimsPrincipal actor, long id, ProfileInput input, CancellationToken ct = default)
    {
        input.Validate(clock.Today);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await membership.LockUser(id, ct);
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        if (!user.MayLogIn)
            throw new ValidationException("This account is unavailable.");

        input.CopyTo(user);
        MembershipCommands.Check(await users.UpdateAsync(user));
        await tx.CommitAsync(ct);
    }

    public async Task RequestEmailChange(ClaimsPrincipal actor, long id, string email, Guid operation, CancellationToken ct = default)
    {
        email = email.Trim();
        if (email.Length > 256 || !new EmailAddressAttribute().IsValid(email) || operation == Guid.Empty)
            throw new ValidationException("Enter a valid email address.");

        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var user = await membership.LockUser(id, ct);
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        if (!user.MayLogIn)
            throw new ValidationException("This account is unavailable.");

        var prior = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operation, ct);
        if (prior is not null)
        {
            if (prior.UserId != id || prior.Kind != MailKind.EmailChange || user.PendingEmail != email)
                throw new ValidationException("This request key was already used.");
            return;
        }
        if (user.PendingEmailRequestedAt?.AddMinutes(1) > clock.Now)
            throw new ValidationException("Wait one minute before requesting another email change.");
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == users.NormalizeEmail(email) && x.Id != id, ct))
            throw new ValidationException("That email address is unavailable.");

        user.PendingEmail = email;
        user.PendingEmailVersion = operation;
        user.PendingEmailRequestedAt = clock.Now;
        await mail.Add(scheduler, operation, MailKind.EmailChange, id, version: operation, ct: ct);
        await tx.CommitAsync(ct);
    }

    public async Task ConfirmEmailChange(long id, Guid version, string token, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await membership.LockUser(id, ct);
        var isValidRequest = user.MayLogIn
            && user.PendingEmail is not null
            && user.PendingEmailVersion == version
            && user.PendingEmailRequestedAt?.AddDays(1) > clock.Now;
        if (!isValidRequest)
            throw new ValidationException("Invalid or expired email change.");

        // Identity's normalizer and the database unique index also arbitrate simultaneous confirmations.
        MembershipCommands.Check(await users.ChangeEmailAsync(user, user.PendingEmail!, token));
        user.PendingEmail = null;
        user.PendingEmailVersion = null;
        user.PendingEmailRequestedAt = null;
        MembershipCommands.Check(await users.UpdateAsync(user));
        await tx.CommitAsync(ct);
    }

    public async Task RequestPasswordReset(string email, CancellationToken ct = default)
    {
        var id = await db.Users.Where(x => x.NormalizedEmail == users.NormalizeEmail(email.Trim()) && x.Admitted && x.PendingDeletionAt == null)
            .Select(x => (long?)x.Id).SingleOrDefaultAsync(ct);
        if (id is null)
            return;

        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var user = await membership.LockUser(id.Value, ct);
        var requestedRecently = await db.Deliveries.AnyAsync(x => x.UserId == id
            && (x.Kind == MailKind.PasswordReset || x.Kind == MailKind.Welcome)
            && x.CreatedAt > clock.Now.AddMinutes(-1), ct);
        if (!user.MayLogIn || requestedRecently)
            return;

        await mail.Add(scheduler, Guid.NewGuid(), MailKind.PasswordReset, user.Id, ct: ct);
        await tx.CommitAsync(ct);
    }

    public async Task SetPassword(long id, string token, string password, CancellationToken ct = default)
    {
        if (password.Length > 256)
            throw new ValidationException("Password must not exceed 256 characters.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await membership.LockUser(id, ct);
        if (!user.MayLogIn || !user.EmailConfirmed)
            throw new ValidationException("This account is unavailable.");

        MembershipCommands.Check(await users.ResetPasswordAsync(user, token, password));
        await tx.CommitAsync(ct);
    }
}
