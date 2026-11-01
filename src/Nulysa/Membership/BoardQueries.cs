using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Integrations;

namespace Nulysa.Membership;

public sealed record BankAttention(string Account, string Status, DateTimeOffset? LastSuccess, DateTimeOffset? LastAttempt, bool Stale)
{
    public bool NeedsAttention => Stale || Status is not ("Current" or "Synchronizing");
}

public sealed record BoardAttention(int Applications, int PaymentReviews, int InactiveMembers, int PendingDeletions,
    int EssentialMailProblems, bool BankEnabled, int StaleHours, List<BankAttention> BankAccounts);

public sealed class BoardQueries(AppDbContext db, MembershipQueries members, AssociationClock clock, IOptions<BankOptions> bank)
{
    public async Task<BoardAttention> Attention(ClaimsPrincipal actor, int inactiveDays, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var inactive = await members.Registry(actor, null, state: "inactive", inactiveDays: inactiveDays, ct: ct);
        var applications = await db.Applications.CountAsync(application => application.VerifiedAt != null && application.RejectedAt == null, ct);
        var payments = await db.Receipts.CountAsync(receipt => receipt.State == ReceiptState.New || receipt.State == ReceiptState.Review, ct);
        var deletions = await db.Users.CountAsync(user => user.PendingDeletionAt != null && db.Members.Any(member => member.UserId == user.Id), ct);
        var mail = await db.Deliveries.CountAsync(delivery => delivery.Kind != MailKind.Invoice &&
            (delivery.State == DeliveryState.Review || delivery.State == DeliveryState.Failed), ct);
        var recorded = await db.BankSyncStates.AsNoTracking().ToListAsync(ct);
        var accounts = bank.Value.Enabled ? bank.Value.Accounts : recorded.Select(status => status.Account);
        var staleBefore = clock.Now.AddHours(-bank.Value.StaleHours);
        var statuses = accounts.Distinct().Order().Select(account =>
        {
            var status = recorded.SingleOrDefault(state => state.Account == account);
            var stale = status?.LastSuccess is null || status.LastSuccess < staleBefore;
            return new BankAttention(account, status?.Status ?? "Never synchronized", status?.LastSuccess, status?.LastAttempt, stale);
        }).ToList();
        return new(applications, payments, inactive.Total, deletions, mail, bank.Value.Enabled, bank.Value.StaleHours, statuses);
    }
}
