namespace Nulysa.Billing;

public sealed class BillingOptions
{
    public int DefaultIntervalMonths { get; set; } = 1;
    public int[] AllowedIntervals { get; set; } = Enumerable.Range(1, 12).ToArray();
    public int InvoiceLeadDays { get; set; } = 14;
    public int InitialMembershipGraceDays { get; set; } = 14;
    public int MembershipRenewalGraceDays { get; set; } = 7;
    public int ServicePaymentWindowDays { get; set; } = 14;
    public bool AutomaticMembershipReactivation { get; set; } = true;
    public bool PurchaseBalanceEnabled { get; set; }
    public string Iban { get; set; } = "";
    public string Bic { get; set; } = "";
    public string Bank { get; set; } = "";
    public string BusinessId { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string[] Address { get; set; } = [];
    public string TypstPath { get; set; } = "typst";
    public string? TemplateDirectory { get; set; }

    public static bool ValidIban(string value)
    {
        var iban = PaymentReference.Normalize(value);
        if (iban.Length is < 15 or > 34 || !iban[..2].All(c => c is >= 'A' and <= 'Z') ||
            !iban.Substring(2, 2).All(char.IsAsciiDigit) || !iban.All(char.IsAsciiLetterOrDigit)) return false;
        if (iban.StartsWith("FI", StringComparison.Ordinal) && (iban.Length != 18 || !iban[2..].All(char.IsAsciiDigit))) return false;
        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var number = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = (remainder * (number < 10 ? 10 : 100) + number) % 97;
        }
        return remainder == 1;
    }
}
