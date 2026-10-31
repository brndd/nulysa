using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;

namespace Nulysa.Billing;

public readonly record struct Period(DateOnly Start, DateOnly End);
public sealed record PriceSegment(DateOnly Start, DateOnly End, DateOnly BasisStart, DateOnly BasisEnd,
    decimal BasePrice, decimal Price, long? DiscountId);
public sealed record Quote(decimal Amount, List<PriceSegment> Segments);

public static class BillingCalendar
{
    public static Period Containing(DateOnly anchor, int months, DateOnly date)
    {
        if (months is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(months));

        var monthDifference = (date.Year - anchor.Year) * 12 + date.Month - anchor.Month;
        var step = (int)Math.Floor((decimal)monthDifference / months);
        var start = anchor.AddMonths(step * months);
        if (start > date)
            start = anchor.AddMonths(--step * months);
        return new(start, anchor.AddMonths((step + 1) * months));
    }

    public static Period Bridge(DateOnly anchor, int months, DateOnly start, bool mergeShort)
    {
        var period = Containing(anchor, months, start);
        var end = period.End;
        if (mergeShort && end.DayNumber - start.DayNumber < 7)
            end = Containing(anchor, months, end).End;
        return new(start, end);
    }

    // A due date is payable through that day; the first non-payable day is at least the next day.
    public static DateOnly ExclusiveExpiry(DateOnly due, int graceDays)
        => due.AddDays(Math.Max(1, graceDays));

    public static List<Period> Uncovered(Period period, IEnumerable<Period> coverage)
    {
        var gaps = new List<Period>();
        var cursor = period.Start;
        foreach (var covered in coverage.OrderBy(x => x.Start))
        {
            if (covered.End <= cursor || covered.Start >= period.End)
                continue;
            if (covered.Start > cursor)
                gaps.Add(new(cursor, covered.Start));

            cursor = covered.End > cursor ? covered.End : cursor;
            if (cursor >= period.End)
                break;
        }
        if (cursor < period.End)
            gaps.Add(new(cursor, period.End));
        return gaps;
    }
}

public static class Pricing
{
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static Quote Calculate(DateOnly anchor, int months, Period period, IReadOnlyList<FeePrice> prices,
        IReadOnlyList<Discount> discounts, IEnumerable<Period> coverage)
    {
        if (period.End <= period.Start)
            throw new ValidationException("A billing period must have positive length.");

        var segments = new List<PriceSegment>();
        foreach (var uncovered in BillingCalendar.Uncovered(period, coverage))
        {
            var cursor = uncovered.Start;
            while (cursor < uncovered.End)
            {
                var basis = BillingCalendar.Containing(anchor, months, cursor);
                var price = prices.Where(x => x.EffectiveOn <= cursor).OrderByDescending(x => x.EffectiveOn).FirstOrDefault()
                    ?? throw new ValidationException("Configure a price effective before this billing period first.");
                var discount = discounts.SingleOrDefault(x => x.WithdrawnAt is null && x.StartsOn <= cursor && cursor < x.EndsOn);
                var segmentEnd = new[] { uncovered.End, basis.End }
                    .Concat(prices.Where(x => x.EffectiveOn > cursor).Select(x => x.EffectiveOn))
                    .Concat(discounts.Where(x => x.WithdrawnAt is null).SelectMany(x => new[] { x.StartsOn, x.EndsOn }).Where(x => x > cursor))
                    .Min();
                var fullPrice = price.MonthlyAmount * months;
                var discountedPrice = discount is null
                    ? fullPrice
                    : discount.Kind == DiscountKind.Percentage
                        ? fullPrice * (100 - discount.Value) / 100
                        : discount.Value * months;

                segments.Add(new(cursor, segmentEnd, basis.Start, basis.End, fullPrice, discountedPrice, discount?.Id));
                cursor = segmentEnd;
            }
        }
        return FromSegments(segments);
    }

    public static Quote FromSegments(IEnumerable<PriceSegment> segments)
    {
        var segmentList = segments.ToList();
        var amount = Round(segmentList.Sum(x => x.Price * (x.End.DayNumber - x.Start.DayNumber) / (x.BasisEnd.DayNumber - x.BasisStart.DayNumber)));
        return new(amount, segmentList);
    }

    public static Quote Slice(IEnumerable<PriceSegment> segments, DateOnly start, IEnumerable<Period>? coverage = null)
    {
        var sliced = segments
            .Where(x => x.End > start)
            .SelectMany(x => BillingCalendar.Uncovered(new(x.Start < start ? start : x.Start, x.End), coverage ?? [])
                .Select(period => x with { Start = period.Start, End = period.End }));
        return FromSegments(sliced);
    }

