using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Communications;
using Nulysa.Data;

namespace Nulysa.Membership;

public sealed record ApplicationReview(Guid Id, string Name, string Email, string Municipality, DateOnly DateOfBirth,
    string Language, string? PhoneNumber, string? Nickname, string? MatrixId, string Message);
public sealed record RegistryEntry(long Id, string Name, string Email, DateOnly JoinedOn, DateTimeOffset? PendingDeletionAt);
public sealed record MemberDetails(long Id, string Email, ProfileInput Profile, DateOnly JoinedOn, DateTimeOffset? PendingDeletionAt,
    DateTimeOffset? DeletionEligibleAt, string? PendingEmail, Approval? Approval, List<LifecycleAction> History);
public sealed record MailAttention(Guid Id, MailKind Kind, DeliveryState State, DateTimeOffset CreatedAt);

public sealed class MembershipQueries(AppDbContext db)
{
    public Task<bool> IsAdministrator(ClaimsPrincipal actor) => Authority.IsAdministrator(db, actor);
    public async Task<List<ApplicationReview>> Applications(ClaimsPrincipal actor, Guid? id = null, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var query = db.Applications.AsNoTracking().Where(x => x.VerifiedAt != null && x.RejectedAt == null);
        if (id is not null) query = query.Where(x => x.Id == id);
        return await query.OrderBy(x => x.VerifiedAt).Take(100)
            .Select(x => new ApplicationReview(x.Id, x.Name, x.Email, x.Municipality, x.DateOfBirth, x.Language, x.PhoneNumber, x.Nickname, x.MatrixId, x.Message)).ToListAsync(ct);
    }

    public async Task<List<RegistryEntry>> Registry(ClaimsPrincipal actor, string? search, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var query = from user in db.Users.AsNoTracking() join member in db.Members on user.Id equals member.UserId select new { user, member };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            _ = long.TryParse(term, out var id);
            query = query.Where(x => x.user.Id == id || x.user.Name.ToUpper().Contains(term) || x.user.NormalizedEmail!.Contains(term));
        }
        return await query.OrderByDescending(x => x.user.PendingDeletionAt != null).ThenBy(x => x.user.Name).Take(100)
            .Select(x => new RegistryEntry(x.user.Id, x.user.Name, x.user.Email!, x.member.JoinedOn, x.user.PendingDeletionAt)).ToListAsync(ct);
    }

    public async Task<MemberDetails?> Details(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        var details = await (from user in db.Users.AsNoTracking()
                             join member in db.Members on user.Id equals member.UserId
                             where user.Id == id
                             select new MemberDetails(user.Id, user.Email!, new ProfileInput
                             {
                                 Name = user.Name,
                                 Municipality = user.Municipality,
                                 DateOfBirth = user.DateOfBirth,
                                 Language = user.Language,
                                 PhoneNumber = user.PhoneNumber,
                                 Nickname = user.Nickname,
                                 MatrixId = user.MatrixId
                             }, member.JoinedOn, user.PendingDeletionAt, member.DeletionEligibleAt, user.PendingEmail, null, new List<LifecycleAction>())).SingleOrDefaultAsync(ct);
        if (details is null) return null;
        if (await Authority.IsAdministrator(db, actor, ct))
        {
            details = details with { Approval = await db.Approvals.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct) };
            details.History.AddRange(await db.LifecycleActions.AsNoTracking().Where(x => x.UserId == id).OrderByDescending(x => x.OccurredAt).Take(50).ToListAsync(ct));
        }
        return details;
    }

    public async Task<List<MailAttention>> MailFailures(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.Deliveries.AsNoTracking().Where(x => x.State == DeliveryState.Review || x.State == DeliveryState.Failed)
            .OrderBy(x => x.CreatedAt).Take(50).Select(x => new MailAttention(x.Id, x.Kind, x.State, x.CreatedAt)).ToListAsync(ct);
    }
}
