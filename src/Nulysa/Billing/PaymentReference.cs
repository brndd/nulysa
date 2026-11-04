using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;

namespace Nulysa.Billing;

internal enum PaymentReferenceKind { Recurring = 1, PurchaseDeposit = 2, OneOff = 3 }

public static class PaymentReference
{
    internal const string SequenceName = "payment_reference_numbers";

    internal static async Task<string> Allocate(AppDbContext db, PaymentReferenceKind kind, CancellationToken ct)
    {
        var number = await db.Database.SqlQuery<long>($"SELECT nextval({SequenceName}::regclass) AS \"Value\"").SingleAsync(ct);
        return Complete(string.Create(CultureInfo.InvariantCulture, $"{(int)kind}{number:D4}"));
    }

    public static string Complete(string value)
    {
        int[] weights = [7, 3, 1];
        var sum = value.Reverse().Select((digit, i) => (digit - '0') * weights[i % 3]).Sum();
        return value + ((10 - sum % 10) % 10).ToString(CultureInfo.InvariantCulture);
    }

    public static string Normalize(string value) => string.Concat(value.Where(x => !char.IsWhiteSpace(x))).ToUpperInvariant();
}
