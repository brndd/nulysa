using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;

namespace Nulysa.Membership;

public sealed record ApplicationReview(Guid Id, string Name, string Email, string Municipality, DateOnly DateOfBirth,
    string Language, string? PhoneNumber, string? Nickname, string? MatrixId, string Message);
public sealed record RegistryEntry(long Id, string Name, string Email, DateOnly JoinedOn, DateTimeOffset? PendingDeletionAt, Standing? Standing = null);
public sealed record MemberDetails(long Id, string Email, ProfileInput Profile, DateOnly JoinedOn, DateTimeOffset? PendingDeletionAt,
    DateTimeOffset? DeletionEligibleAt, string? PendingEmail, Approval? Approval, List<LifecycleAction> History);
public sealed record MailAttention(Guid Id, MailKind Kind, DeliveryState State, DateTimeOffset CreatedAt);

public sealed class MembershipQueries(AppDbContext db, AssociationClock clock, IOptions<BillingOptions> billing)
{
    public Task<bool> IsAdministrator(ClaimsPrincipal actor) => Authority.IsAdministrator(db, actor);
    public async Task<List<ApplicationReview>> Applications(ClaimsPrincipal actor, Guid? id = null, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var query = db.Applications.AsNoTracking().Where(x => x.VerifiedAt != null && x.RejectedAt == null);
        if (id is not null)
            query = query.Where(x => x.Id == id);

        return await query
            .OrderBy(x => x.VerifiedAt)
            .Take(100)
            .Select(x => new ApplicationReview(x.Id, x.Name, x.Email, x.Municipality, x.DateOfBirth, x.Language, x.PhoneNumber, x.Nickname, x.MatrixId, x.Message))
            .ToListAsync(ct);
    }

    public async Task<List<RegistryEntry>> Registry(ClaimsPrincipal actor, string? search, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var query = from user in db.Users.AsNoTracking()
                    join member in db.Members on user.Id equals member.UserId
                    select new { user, member };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            _ = long.TryParse(term, out var id);
            query = query.Where(x => x.user.Id == id || x.user.Name.ToUpper().Contains(term) || x.user.NormalizedEmail!.Contains(term));
        }

        var entries = await query
            .Select(x => new RegistryEntry(x.user.Id, x.user.Name, x.user.Email!, x.member.JoinedOn, x.user.PendingDeletionAt))
            .ToListAsync(ct);
        var ids = entries.Select(x => x.Id).ToList();
        var accounts = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.UserId != null && ids.Contains(x.UserId.Value))
            .ToListAsync(ct);
        var accountIds = accounts.Select(x => x.Id).ToList();
        var coverage = (await db.Coverage.AsNoTracking()
            .Where(x => accountIds.Contains(x.BillingAccountId) && x.Benefit == Benefit.Membership)
            .ToListAsync(ct))
            .ToLookup(x => x.BillingAccountId);
        var lines = (await (from line in db.InvoiceLines.AsNoTracking()
                            join invoice in db.Invoices on line.InvoiceId equals invoice.Id
                            where accountIds.Contains(invoice.BillingAccountId) && line.Benefit == Benefit.Membership
                            select new { invoice.BillingAccountId, line }).ToListAsync(ct))
            .ToLookup(x => x.BillingAccountId, x => x.line);
        var renewing = (await db.RecurringItems
            .Where(x => x.MembershipId != null && ids.Contains(x.MembershipId.Value) && x.EndedAt == null)
            .Select(x => x.MembershipId!.Value)
            .ToListAsync(ct))
            .ToHashSet();
        var accountByUser = accounts.ToDictionary(x => x.UserId!.Value, x => x.Id);

        return entries.Select(entry => entry with
        {
            Standing = BillingFacts.MembershipStanding(entry.PendingDeletionAt is null,
                coverage[accountByUser.GetValueOrDefault(entry.Id)],
                lines[accountByUser.GetValueOrDefault(entry.Id)],
                renewing.Contains(entry.Id),
                billing.Value.MembershipRenewalGraceDays,
                clock.Today)
        })
            .OrderByDescending(x => x.PendingDeletionAt != null)
            .ThenBy(x => x.Standing!.Active)
            .ThenBy(x => x.Standing!.InactiveSince ?? DateOnly.MaxValue)
            .ThenBy(x => x.Name)
            .Take(100)
            .ToList();
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
                             }, member.JoinedOn, user.PendingDeletionAt, member.DeletionEligibleAt, user.PendingEmail, null, new List<LifecycleAction>()))
            .SingleOrDefaultAsync(ct);
        if (details is null)
            return null;

        if (await Authority.IsAdministrator(db, actor, ct))
        {
            var approval = await db.Approvals.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct);
            var history = await db.LifecycleActions.AsNoTracking()
                .Where(x => x.UserId == id)
                .OrderByDescending(x => x.OccurredAt)
                .Take(50)
                .ToListAsync(ct);
            details = details with { Approval = approval };
            details.History.AddRange(history);
        }
        return details;
    }

    public async Task<List<MailAttention>> MailFailures(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.Deliveries.AsNoTracking()
            .Where(x => x.State == DeliveryState.Review || x.State == DeliveryState.Failed)
            .OrderBy(x => x.CreatedAt)
            .Take(50)
            .Select(x => new MailAttention(x.Id, x.Kind, x.State, x.CreatedAt))
            .ToListAsync(ct);
    }
}
