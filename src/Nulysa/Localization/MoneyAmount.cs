using System.Globalization;

namespace Nulysa.Localization;

// UI form value only. Domain commands continue to accept decimal amounts.
// No grouping separators: a comma must never silently turn 1,25 into 125.
public readonly record struct MoneyAmount(decimal Value) : IParsable<MoneyAmount>, IFormattable
{
    public static implicit operator decimal(MoneyAmount amount) => amount.Value;
    public static implicit operator MoneyAmount(decimal value) => new(value);
    public override string ToString() => Value.ToString("0.##", CultureInfo.CurrentCulture);
    public string ToString(string? format, IFormatProvider? provider) => Value.ToString(format, provider);
    public string ToString(string format) => Value.ToString(format, CultureInfo.CurrentCulture);
    public string ToString(IFormatProvider? provider) => Value.ToString(provider);
    public static MoneyAmount Parse(string text, IFormatProvider? provider) => TryParse(text, provider, out var result)
        ? result : throw new FormatException(UiText.T("Enter an amount using the selected language's decimal separator, without thousands separators."));
    public static bool TryParse(string? text, IFormatProvider? provider, out MoneyAmount result)
    {
        var valid = decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
            CultureInfo.CurrentCulture, out var value);
        result = new(value);
        return valid;
    }
}