    public static Quote Adjust(IEnumerable<PriceSegment> quoted, IReadOnlyList<Discount> discounts, DateOnly effectiveOn, IEnumerable<Period> coverage)
    {
        var adjusted = new List<PriceSegment>();
        foreach (var original in quoted)
        {
            foreach (var uncovered in BillingCalendar.Uncovered(new(original.Start, original.End), coverage))
            {
                var cursor = uncovered.Start;
                while (cursor < uncovered.End)
                {
                    var segmentEnd = new[] { uncovered.End }
                        .Concat(discounts.SelectMany(x => new[] { x.StartsOn, x.EndsOn }))
                        .Append(effectiveOn)
                        .Where(x => x > cursor)
                        .Min();
                    if (cursor < effectiveOn)
                        adjusted.Add(original with { Start = cursor, End = segmentEnd });
                    else
                    {
                        var discount = discounts.SingleOrDefault(x => x.WithdrawnAt is null && x.StartsOn <= cursor && cursor < x.EndsOn);
                        var months = (original.BasisEnd.Year - original.BasisStart.Year) * 12 + original.BasisEnd.Month - original.BasisStart.Month;
                        var price = discount is null
                            ? original.BasePrice
                            : discount.Kind == DiscountKind.Percentage
                                ? original.BasePrice * (100 - discount.Value) / 100
                                : discount.Value * months;
                        adjusted.Add(original with { Start = cursor, End = segmentEnd, Price = price, DiscountId = discount?.Id });
                    }
                    cursor = segmentEnd;
                }
            }
        }
        return FromSegments(adjusted);
    }

    public static decimal FutureDiscountCredit(IReadOnlyList<PriceSegment> original, Discount discount,
        IReadOnlyList<(Discount Discount, decimal Amount)> previous)
    {
        var discountBoundaries = previous
            .Select(x => x.Discount)
            .Append(discount)
            .SelectMany(x => new[] { x.StartsOn, x.EndsOn })
            .Distinct()
            .ToList();
        var splitSegments = original.SelectMany(segment =>
        {
            var dates = discountBoundaries
                .Where(x => segment.Start < x && x < segment.End)
                .Append(segment.Start)
                .Append(segment.End)
                .Order()
                .ToArray();
            return dates.Zip(dates.Skip(1), (start, end) => segment with { Start = start, End = end });
        }).ToList();

        static decimal Reduction(PriceSegment segment, Discount adjustment)
        {
            if (segment.Start < adjustment.StartsOn || segment.End > adjustment.EndsOn)
                return 0;

            var months = (segment.BasisEnd.Year - segment.BasisStart.Year) * 12 + segment.BasisEnd.Month - segment.BasisStart.Month;
            var reduced = adjustment.Kind == DiscountKind.Percentage
                ? segment.BasePrice * (100 - adjustment.Value) / 100
                : adjustment.Value * months;
            return Math.Max(0, segment.Price - reduced) * (segment.End.DayNumber - segment.Start.DayNumber) / (segment.BasisEnd.DayNumber - segment.BasisStart.DayNumber);
        }

        var creditedBySegment = new decimal[splitSegments.Count];
        foreach (var credit in previous)
        {
            var uncreditedReduction = splitSegments
                .Select((segment, i) => Math.Max(0, Reduction(segment, credit.Discount) - creditedBySegment[i]))
                .ToArray();
            var totalUncredited = uncreditedReduction.Sum();
            if (totalUncredited == 0)
                throw new ValidationException("Previous discount credits require treasurer reconciliation.");

            // Distribute actual rounded money only over the days this correction compensated.
            for (var i = 0; i < splitSegments.Count; i++)
                creditedBySegment[i] += credit.Amount * uncreditedReduction[i] / totalUncredited;
        }

        var remainingCredit = Round(splitSegments
            .Select((segment, i) => Math.Max(0, Reduction(segment, discount) - creditedBySegment[i]))
            .Sum());
        return Math.Max(0, Math.Min(remainingCredit, FromSegments(original).Amount - previous.Sum(x => x.Amount)));
    }
}

public static class PaymentReference
{
    public static string Create(char prefix)
    {
        var value = prefix + (BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) % 1_000_000_000_000_000UL).ToString("D15", CultureInfo.InvariantCulture);
        return Complete(value);
    }

    public static string Complete(string value)
    {
        var sum = value.Reverse().Select((digit, i) => (digit - '0') * new[] { 7, 3, 1 }[i % 3]).Sum();
        return value + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }

    public static string Normalize(string value) => string.Concat(value.Where(x => !char.IsWhiteSpace(x))).ToUpperInvariant();
}
