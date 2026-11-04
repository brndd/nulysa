using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nulysa.Billing;

public readonly record struct Period(DateOnly Start, DateOnly EndExclusive);
public sealed record PriceSegment(DateOnly Start, [property: JsonPropertyName("End")] DateOnly EndExclusive,
    DateOnly BasisStart, [property: JsonPropertyName("BasisEnd")] DateOnly BasisEndExclusive,
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
        // Always count from the original anchor so a clipped February date cannot shift March.
        var start = anchor.AddMonths(step * months);
        if (start > date)
            start = anchor.AddMonths(--step * months);
        return new(start, anchor.AddMonths((step + 1) * months));
    }

    public static Period Bridge(DateOnly anchor, int months, DateOnly start, bool mergeShort)
    {
        var period = Containing(anchor, months, start);
        var end = period.EndExclusive;
        if (mergeShort && end.DayNumber - start.DayNumber < 7)
            end = Containing(anchor, months, end).EndExclusive;
        return new(start, end);
    }

    // The due date is payable; renewal grace days are the days after it.
    public static DateOnly PaymentCutoffExclusive(DateOnly due, int graceDays)
        => due.AddDays(1 + Math.Max(0, graceDays));

    public static List<Period> Uncovered(Period period, IEnumerable<Period> coverage)
    {
        var gaps = new List<Period>();
        var cursor = period.Start;
        foreach (var covered in coverage.OrderBy(x => x.Start))
        {
            if (covered.EndExclusive <= cursor || covered.Start >= period.EndExclusive)
                continue;
            if (covered.Start > cursor)
                gaps.Add(new(cursor, covered.Start));

            cursor = covered.EndExclusive > cursor ? covered.EndExclusive : cursor;
            if (cursor >= period.EndExclusive)
                break;
        }
        if (cursor < period.EndExclusive)
            gaps.Add(new(cursor, period.EndExclusive));
        return gaps;
    }
}

public static class Pricing
{
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static Quote Calculate(DateOnly anchor, int months, Period period, IReadOnlyList<FeePrice> prices,
        IReadOnlyList<Discount> discounts, IEnumerable<Period> coverage)
    {
        if (period.EndExclusive <= period.Start)
            throw new ValidationException("A billing period must have positive length.");

        var segments = new List<PriceSegment>();
        foreach (var uncovered in BillingCalendar.Uncovered(period, coverage))
        {
            var cursor = uncovered.Start;
            while (cursor < uncovered.EndExclusive)
            {
                var basis = BillingCalendar.Containing(anchor, months, cursor);
                var price = prices.Where(x => x.EffectiveOn <= cursor).OrderByDescending(x => x.EffectiveOn).FirstOrDefault()
                    ?? throw new ValidationException("Configure a price effective before this billing period first.");
                var discount = discounts.SingleOrDefault(x => x.WithdrawnAt is null && x.StartsOn <= cursor && cursor < x.EndExclusive);
                var segmentEnd = new[] { uncovered.EndExclusive, basis.EndExclusive }
                    .Concat(prices.Where(x => x.EffectiveOn > cursor).Select(x => x.EffectiveOn))
                    .Concat(discounts.Where(x => x.WithdrawnAt is null).SelectMany(x => new[] { x.StartsOn, x.EndExclusive }).Where(x => x > cursor))
                    .Min();
                var fullPrice = price.MonthlyAmount * months;
                var discountedPrice = discount is null
                    ? fullPrice
                    : discount.Kind == DiscountKind.Percentage
                        ? fullPrice * (100 - discount.Value) / 100
                        : discount.Value * months;

                segments.Add(new(cursor, segmentEnd, basis.Start, basis.EndExclusive, fullPrice, discountedPrice, discount?.Id));
                cursor = segmentEnd;
            }
        }
        return FromSegments(segments);
    }

    public static Quote FromSegments(IEnumerable<PriceSegment> segments)
    {
        var segmentList = segments.ToList();
        // Round the complete purchase once; rounding individual date segments would change its price.
        var amount = Round(segmentList.Sum(x => x.Price * (x.EndExclusive.DayNumber - x.Start.DayNumber) / (x.BasisEndExclusive.DayNumber - x.BasisStart.DayNumber)));
        return new(amount, segmentList);
    }

    public static Quote Slice(IEnumerable<PriceSegment> segments, DateOnly start, IEnumerable<Period>? coverage = null)
    {
        var sliced = segments
            .Where(x => x.EndExclusive > start)
            .SelectMany(x => BillingCalendar.Uncovered(new(x.Start < start ? start : x.Start, x.EndExclusive), coverage ?? [])
                .Select(period => x with { Start = period.Start, EndExclusive = period.EndExclusive }));
        return FromSegments(sliced);
    }

    public static Quote Adjust(IEnumerable<PriceSegment> quoted, IReadOnlyList<Discount> discounts, DateOnly effectiveOn, IEnumerable<Period> coverage)
    {
        var adjusted = new List<PriceSegment>();
        foreach (var original in quoted)
        {
            foreach (var uncovered in BillingCalendar.Uncovered(new(original.Start, original.EndExclusive), coverage))
            {
                var cursor = uncovered.Start;
                while (cursor < uncovered.EndExclusive)
                {
                    var segmentEnd = new[] { uncovered.EndExclusive }
                        .Concat(discounts.SelectMany(x => new[] { x.StartsOn, x.EndExclusive }))
                        .Append(effectiveOn)
                        .Where(x => x > cursor)
                        .Min();
                    if (cursor < effectiveOn)
                        adjusted.Add(original with { Start = cursor, EndExclusive = segmentEnd });
                    else
                    {
                        var discount = discounts.SingleOrDefault(x => x.WithdrawnAt is null && x.StartsOn <= cursor && cursor < x.EndExclusive);
                        var months = (original.BasisEndExclusive.Year - original.BasisStart.Year) * 12 + original.BasisEndExclusive.Month - original.BasisStart.Month;
                        var price = discount is null
                            ? original.BasePrice
                            : discount.Kind == DiscountKind.Percentage
                                ? original.BasePrice * (100 - discount.Value) / 100
                                : discount.Value * months;
                        adjusted.Add(original with { Start = cursor, EndExclusive = segmentEnd, Price = price, DiscountId = discount?.Id });
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
            .SelectMany(x => new[] { x.StartsOn, x.EndExclusive })
            .Distinct()
            .ToList();
        var splitSegments = original.SelectMany(segment =>
        {
            var dates = discountBoundaries
                .Where(x => segment.Start < x && x < segment.EndExclusive)
                .Append(segment.Start)
                .Append(segment.EndExclusive)
                .Order()
                .ToArray();
            return dates.Zip(dates.Skip(1), (start, end) => segment with { Start = start, EndExclusive = end });
        }).ToList();

        static decimal Reduction(PriceSegment segment, Discount adjustment)
        {
            if (segment.Start < adjustment.StartsOn || segment.EndExclusive > adjustment.EndExclusive)
                return 0;

            var months = (segment.BasisEndExclusive.Year - segment.BasisStart.Year) * 12 + segment.BasisEndExclusive.Month - segment.BasisStart.Month;
            var reduced = adjustment.Kind == DiscountKind.Percentage
                ? segment.BasePrice * (100 - adjustment.Value) / 100
                : adjustment.Value * months;
            return Math.Max(0, segment.Price - reduced) * (segment.EndExclusive.DayNumber - segment.Start.DayNumber) / (segment.BasisEndExclusive.DayNumber - segment.BasisStart.DayNumber);
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
