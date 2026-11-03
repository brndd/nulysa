using Nulysa.Billing;
using static Nulysa.Localization.UiText;

namespace Nulysa.Components;

public static class BillingDateText
{
    public static string Period(DateOnly start, DateOnly endExclusive)
        => T("{0:d MMM yyyy} through {1:d MMM yyyy}", start, endExclusive.AddDays(-1));

    public static string PurchasePeriod(InvoiceLine line)
        => line.Benefit == Benefit.OneOff ? T("Pay by {0:d MMM yyyy}", line.ChargeDueOn)
            : Period(line.StartsOn, line.EndExclusive);
}
