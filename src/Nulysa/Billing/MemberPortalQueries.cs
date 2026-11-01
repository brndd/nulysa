using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Billing;

public sealed record OpenBillSummary(long Id, DateOnly? DueOn);
public sealed record PortalOverview(Standing? Membership, string? PendingEmail, List<OpenBillSummary> Bills, bool HasMoreBills);
public sealed record InvoiceEmailSettings(long AccountId, bool Enabled);

public sealed class MemberPortalQueries(AppDbContext db, AssociationClock clock, IOptions<BillingOptions> options)
{
    public async Task<string?> PendingEmail(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        var id = Authority.Id(actor);
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        return await db.Users.Where(user => user.Id == id).Select(user => user.PendingEmail).SingleAsync(ct);
    }

    public async Task<PortalOverview> Overview(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        var id = Authority.Id(actor);
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        var pendingEmail = await db.Users.Where(user => user.Id == id).Select(user => user.PendingEmail).SingleAsync(ct);
        var accountId = await db.BillingAccounts.Where(account => account.UserId == id).Select(account => (long?)account.Id).SingleOrDefaultAsync(ct);
        if (accountId is null)
            return new(null, pendingEmail, [], false);

        var today = clock.Today;
        var coverage = await db.Coverage.AsNoTracking()
            .Where(period => period.BillingAccountId == accountId && period.Benefit == Benefit.Membership && period.StartsOn <= today)
            .OrderByDescending(period => period.EndsOn)
            .Take(1)
            .ToListAsync(ct);
        var lines = await (from line in db.InvoiceLines.AsNoTracking()
                           join invoice in db.Invoices on line.InvoiceId equals invoice.Id
                           where invoice.BillingAccountId == accountId && line.Benefit == Benefit.Membership
                           select line).ToListAsync(ct);
        var renewing = await db.RecurringItems.AnyAsync(item => item.MembershipId == id && item.EndedAt == null, ct);
        var membership = BillingFacts.MembershipStanding(true, coverage, lines, renewing, options.Value.MembershipRenewalGraceDays, today);
        var bills = await db.Invoices.AsNoTracking()
            .Where(invoice => invoice.BillingAccountId == accountId && invoice.ClosedAt == null &&
                db.InvoiceLines.Any(line => line.InvoiceId == invoice.Id && line.State == LineState.Open && line.Amount > 0 && line.ExpiresOn > today))
            .OrderBy(invoice => invoice.DueOn)
            .ThenBy(invoice => invoice.Id)
            .Select(invoice => new OpenBillSummary(invoice.Id, invoice.DueOn))
            .Take(6)
            .ToListAsync(ct);
        return new(membership, pendingEmail, bills.Take(5).ToList(), bills.Count > 5);
    }

    public async Task<InvoiceEmailSettings?> EmailSettings(ClaimsPrincipal actor, long id, CancellationToken ct = default)
    {
        await Authority.RequireOwnerOrAdministrator(db, actor, id, ct);
        return await db.BillingAccounts.AsNoTracking()
            .Where(account => account.UserId == id)
            .Select(account => new InvoiceEmailSettings(account.Id, account.EmailInvoices))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<Dictionary<long, decimal?>> ServicePrices(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireOwnerOrAdministrator(db, actor, Authority.Id(actor), ct);
        var today = clock.Today;
        return await db.Fees.AsNoTracking()
            .Where(fee => fee.ServiceDefinitionId != null && !fee.Retired)
            .Select(fee => new
            {
                ServiceId = fee.ServiceDefinitionId!.Value,
                Amount = db.FeePrices.Where(price => price.FeeDefinitionId == fee.Id && price.EffectiveOn <= today)
                    .OrderByDescending(price => price.EffectiveOn)
                    .Select(price => (decimal?)price.MonthlyAmount)
                    .FirstOrDefault()
            })
            .ToDictionaryAsync(price => price.ServiceId, price => price.Amount, ct);
    }
}
