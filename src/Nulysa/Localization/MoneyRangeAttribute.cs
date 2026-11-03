using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Nulysa.Localization;

[AttributeUsage(AttributeTargets.Property)]
public sealed class MoneyRangeAttribute(string minimum, string maximum) : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var min = decimal.Parse(minimum, CultureInfo.InvariantCulture);
        var max = decimal.Parse(maximum, CultureInfo.InvariantCulture);
        return value is MoneyAmount amount && amount.Value >= min && amount.Value <= max ? ValidationResult.Success
            : new ValidationResult(UiText.T("The {0} field must be between {1} and {2}.", UiText.T(validationContext.DisplayName), min, max), [validationContext.MemberName!]);
    }
}
