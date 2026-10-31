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
        if (months is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(months));
        var difference = (date.Year - anchor.Year) * 12 + date.Month - anchor.Month;
        var step = (int)Math.Floor((decimal)difference / months);
        var start = anchor.AddMonths(step * months);
        if (start > date) start = anchor.AddMonths(--step * months);
        return new(start, anchor.AddMonths((step + 1) * months));
    }

    public static Period Bridge(DateOnly anchor, int months, DateOnly start, bool mergeShort)
    {
        var period = Containing(anchor, months, start);
        var end = period.End;
        if (mergeShort && end.DayNumber - start.DayNumber < 7) end = Containing(anchor, months, end).End;
        return new(start, end);
    }

    public static List<Period> Uncovered(Period period, IEnumerable<Period> coverage)
    {
        var result = new List<Period>();
        var start = period.Start;
        foreach (var covered in coverage.OrderBy(x => x.Start))
        {
            if (covered.End <= start || covered.Start >= period.End) continue;
            if (covered.Start > start) result.Add(new(start, covered.Start));
            start = covered.End > start ? covered.End : start;
            if (start >= period.End) break;
        }
        if (start < period.End) result.Add(new(start, period.End));
        return result;
    }
}

public static class Pricing
{
    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static Quote Calculate(DateOnly anchor, int months, Period period, IReadOnlyList<FeePrice> prices,
        IReadOnlyList<Discount> discounts, IEnumerable<Period> coverage)
    {
        if (period.End <= period.Start) throw new ValidationException("A billing period must have positive length.");
        var segments = new List<PriceSegment>();
        foreach (var uncovered in BillingCalendar.Uncovered(period, coverage))
        {
            var cursor = uncovered.Start;
            while (cursor < uncovered.End)
            {
                var basis = BillingCalendar.Containing(anchor, months, cursor);
                var price = prices.Where(x => x.EffectiveOn <= cursor).OrderByDescending(x => x.EffectiveOn).FirstOrDefault()
                    ?? throw new ValidationException("Configure a price effective before this billing period first.");
                var discount = discounts.Where(x => x.WithdrawnAt is null && x.StartsOn <= cursor && cursor < x.EndsOn).SingleOrDefault();
                var end = new[] { uncovered.End, basis.End }
                    .Concat(prices.Where(x => x.EffectiveOn > cursor).Select(x => x.EffectiveOn))
                    .Concat(discounts.Where(x => x.WithdrawnAt is null).SelectMany(x => new[] { x.StartsOn, x.EndsOn }).Where(x => x > cursor)).Min();
                var full = price.MonthlyAmount * months;
                var reduced = discount is null ? full : discount.Kind == DiscountKind.Percentage ? full * (100 - discount.Value) / 100 : discount.Value * months;
                segments.Add(new(cursor, end, basis.Start, basis.End, full, reduced, discount?.Id));
                cursor = end;
            }
        }
        return FromSegments(segments);
    }

    public static Quote FromSegments(IEnumerable<PriceSegment> segments)
    {
        var list = segments.ToList();
        return new(Round(list.Sum(x => x.Price * (x.End.DayNumber - x.Start.DayNumber) / (x.BasisEnd.DayNumber - x.BasisStart.DayNumber))), list);
    }

    public static Quote Slice(IEnumerable<PriceSegment> segments, DateOnly start, IEnumerable<Period>? coverage = null)
        => FromSegments(segments.Where(x => x.End > start).SelectMany(x => BillingCalendar.Uncovered(
            new(x.Start < start ? start : x.Start, x.End), coverage ?? []).Select(p => x with { Start = p.Start, End = p.End })));

    public static Quote Adjust(IEnumerable<PriceSegment> quoted, IReadOnlyList<Discount> discounts, DateOnly effectiveOn, IEnumerable<Period> coverage)
    {
        var result = new List<PriceSegment>();
        foreach (var original in quoted)
        {
            foreach (var uncovered in BillingCalendar.Uncovered(new(original.Start, original.End), coverage))
            {
                var cursor = uncovered.Start;
                while (cursor < uncovered.End)
                {
                    var end = new[] { uncovered.End }.Concat(discounts.SelectMany(x => new[] { x.StartsOn, x.EndsOn }))
                        .Append(effectiveOn).Where(x => x > cursor).Min();
                    if (cursor < effectiveOn) result.Add(original with { Start = cursor, End = end });
                    else
                    {
                        var discount = discounts.SingleOrDefault(x => x.WithdrawnAt is null && x.StartsOn <= cursor && cursor < x.EndsOn);
                        var months = (original.BasisEnd.Year - original.BasisStart.Year) * 12 + original.BasisEnd.Month - original.BasisStart.Month;
                        var price = discount is null ? original.BasePrice : discount.Kind == DiscountKind.Percentage
                            ? original.BasePrice * (100 - discount.Value) / 100 : discount.Value * months;
                        result.Add(original with { Start = cursor, End = end, Price = price, DiscountId = discount?.Id });
                    }
                    cursor = end;
                }
            }
        }
        return FromSegments(result);
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
