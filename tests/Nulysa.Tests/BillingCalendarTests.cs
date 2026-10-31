using Nulysa.Billing;

namespace Nulysa.Tests;

[Trait("Group", "CAL")]
public sealed class BillingCalendarTests
{
    [Fact]
    public void Overlapping_discount_credits_follow_the_days_actually_compensated_by_each_prior_credit()
    {
        var original = new[] { new PriceSegment(new(2026, 6, 1), new(2027, 6, 1), new(2026, 6, 1), new(2027, 6, 1), 120, 120, null) };
        var july = new Discount { StartsOn = new(2026, 7, 1), EndsOn = new(2026, 8, 1), Value = 50 };
        var combined = new Discount { StartsOn = new(2026, 7, 1), EndsOn = new(2026, 9, 1), Value = 50 };

        Assert.Equal(5.10m, Pricing.FutureDiscountCredit(original, combined, [(july, 5.10m)]));
        var julyFree = new Discount { StartsOn = july.StartsOn, EndsOn = july.EndsOn, Value = 100 };

        Assert.Equal(5.09m, Pricing.FutureDiscountCredit(original, julyFree, [(july, 5.10m), (combined, 5.10m)]));
        Assert.Equal(0m, Pricing.FutureDiscountCredit(original, combined, [(july, 5.10m), (combined, 5.10m)]));
    }

    [Theory]
    [InlineData(2025, 28)]
    [InlineData(2024, 29)]
    public void Month_end_returns_to_intended_day_and_quarters_keep_the_joining_phase(int year, int februaryEnd)
    {
        var anchor = new DateOnly(year, 1, 31);

        Assert.Equal(new Period(anchor, new(year, 2, februaryEnd)), BillingCalendar.Containing(anchor, 1, anchor));
        Assert.Equal(new Period(new(year, 2, februaryEnd), new(year, 3, 31)), BillingCalendar.Containing(anchor, 1, new(year, 2, februaryEnd)));
        Assert.Equal(new Period(new(year, 7, 12), new(year, 9, 12)), BillingCalendar.Bridge(new(year, 3, 12), 3, new(year, 7, 12), true));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(7, 8)]
    public void Due_dates_are_inclusive_and_grace_days_come_after_them(int graceDays, int expectedOffset)
    {
        var due = new DateOnly(2026, 6, 20);

        Assert.Equal(due.AddDays(expectedOffset), BillingCalendar.ExclusiveExpiry(due, graceDays));
    }

    [Fact]
    public void Actual_days_discount_splits_final_rounding_and_short_bridges_are_independent()
    {
        var anchor = new DateOnly(2026, 3, 12);
        var prices = new[] { new FeePrice { EffectiveOn = new(2000, 1, 1), MonthlyAmount = 10 } };
        var quote = Pricing.Calculate(anchor, 1, new(new(2026, 3, 27), new(2026, 4, 12)), prices, [], []);

        Assert.Equal(5.16m, quote.Amount);
        var discount = new Discount { StartsOn = anchor, EndsOn = new(2026, 3, 27), Kind = DiscountKind.Percentage, Value = 50 };
        quote = Pricing.Calculate(anchor, 1, new(anchor, new(2026, 4, 12)), prices, [discount], []);

        Assert.Equal(7.58m, quote.Amount);
        Assert.Equal(2, quote.Segments.Count);
        Assert.Equal(1.01m, Pricing.Round(1.005m));
        Assert.Equal(-1.01m, Pricing.Round(-1.005m));
        var shortPeriod = BillingCalendar.Bridge(new(2026, 3, 1), 1, new(2026, 3, 28), true);

        Assert.Equal(new DateOnly(2026, 5, 1), shortPeriod.End);
        var merged = Pricing.Calculate(new(2026, 3, 1), 1, shortPeriod, [new FeePrice { EffectiveOn = new(2000, 1, 1), MonthlyAmount = 30 }], [], []);

        Assert.Equal(33.87m, merged.Amount);
        Assert.Equal(28m, Pricing.Slice(merged.Segments, new(2026, 4, 3)).Amount);
        Assert.Equal(new DateOnly(2026, 5, 1), Pricing.Slice(merged.Segments, new(2026, 4, 3)).Segments[^1].End);
        var uncovered = BillingCalendar.Uncovered(new(new(2026, 1, 1), new(2026, 4, 1)), [new(new(2026, 1, 1), new(2026, 2, 1)), new(new(2026, 3, 1), new(2026, 4, 1))]);

        Assert.Equal(new Period(new(2026, 2, 1), new(2026, 3, 1)), Assert.Single(uncovered));
    }
}
