using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Services;

namespace Nulysa.Billing;

public sealed record Balances(decimal BillingCredit, decimal PurchaseBalance, decimal Held);
public sealed record Standing(bool Active, string Description, DateOnly? Until, DateOnly? InactiveSince = null);

// One locked account's facts. Amounts are derived from the journal, never stored counters.
internal sealed class BillingFacts
{
    public required BillingAccount Account { get; init; }
    public User? User { get; init; }
    public required List<BillingGroup> Groups { get; init; }
    public required List<RecurringItem> Items { get; init; }
    public required List<Invoice> Invoices { get; init; }
    public required List<InvoiceLine> Lines { get; init; }
    public required List<MoneyMovement> Movements { get; init; }
    public required List<Coverage> Coverage { get; init; }
    public required List<ServiceEnrollment> Enrollments { get; init; }
    public required List<ServiceDefinition> Services { get; init; }
    public required List<FeeDefinition> Fees { get; init; }
    public required List<FeePrice> Prices { get; init; }
    public required List<Discount> Discounts { get; init; }
    public int MembershipRenewalGraceDays { get; init; }

    public static async Task<BillingFacts> Load(AppDbContext db, long id, CancellationToken ct, int membershipRenewalGraceDays = 0)
    {
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new ValidationException("Choose an existing financial account ID.");
        var invoices = await db.Invoices.Where(x => x.BillingAccountId == id).ToListAsync(ct);
        var groups = await db.BillingGroups.Where(x => x.BillingAccountId == id).ToListAsync(ct);
        return new()
        {
            Account = account,
            MembershipRenewalGraceDays = membershipRenewalGraceDays,
            User = await db.Users.SingleOrDefaultAsync(x => x.Id == account.UserId, ct),
            Groups = groups,
            Invoices = invoices,
            Lines = await db.InvoiceLines.Where(x => db.Invoices.Any(i => i.Id == x.InvoiceId && i.BillingAccountId == id)).ToListAsync(ct),
            Items = await db.RecurringItems.Where(x => db.BillingGroups.Any(g => g.Id == x.BillingGroupId && g.BillingAccountId == id)).ToListAsync(ct),
            Movements = await db.MoneyMovements.Where(x => x.BillingAccountId == id).ToListAsync(ct),
            Coverage = await db.Coverage.Where(x => x.BillingAccountId == id).ToListAsync(ct),
            Enrollments = await db.ServiceEnrollments.Where(x => x.UserId == account.UserId).ToListAsync(ct),
            Services = await db.ServiceDefinitions.ToListAsync(ct),
            Fees = await db.Fees.ToListAsync(ct),
            Prices = await db.FeePrices.ToListAsync(ct),
            Discounts = await db.Discounts.Where(x => x.BillingAccountId == id).ToListAsync(ct)
        };
    }

    public Balances Balances => new(
        Movements.Sum(x => x.Kind switch
        {
            MovementKind.ReceiptToBilling or MovementKind.LineToBilling or MovementKind.PurchaseToBilling or MovementKind.PurchaseCredit => x.Amount,
            MovementKind.BillingToLine or MovementKind.BillingToPurchase or MovementKind.BillingRefund or MovementKind.BillingReceiptReversal => -x.Amount,
            _ => 0
        }),
        Movements.Sum(x => x.Kind switch
        {
            MovementKind.ReceiptToPurchase or MovementKind.BillingToPurchase => x.Amount,
            MovementKind.PurchaseToBilling or MovementKind.PurchaseRefund or MovementKind.PurchaseReceiptReversal => -x.Amount,
            _ => 0
        }), Lines.Sum(Held));

    public decimal Held(InvoiceLine line) => Movements
        .Where(x => x.InvoiceLineId == line.Id)
        .Sum(x => x.Kind switch
        {
            MovementKind.ReceiptToLine or MovementKind.BillingToLine => x.Amount,
            MovementKind.LineToBilling or MovementKind.LinePurchase => -x.Amount,
            _ => 0
        });

    public IEnumerable<Period> Periods(Benefit benefit, long? service) => Coverage
        .Where(x => x.Benefit == benefit && x.ServiceDefinitionId == service)
        .Select(x => new Period(x.StartsOn, x.EndsOn));

    public Standing Membership(DateOnly on)
        => MembershipStanding(User is { MayLogIn: true }, Coverage, Lines, Items.Any(x => x.MembershipId != null && x.EndedAt == null), MembershipRenewalGraceDays, on);

