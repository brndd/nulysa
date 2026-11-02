using Nulysa.Billing;

namespace Nulysa.Components;

public static class BillingDateText
{
    public static string Period(DateOnly start, DateOnly endExclusive)
        => $"{start:d MMM yyyy} through {endExclusive.AddDays(-1):d MMM yyyy}";

    public static string PurchasePeriod(InvoiceLine line)
        => line.Benefit == Benefit.OneOff ? $"Pay by {line.ChargeDueOn:d MMM yyyy}"
            : Period(line.StartsOn, line.EndExclusive);
}
