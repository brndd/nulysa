using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Data;

namespace Nulysa.Billing;

public sealed record PaymentMember(long AccountId, long UserId, string Name, string Email);

public sealed record PaymentReviewPage(List<Receipt> Entries, int Number, int PageCount, int Total)
{
    public const int Size = 25;
}

public sealed class AdministrationBillingQueries(AppDbContext db)
{
    public async Task<PaymentReviewPage> Review(ClaimsPrincipal actor, bool all, string sort, bool descending,
        int page, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var query = db.Receipts.AsNoTracking();
        if (!all)
            query = query.Where(row => row.State == ReceiptState.New || row.State == ReceiptState.Review);

        var total = await query.CountAsync(ct);
        var pages = Math.Max(1, (total - 1) / PaymentReviewPage.Size + 1);
        page = Math.Clamp(page, 1, pages);
        var ordered = (sort, descending) switch
        {
            ("amount", false) => query.OrderBy(row => row.Amount),
            ("amount", true) => query.OrderByDescending(row => row.Amount),
            ("reference", false) => query.OrderBy(row => row.Reference),
            ("reference", true) => query.OrderByDescending(row => row.Reference),
            ("counterparty", false) => query.OrderBy(row => row.Counterparty),
            ("counterparty", true) => query.OrderByDescending(row => row.Counterparty),
            ("message", false) => query.OrderBy(row => row.Message),
            ("message", true) => query.OrderByDescending(row => row.Message),
            ("reason", false) => query.OrderBy(row => row.ReviewReason),
            ("reason", true) => query.OrderByDescending(row => row.ReviewReason),
            ("status", false) => query.OrderBy(row => row.State),
            ("status", true) => query.OrderByDescending(row => row.State),
            (_, true) => query.OrderByDescending(row => row.ReceivedOn),
            _ => query.OrderBy(row => row.ReceivedOn)
        };
        var entries = await ordered.ThenBy(row => row.Id)
            .Skip((page - 1) * PaymentReviewPage.Size).Take(PaymentReviewPage.Size).ToListAsync(ct);
        return new(entries, page, pages, total);
    }

    public async Task<bool> InvoiceBelongsToMember(ClaimsPrincipal actor, long invoiceId, long userId, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await (from invoice in db.Invoices
                      join account in db.BillingAccounts on invoice.BillingAccountId equals account.Id
                      where invoice.Id == invoiceId && account.UserId == userId
                      select invoice.Id).AnyAsync(ct);
    }

    public async Task<bool> AccountExists(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.BillingAccounts.AnyAsync(account => account.Id == id, ct);
    }

    public async Task<Receipt?> Receipt(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.Receipts.AsNoTracking().SingleOrDefaultAsync(receipt => receipt.Id == id, ct);
    }

    public async Task<List<PaymentMember>> Members(ClaimsPrincipal actor, string? search, long? accountId = null, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (accountId is null && string.IsNullOrWhiteSpace(search))
            return [];

        var term = search?.Trim().ToUpperInvariant() ?? "";
        _ = long.TryParse(term, out var memberId);
        return await (from account in db.BillingAccounts.AsNoTracking()
                      join user in db.Users on account.UserId equals user.Id
                      join member in db.Members on user.Id equals member.UserId
                      where user.Admitted && user.EmailConfirmed && user.PendingDeletionAt == null
                      where accountId != null ? account.Id == accountId : user.Id == memberId || user.Name.ToUpper().Contains(term) || user.NormalizedEmail!.Contains(term)
                      orderby user.Name, user.Id
                      select new PaymentMember(account.Id, user.Id, user.Name, user.Email!))
            .Take(25)
            .ToListAsync(ct);
    }

    public async Task<List<Receipt>> PostedReceipts(ClaimsPrincipal actor, long accountId, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return await db.Receipts.AsNoTracking()
            .Where(receipt => receipt.BillingAccountId == accountId && receipt.State == ReceiptState.Posted)
            .OrderByDescending(receipt => receipt.ReceivedOn)
            .ThenByDescending(receipt => receipt.Id)
            .Take(100)
            .ToListAsync(ct);
    }
}
