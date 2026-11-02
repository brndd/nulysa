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
public sealed record RegistryPage(List<RegistryEntry> Entries, int Total, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
}
public sealed record MemberDetails(long Id, string Email, ProfileInput Profile, DateOnly JoinedOn, DateTimeOffset? PendingDeletionAt,
    DateTimeOffset? DeletionEligibleAt, string? PendingEmail, Approval? Approval, List<LifecycleAction> History);
public sealed record MailAttention(Guid Id, MailKind Kind, DeliveryState State, DateTimeOffset CreatedAt, int Attempts, string? ProviderId);
public sealed record MailAttentionPage(List<MailAttention> Entries, int Total, int Page)
{
    public const int PageSize = 25;
    public int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

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

    public async Task<RegistryPage> Registry(ClaimsPrincipal actor, string? search, string state = "all", int inactiveDays = 0,
        string sort = "attention", int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var entries = await RegistryEntries(actor, search, ct);
        var cutoff = clock.Today.AddDays(-Math.Clamp(inactiveDays, 0, 36500));
        var filtered = entries.Where(entry => state switch
        {
            "pending-deletion" => entry.PendingDeletionAt is not null,
            "active" => entry.PendingDeletionAt is null && entry.Standing!.Active,
            "inactive" => entry.PendingDeletionAt is null && !entry.Standing!.Active &&
                (inactiveDays <= 0 || entry.Standing.InactiveSince <= cutoff),
            _ => true
        }).ToList();
        var ordered = sort switch
        {
            "name" => filtered.OrderBy(entry => entry.Name).ThenBy(entry => entry.Id),
            "newest" => filtered.OrderByDescending(entry => entry.JoinedOn).ThenByDescending(entry => entry.Id),
            _ => filtered.OrderByDescending(entry => entry.PendingDeletionAt != null)
                .ThenBy(entry => entry.Standing!.Active)
                .ThenBy(entry => entry.Standing!.InactiveSince ?? DateOnly.MaxValue)
                .ThenBy(entry => entry.Name)
                .ThenBy(entry => entry.Id)
        };
        var size = pageSize is 25 or 50 or 100 ? pageSize : 25;
        var currentPage = Math.Clamp(page, 1, Math.Max(1, (filtered.Count + size - 1) / size));
        return new(ordered.Skip((currentPage - 1) * size).Take(size).ToList(), filtered.Count, currentPage, size);
    }

    private async Task<List<RegistryEntry>> RegistryEntries(ClaimsPrincipal actor, string? search, CancellationToken ct)
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
        // The standing evaluator needs only the latest started coverage and relevant line boundaries, not the journal.
        var startedCoverage = db.Coverage.AsNoTracking()
            .Where(period => accountIds.Contains(period.BillingAccountId) && period.Benefit == Benefit.Membership && period.StartsOn <= clock.Today);
        var coverage = (await startedCoverage
            .GroupBy(period => period.BillingAccountId)
            .Select(periods => periods.OrderByDescending(period => period.EndExclusive).ThenByDescending(period => period.Id).First())
            .ToListAsync(ct))
            .ToLookup(x => x.BillingAccountId);
        var membershipLines = from line in db.InvoiceLines.AsNoTracking()
                              join invoice in db.Invoices on line.InvoiceId equals invoice.Id
                              where accountIds.Contains(invoice.BillingAccountId) && line.Benefit == Benefit.Membership
                              select new { invoice.BillingAccountId, line };
        var graceLines = await membershipLines
            .Where(row => row.line.State == LineState.Open && row.line.Purpose != PurchasePurpose.Resumption &&
                row.line.StartsOn <= clock.Today && clock.Today < row.line.PaymentCutoffExclusive)
            .GroupBy(row => row.BillingAccountId)
            .Select(rows => rows.OrderByDescending(row => row.line.PaymentCutoffExclusive).ThenByDescending(row => row.line.Id).First())
            .ToListAsync(ct);
        var expiredLines = await membershipLines
            .Where(row => row.line.Purpose != PurchasePurpose.Resumption && row.line.State != LineState.Fulfilled && row.line.PaymentCutoffExclusive <= clock.Today)
            .GroupBy(row => row.BillingAccountId)
            .Select(rows => rows.OrderByDescending(row => row.line.PaymentCutoffExclusive).ThenByDescending(row => row.line.Id).First())
            .ToListAsync(ct);
        var cancelledRenewals = await membershipLines
            .Where(row => (row.line.State == LineState.Cancelled || row.line.State == LineState.Expired) &&
                row.line.StartsOn == startedCoverage.Where(period => period.BillingAccountId == row.BillingAccountId).Max(period => (DateOnly?)period.EndExclusive))
            .GroupBy(row => row.BillingAccountId)
            .Select(rows => rows.OrderByDescending(row => row.line.Id).First())
            .ToListAsync(ct);
        var lines = graceLines.Concat(expiredLines).Concat(cancelledRenewals)
            .ToLookup(x => x.BillingAccountId, x => x.line);
        var renewing = (await db.RecurringItems
            .Where(x => x.MembershipUserId != null && ids.Contains(x.MembershipUserId.Value) && x.EndedAt == null)
            .Select(x => x.MembershipUserId!.Value)
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
        }).ToList();
    }

    public async Task<RegistryEntry?> Subject(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await (from user in db.Users.AsNoTracking()
                      join member in db.Members on user.Id equals member.UserId
                      where user.Id == id
                      select new RegistryEntry(user.Id, user.Name, user.Email!, member.JoinedOn, user.PendingDeletionAt, null))
            .SingleOrDefaultAsync(ct);
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

    public async Task<MailAttentionPage> MailFailures(ClaimsPrincipal actor, string state = "all", bool essentialOnly = true,
        int page = 1, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var query = db.Deliveries.AsNoTracking()
            .Where(x => x.State == DeliveryState.Review || x.State == DeliveryState.Failed)
            .Where(x => !essentialOnly || x.Kind != MailKind.Invoice);
        if (state == "failed")
            query = query.Where(x => x.State == DeliveryState.Failed);
        else if (state == "review")
            query = query.Where(x => x.State == DeliveryState.Review);

        var total = await query.CountAsync(ct);
        var currentPage = Math.Clamp(page, 1, Math.Max(1, (total + MailAttentionPage.PageSize - 1) / MailAttentionPage.PageSize));
        var entries = await query
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((currentPage - 1) * MailAttentionPage.PageSize)
            .Take(MailAttentionPage.PageSize)
            .Select(x => new MailAttention(x.Id, x.Kind, x.State, x.CreatedAt, x.Attempts, x.ProviderId))
            .ToListAsync(ct);
        return new(entries, total, currentPage);
    }
}
