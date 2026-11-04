using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Services;

namespace Nulysa.Billing;

public sealed record Balances(decimal BillingCredit, decimal PurchaseBalance, decimal Held);

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
    public required List<CreditLot> CreditLots { get; init; }
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
            CreditLots = await db.CreditLots.Where(x => x.BillingAccountId == id).ToListAsync(ct),
            Coverage = await db.Coverage.Where(x => x.BillingAccountId == id).ToListAsync(ct),
            Enrollments = await db.ServiceEnrollments.Where(x => x.UserId == account.UserId).ToListAsync(ct),
            Services = await db.ServiceDefinitions.ToListAsync(ct),
            Fees = await db.Fees.ToListAsync(ct),
            Prices = await db.FeePrices.ToListAsync(ct),
            Discounts = await db.Discounts.Where(x => x.BillingAccountId == id).ToListAsync(ct)
        };
    }

    public Balances? ReadBalances { get; init; }
    public Dictionary<long, decimal>? ReadHolds { get; init; }

    public Balances Balances => ReadBalances ?? new(
        Movements.Sum(x => BillingBalanceDelta(x.Kind) * x.Amount),
        Movements.Sum(x => PurchaseBalanceDelta(x.Kind) * x.Amount),
        Lines.Sum(Held));

    internal static decimal BillingBalanceDelta(MovementKind kind) => kind switch
    {
        MovementKind.ReceiptToBilling or MovementKind.LineToBilling or MovementKind.PurchaseToBilling or MovementKind.PurchaseCredit => 1,
        MovementKind.BillingToLine or MovementKind.BillingToPurchase or MovementKind.BillingRefund or MovementKind.BillingReceiptReversal => -1,
        _ => 0
    };

    internal static decimal PurchaseBalanceDelta(MovementKind kind) => kind switch
    {
        MovementKind.ReceiptToPurchase or MovementKind.BillingToPurchase => 1,
        MovementKind.PurchaseToBilling or MovementKind.PurchaseRefund or MovementKind.PurchaseReceiptReversal => -1,
        _ => 0
    };

    internal static decimal HeldBalanceDelta(MovementKind kind) => kind switch
    {
        MovementKind.ReceiptToLine or MovementKind.BillingToLine => 1,
        MovementKind.LineToBilling or MovementKind.LinePurchase => -1,
        _ => 0
    };

    public decimal Held(InvoiceLine line) => ReadHolds?.GetValueOrDefault(line.Id) ?? Movements
        .Where(x => x.InvoiceLineId == line.Id)
        .Sum(x => x.Kind switch
        {
            MovementKind.ReceiptToLine or MovementKind.BillingToLine => x.Amount,
            MovementKind.LineToBilling or MovementKind.LinePurchase => -x.Amount,
            _ => 0
        });

    public IEnumerable<Period> Periods(Benefit benefit, long? service) => Coverage
        .Where(x => x.Benefit == benefit && x.ServiceDefinitionId == service)
        .Select(x => new Period(x.StartsOn, x.EndExclusive));

    public Period MembershipGrantPeriod(int months, DateOnly on)
    {
        if (months is < 1 or > 120)
            throw new ValidationException("Grant between 1 and 120 whole months.");
        var periods = Periods(Benefit.Membership, null)
            .OrderBy(period => period.Start)
            .ToList();
        var hasCurrentCoverage = periods.Any(period => period.Start <= on && on < period.EndExclusive);
        if (!hasCurrentCoverage)
            throw new ValidationException("A duration extension requires current paid or granted membership coverage.");
        var extensionStart = on;
        foreach (var period in periods)
        {
            var extendsCoverage = period.Start <= extensionStart && period.EndExclusive > extensionStart;
            if (extendsCoverage)
                extensionStart = period.EndExclusive;
        }
        return new(extensionStart, extensionStart.AddMonths(months));
    }

    public Standing Membership(DateOnly on)
        => MembershipStanding(User is { MayLogIn: true }, Coverage, Lines, Items.Any(x => x.MembershipUserId != null && x.EndedAt == null), MembershipRenewalGraceDays, on);

    /// <summary>Derives membership standing for a date from account access, coverage and payment requests.</summary>
    /// <remarks>
    /// Renewal grace can apply before the worker issues a request. A return request alone gives no grace.
    /// </remarks>
    internal static Standing MembershipStanding(bool mayLogIn, IEnumerable<Coverage> coverage, IEnumerable<InvoiceLine> lines, bool renewing, int graceDays, DateOnly on)
    {
        if (!mayLogIn)
            return new(StandingState.Unavailable, null);

        var coveredPeriod = coverage
            .Where(x => x.Benefit == Benefit.Membership && x.StartsOn <= on && on < x.EndExclusive)
            .OrderByDescending(x => x.EndExclusive)
            .FirstOrDefault();
        if (coveredPeriod is not null)
            return new(StandingState.Covered, coveredPeriod.EndExclusive);

        var graceLine = lines
            .Where(x => x.Benefit == Benefit.Membership && x.State == LineState.Open &&
                x.Purpose != PurchasePurpose.Resumption && x.StartsOn <= on && on < x.PaymentCutoffExclusive)
            .OrderByDescending(x => x.PaymentCutoffExclusive)
            .FirstOrDefault();
        if (graceLine is not null)
        {
            var isInitial = graceLine.Purpose == PurchasePurpose.Initial;
            var isDueToday = graceDays == 0 && graceLine.Purpose is PurchasePurpose.Renewal or PurchasePurpose.Alignment;
            return new(isInitial ? StandingState.MembershipInitialGrace : StandingState.MembershipRenewalGrace,
                graceLine.PaymentCutoffExclusive, DueToday: isDueToday);
        }

        // Standing must remain correct when the worker has not issued the next request yet.
        var previousPeriod = coverage
            .Where(x => x.Benefit == Benefit.Membership && x.EndExclusive <= on)
            .OrderByDescending(x => x.EndExclusive)
            .FirstOrDefault();
        var renewalCutoffExclusive = previousPeriod is { } previous
            ? BillingCalendar.PaymentCutoffExclusive(previous.EndExclusive, graceDays)
            : (DateOnly?)null;
        var withinRenewalGrace = previousPeriod is not null
            && on < renewalCutoffExclusive
            && renewing;
        var renewalWasCancelled = previousPeriod is not null && lines.Any(x =>
            x.Benefit == Benefit.Membership
            && x.StartsOn == previousPeriod.EndExclusive
            && x.State is LineState.Cancelled or LineState.Expired);
        if (withinRenewalGrace && !renewalWasCancelled)
        {
            return new(StandingState.MembershipRenewalGrace, renewalCutoffExclusive, DueToday: graceDays == 0);
        }

        var inactiveSince = lines
            .Where(x => x.Benefit == Benefit.Membership && x.Purpose != PurchasePurpose.Resumption && x.PaymentCutoffExclusive <= on && x.State != LineState.Fulfilled)
            .Select(x => (DateOnly?)x.PaymentCutoffExclusive)
            .Append(renewalCutoffExclusive)
            .Where(x => x <= on)
            .Max();
        return new(StandingState.MembershipInactive, null, inactiveSince);
    }

    public Standing Service(ServiceEnrollment enrollment, DateOnly on)
    {
        if (enrollment.EndedAt is not null)
            return new(StandingState.NotSubscribed, null);
        if (!Membership(on).Active)
            return new(StandingState.ServiceBlockedByMembership, null);

        var coveredPeriod = Periods(Benefit.Service, enrollment.ServiceDefinitionId)
            .Where(x => x.Start <= on && on < x.EndExclusive)
            .OrderByDescending(x => x.EndExclusive)
            .FirstOrDefault();
        if (coveredPeriod != default)
            return new(enrollment.RenewalStoppedAt is null ? StandingState.Covered : StandingState.CoveredWithRenewalStopped, coveredPeriod.EndExclusive);

        var service = Services.Single(x => x.Id == enrollment.ServiceDefinitionId);
        var graceEnd = ServiceGraceEndExclusive(enrollment, on);
        if (graceEnd > on)
            return new(StandingState.ServiceRenewalGrace, graceEnd, DueToday: service.RenewalGraceDays == 0);
        return new(StandingState.AwaitingPayment, null);
    }

    public DateOnly? ServiceGraceEndExclusive(ServiceEnrollment enrollment, DateOnly on)
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
            return renewalLine.PaymentCutoffExclusive;

        var lastCoverage = Coverage
            .Where(x => x.Benefit == Benefit.Service && x.ServiceDefinitionId == enrollment.ServiceDefinitionId && x.StartsOn <= on)
            .OrderByDescending(x => x.EndExclusive)
            .FirstOrDefault();
        return lastCoverage is { } coverage
            ? BillingCalendar.PaymentCutoffExclusive(coverage.EndExclusive, Services.Single(x => x.Id == enrollment.ServiceDefinitionId).RenewalGraceDays)
            : null;
    }
}