    internal static Standing MembershipStanding(bool mayLogIn, IEnumerable<Coverage> coverage, IEnumerable<InvoiceLine> lines, bool renewing, int graceDays, DateOnly on)
    {
        if (!mayLogIn)
            return new(false, "Pending deletion or unavailable", null);

        var coveredPeriod = coverage
            .Where(x => x.Benefit == Benefit.Membership && x.StartsOn <= on && on < x.EndsOn)
            .OrderByDescending(x => x.EndsOn)
            .FirstOrDefault();
        if (coveredPeriod is not null)
            return new(true, "Covered", coveredPeriod.EndsOn);

        var graceLine = lines
            .Where(x => x.Benefit == Benefit.Membership && x.State == LineState.Open &&
                x.Purpose != PurchasePurpose.Resumption && x.StartsOn <= on && on < x.ExpiresOn)
            .OrderByDescending(x => x.ExpiresOn)
            .FirstOrDefault();
        if (graceLine is not null)
            return new(true, "Active during grace", graceLine.ExpiresOn);

        var previousPeriod = coverage
            .Where(x => x.Benefit == Benefit.Membership && x.EndsOn <= on)
            .OrderByDescending(x => x.EndsOn)
            .FirstOrDefault();
        var withinRenewalGrace = previousPeriod is not null
            && on < previousPeriod.EndsOn.AddDays(graceDays)
            && renewing;
        var renewalWasCancelled = previousPeriod is not null && lines.Any(x =>
            x.Benefit == Benefit.Membership
            && x.StartsOn == previousPeriod.EndsOn
            && x.State is LineState.Cancelled or LineState.Expired);
        if (withinRenewalGrace && !renewalWasCancelled)
            return new(true, "Active during grace", previousPeriod!.EndsOn.AddDays(graceDays));

        var inactiveSince = lines
            .Where(x => x.Benefit == Benefit.Membership && x.Purpose != PurchasePurpose.Resumption && x.ExpiresOn <= on && x.State != LineState.Fulfilled)
            .Select(x => (DateOnly?)x.ExpiresOn)
            .Append(previousPeriod?.EndsOn.AddDays(graceDays))
            .Where(x => x <= on)
            .Max();
        return new(false, "Inactive — membership continues; billing paused", null, inactiveSince);
    }

    public Standing Service(ServiceEnrollment enrollment, DateOnly on)
    {
        if (enrollment.EndedAt is not null)
            return new(false, "Not subscribed", null);
        if (!Membership(on).Active)
            return new(false, "Membership inactive", null);

        var coveredPeriod = Periods(Benefit.Service, enrollment.ServiceDefinitionId)
            .Where(x => x.Start <= on && on < x.End)
            .OrderByDescending(x => x.End)
            .FirstOrDefault();
        if (coveredPeriod != default)
            return new(true, enrollment.RenewalStoppedAt is null ? "Covered" : "Renewal stopped — covered", coveredPeriod.End);

        var graceEnd = ServiceGraceEnd(enrollment, on);
        if (graceEnd > on)
            return new(true, "Renewal grace", graceEnd);
        return new(false, "Awaiting payment — no current coverage", null);
    }

    public DateOnly? ServiceGraceEnd(ServiceEnrollment enrollment, DateOnly on)
    {
        if (enrollment.RenewalStoppedAt is not null || enrollment.FirstPaidOn is null)
            return null;

        var renewalLine = Lines
            .Where(x => x.ServiceEnrollmentId == enrollment.Id
                && x.Purpose is PurchasePurpose.Renewal or PurchasePurpose.Alignment
                && x.StartsOn <= on
                && x.State is LineState.Open or LineState.Expired)
            .OrderByDescending(x => x.StartsOn)
            .FirstOrDefault();
        if (renewalLine is not null)
            return renewalLine.ExpiresOn;

        var lastCoverage = Coverage
            .Where(x => x.Benefit == Benefit.Service && x.ServiceDefinitionId == enrollment.ServiceDefinitionId && x.StartsOn <= on)
            .OrderByDescending(x => x.EndsOn)
            .FirstOrDefault();
        return lastCoverage?.EndsOn.AddDays(Services.Single(x => x.Id == enrollment.ServiceDefinitionId).RenewalGraceDays);
    }
}
