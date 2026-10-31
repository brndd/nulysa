using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using OpenIddict.Abstractions;
using Quartz;

namespace Nulysa.Membership;

public sealed class MembershipCommands(AppDbContext db, UserManager<User> users, ISchedulerFactory schedulers, MailQueue mail,
    AssociationClock clock, IOptions<MembershipOptions> options, IDataProtectionProvider protection,
    IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, BillingCommands billing)
{
    public string VerificationToken(MembershipApplication application)
        => protection.CreateProtector("membership-verification-v1").Protect(application.Id + ":" + application.VerificationVersion);

    public async Task Apply(Guid id, ApplicationInput input, CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new ValidationException("Invalid application key.");
        Validator.ValidateObject(input, new ValidationContext(input), true);
        input.Profile.Validate(clock.Today);
        var email = input.Email.Trim();
        var normalized = users.NormalizeEmail(email);
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalized}, 0))", ct);
        var existing = await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (existing is not null)
        {
            if (existing.Email != email || existing.Name != input.Profile.Name.Trim() || existing.Message != input.Message.Trim() ||
                existing.Municipality != input.Profile.Municipality.Trim() || existing.DateOfBirth != input.Profile.DateOfBirth ||
                existing.Language != input.Profile.Language || existing.Nickname != input.Profile.Nickname?.Trim() ||
                existing.PhoneNumber != input.Profile.PhoneNumber?.Trim() || existing.MatrixId != input.Profile.MatrixId?.Trim())
                throw new ValidationException("This request key was already used for different details.");
            return;
        }
        // Delivery and approval rows outlive the application, so a stale client key must not be reused.
        if (await db.Approvals.AnyAsync(x => x.Id == id, ct) || await db.Deliveries.AnyAsync(x => x.Id == id, ct))
            throw new ValidationException("This request key was already used.");
        // Same public outcome for a registered address: do not reveal the registry to anonymous callers.
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized, ct) || await db.Applications.AnyAsync(x => x.NormalizedEmail == normalized, ct)) return;
        var application = new MembershipApplication
        {
            Id = id,
            Email = email,
            NormalizedEmail = normalized,
            Name = input.Profile.Name.Trim(),
            Municipality = input.Profile.Municipality.Trim(),
            DateOfBirth = input.Profile.DateOfBirth!.Value,
            Language = input.Profile.Language,
            PhoneNumber = input.Profile.PhoneNumber?.Trim(),
            Nickname = input.Profile.Nickname?.Trim(),
            MatrixId = input.Profile.MatrixId?.Trim(),
            Message = input.Message.Trim(),
            TermsAcceptedAt = clock.Now,
            CreatedAt = clock.Now,
            VerificationRequestedAt = clock.Now,
            VerificationVersion = Guid.NewGuid()
        };
        db.Applications.Add(application);
        await mail.Add(scheduler, Guid.NewGuid(), MailKind.Verification, applicationId: id, version: application.VerificationVersion, ct: ct);
        await tx.CommitAsync(ct);
    }

    public async Task ResendVerification(string email, CancellationToken ct = default)
    {
        var normalized = users.NormalizeEmail(email.Trim());
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM \"Applications\" WHERE \"NormalizedEmail\" = {normalized} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (application is null || application.VerifiedAt is not null || application.RejectedAt is not null ||
            application.CreatedAt.AddHours(options.Value.UnverifiedRetentionHours) <= clock.Now ||
            application.VerificationRequestedAt.AddMinutes(1) > clock.Now) return;
        application.VerificationVersion = Guid.NewGuid();
        application.VerificationRequestedAt = clock.Now;
        await mail.Add(scheduler, Guid.NewGuid(), MailKind.Verification, applicationId: application.Id, version: application.VerificationVersion, ct: ct);
        await tx.CommitAsync(ct);
    }

    public async Task Verify(Guid id, string token, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var application = await LockApplication(id, ct);
        string value;
        try { value = protection.CreateProtector("membership-verification-v1").Unprotect(token); }
        catch (System.Security.Cryptography.CryptographicException) { throw new ValidationException("Invalid or expired verification link."); }
        if (application is null || application.RejectedAt is not null || application.CreatedAt.AddHours(options.Value.UnverifiedRetentionHours) <= clock.Now ||
            value != application.Id + ":" + application.VerificationVersion)
            throw new ValidationException("Invalid or expired verification link.");
        application.VerifiedAt ??= clock.Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<long> Approve(ClaimsPrincipal actor, Guid applicationId, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var application = await LockApplication(applicationId, ct);
        await Authority.RequireAdministrator(db, actor, ct);
        var approval = await db.Approvals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct);
        if (approval is not null) return approval.UserId;
        if (application is not { VerifiedAt: not null, RejectedAt: null }) throw new ValidationException("Only a verified, undecided application can be approved.");
        // Apply checks user and application existence in separate snapshots; serialize on the email.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({application.NormalizedEmail}, 0))", ct);
        var user = new User
        {
            Email = application.Email,
            UserName = Guid.NewGuid().ToString("N"),
            EmailConfirmed = true,
            Admitted = true,
            Name = application.Name,
            Municipality = application.Municipality,
            DateOfBirth = application.DateOfBirth,
            Language = application.Language,
            PhoneNumber = application.PhoneNumber,
            Nickname = application.Nickname,
            MatrixId = application.MatrixId
        };
        Check(await users.CreateAsync(user));
        var approvedAt = clock.Now;
        var joinedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(approvedAt, clock.Zone).DateTime);
        db.Members.Add(new() { UserId = user.Id, JoinedOn = joinedOn });
        db.Approvals.Add(new() { Id = applicationId, UserId = user.Id, AdministratorId = Authority.Id(actor), ApprovedAt = approvedAt });
        var account = new BillingAccount { UserId = user.Id, CreatedAt = approvedAt };
        db.BillingAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        await billing.Initialize(account, joinedOn, approvedAt, scheduler, ct);
        db.Applications.Remove(application);
        await mail.Add(scheduler, applicationId, MailKind.Welcome, user.Id, ct: ct);
        await tx.CommitAsync(ct);
        return user.Id;
    }

    public async Task Reject(ClaimsPrincipal actor, Guid id, string? explanation, CancellationToken ct = default)
    {
        if (explanation?.Length > 2000) throw new ValidationException("Explanation is too long.");
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var application = await LockApplication(id, ct);
        await Authority.RequireAdministrator(db, actor, ct);
        if (application is null && await db.Deliveries.AnyAsync(x => x.Id == id && x.Kind == MailKind.Rejection, ct)) return;
        if (application?.RejectedAt is not null)
        {
            if (application.RejectionExplanation != explanation?.Trim()) throw new ValidationException("This rejection key was already used with another explanation.");
            return;
        }
        if (application is not { VerifiedAt: not null }) throw new ValidationException("Only a verified application can be rejected.");
        application.RejectedAt = clock.Now;
        application.RejectionExplanation = explanation?.Trim();
        await mail.Add(scheduler, id, MailKind.Rejection, applicationId: id, ct: ct);
        await tx.CommitAsync(ct);
    }

    public async Task BeginDeletion(ClaimsPrincipal actor, long id, Guid operation, string reason, CancellationToken ct = default)
    {
        ValidateOperation(operation, reason);
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var user = await LockUser(id, ct);
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        if (await Repeated(operation, actor, id, "begin-deletion", reason, ct)) return;
        if (user.PendingDeletionAt is not null) return;
        var member = await db.Members.SingleOrDefaultAsync(x => x.UserId == id, ct) ?? throw new ValidationException("This account has no membership record.");
        user.PendingDeletionAt = clock.Now;
        user.PendingEmail = null; user.PendingEmailVersion = null; user.PendingEmailRequestedAt = null;
        member.DeletionEligibleAt = clock.AfterCalendarDays(options.Value.DeletionWaitingDays);
        member.DeletionOperation = operation;
        Check(await users.UpdateSecurityStampAsync(user));
        await tokens.RevokeBySubjectAsync(user.OidcSubject, ct);
        await authorizations.RevokeBySubjectAsync(user.OidcSubject, ct);
        var accountId = await db.BillingAccounts.Where(x => x.UserId == id).Select(x => x.Id).SingleAsync(ct);
        await billing.CancelForDeletion(accountId, operation, Authority.Id(actor), reason, scheduler, ct);
        await db.InitialMembershipRequests.Where(x => x.BillingAccountId == accountId && x.CancelledAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.CancelledAt, clock.Now), ct);
        Record(operation, actor, id, "begin-deletion", reason);
        await mail.CancelForUser(scheduler, id, ct);
        await mail.Add(scheduler, operation, MailKind.DeletionWarning, id, version: operation, ct: ct);
        await tx.CommitAsync(ct);
    }

    public async Task Restore(ClaimsPrincipal actor, long id, Guid operation, string reason, CancellationToken ct = default)
    {
        ValidateOperation(operation, reason);
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var user = await LockUser(id, ct);
        await Authority.RequireAdministrator(db, actor, ct);
        if (await Repeated(operation, actor, id, "restore", reason, ct)) return;
        if (user.PendingDeletionAt is null) throw new ValidationException("This account is not pending deletion.");
        var member = await db.Members.SingleOrDefaultAsync(x => x.UserId == id, ct) ?? throw new ValidationException("This account has no membership record.");
        user.PendingDeletionAt = null;
        member.DeletionEligibleAt = null; member.DeletionOperation = null;
        Check(await users.UpdateSecurityStampAsync(user));
        var accountId = await db.BillingAccounts.Where(x => x.UserId == id).Select(x => x.Id).SingleAsync(ct);
        await billing.RestoreMembershipBilling(accountId, operation, Authority.Id(actor), reason, ct);
        Record(operation, actor, id, "restore", reason);
        await mail.CancelForUser(scheduler, id, ct);
        await mail.Add(scheduler, operation, MailKind.Restoration, id, version: operation, ct: ct);
        // No new grace, coverage, billing request or credentials are restored here.
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> Erase(long id, CancellationToken ct = default)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        if (!await db.Users.AnyAsync(x => x.Id == id, ct)) return false;
        var user = await LockUser(id, ct);
        var member = await db.Members.SingleOrDefaultAsync(x => x.UserId == id, ct);
        if (user.PendingDeletionAt is null || member?.DeletionEligibleAt is null || member.DeletionEligibleAt > clock.Now) return false;
        await mail.CancelForUser(scheduler, id, ct);
        db.Deliveries.RemoveRange(await db.Deliveries.Where(x => x.UserId == id).ToListAsync(ct));
        await foreach (var token in tokens.FindBySubjectAsync(user.OidcSubject, ct)) await tokens.DeleteAsync(token, ct);
        await foreach (var authorization in authorizations.FindBySubjectAsync(user.OidcSubject, ct)) await authorizations.DeleteAsync(authorization, ct);
        var accountId = await db.BillingAccounts.Where(x => x.UserId == id).Select(x => x.Id).SingleAsync(ct);
        await billing.ErasePersonalRecords(accountId, ct);
        Check(await users.DeleteAsync(user));
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<User> LockUser(long id, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Member locks require a command transaction.");
        // All member commands use billing account -> user, including erasure and future S2 financial commands.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"BillingAccounts\" WHERE \"UserId\" = {id} FOR UPDATE", ct);
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new ValidationException("Member no longer exists.");
        await db.Entry(user).ReloadAsync(ct);
        var trackedMember = db.ChangeTracker.Entries<Member>().SingleOrDefault(x => x.Entity.UserId == id);
        if (trackedMember is not null) await trackedMember.ReloadAsync(ct);
        return user;
    }

    private async Task<MembershipApplication?> LockApplication(Guid id, CancellationToken ct)
    {
        var application = await db.Applications.FromSqlInterpolated($"SELECT * FROM \"Applications\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (application is not null) await db.Entry(application).ReloadAsync(ct);
        return application;
    }

    private async Task<bool> Repeated(Guid operation, ClaimsPrincipal actor, long id, string action, string reason, CancellationToken ct)
    {
        var prior = await db.LifecycleActions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operation, ct);
        if (prior is null) return false;
        if (prior.UserId != id || prior.ActorId != Authority.Id(actor) || prior.Action != action || prior.Reason != reason.Trim())
            throw new ValidationException("This operation key was already used for another action.");
        return true;
    }

    private void Record(Guid operation, ClaimsPrincipal actor, long id, string action, string reason)
        => db.LifecycleActions.Add(new() { Id = operation, ActorId = Authority.Id(actor), UserId = id, Action = action, Reason = reason.Trim(), OccurredAt = clock.Now });

    private static void ValidateOperation(Guid operation, string reason)
    {
        if (operation == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) throw new ValidationException("Supply a reason (up to 1000 characters) and an operation key.");
    }

    public static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw new ValidationException(string.Join(" ", result.Errors.Select(x => x.Code.StartsWith("Duplicate", StringComparison.Ordinal)
            ? "That email address is unavailable." : x.Description)));
    }
}
