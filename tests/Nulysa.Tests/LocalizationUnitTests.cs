using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using Microsoft.AspNetCore.Http;
using Nulysa.Localization;

namespace Nulysa.Tests;

public sealed class LocalizationUnitTests
{
    [Theory]
    [InlineData("fi-FI", "fi", "3.12.2026", "Voimassa 3.12.2026 asti", "1.12.2026 – 3.12.2026")]
    [InlineData("en-GB", "en", "3 Dec 2026", "Valid through 3 Dec 2026", "1 Dec 2026 through 3 Dec 2026")]
    public void Display_dates_use_local_formats_and_periods_keep_the_inclusive_last_day(
        string culture, string uiCulture, string date, string standing, string period)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new(culture);
            CultureInfo.CurrentUICulture = new(uiCulture);
            var lastDay = new DateOnly(2026, 12, 3);
            Assert.Equal(date, UiText.Date(lastDay));
            Assert.Equal(standing, UiText.T("Covered through {0:d MMM yyyy}", lastDay));
            Assert.Equal(period, Nulysa.Components.BillingDateText.Period(new(2026, 12, 1), lastDay.AddDays(1)));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("fi-FI", "1,25", 1.25)]
    [InlineData("en-GB", "1.25", 1.25)]
    [InlineData("fi-FI", "-1,25", -1.25)]
    public void Money_values_use_the_selected_decimal_separator(string culture, string text, decimal expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new(culture);
            Assert.True(MoneyAmount.TryParse(text, CultureInfo.InvariantCulture, out var amount));
            Assert.Equal(expected, amount.Value);
            Assert.Equal(expected.ToString("0.##", CultureInfo.CurrentCulture), amount.ToString());
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("en-GB", "1,25")]
    [InlineData("fi-FI", "1.25")]
    [InlineData("fi-FI", "1.234,56")]
    [InlineData("en-GB", "1,234.56")]
    [InlineData("en-GB", "1e3")]
    public void Ambiguous_grouped_or_exponential_amounts_are_rejected(string culture, string text)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new(culture);
            Assert.False(MoneyAmount.TryParse(text, null, out _));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }


    [Theory]
    [InlineData("fi", "fi-FI", "fi")]
    [InlineData("fi-SE", "fi-FI", "fi")]
    [InlineData("en-US", "en-GB", "en")]
    [InlineData("sv;q=1,fi;q=0.5,en;q=0", "fi-FI", "fi")]
    public async Task Browser_language_resolves_matching_UI_and_formatting_cultures(string header, string culture, string uiCulture)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.AcceptLanguage = header;
        var result = await new HeaderCultureProvider().DetermineProviderCultureResult(context);
        Assert.Equal(culture, result!.Cultures.Single().Value);
        Assert.Equal(uiCulture, result.UICultures.Single().Value);
    }

    [Fact]
    public void Every_Finnish_resource_has_the_same_format_arguments_as_its_English_source()
    {
        var resources = new ResourceManager("Nulysa.Localization.UiStrings", typeof(UiText).Assembly);
        var english = resources.GetResourceSet(new CultureInfo("en"), true, true)!;
        var finnish = resources.GetResourceSet(new CultureInfo("fi"), true, false)!;
        foreach (DictionaryEntry entry in english)
        {
            var translated = finnish.GetString((string)entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(translated), (string)entry.Key);
            Assert.Equal(CompositeFormat.Parse((string)entry.Value!).MinimumArgumentCount, CompositeFormat.Parse(translated!).MinimumArgumentCount);
        }
    }
    [Fact]
    public void Finnish_validation_and_identity_errors_use_resources_and_money_limits_are_preserved()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new("fi-FI");
            CultureInfo.CurrentUICulture = new("fi");
            Assert.Equal("Kenttä Uusi salasana on pakollinen.", ValidationText.Translate("The NewPassword field is required."));
            Assert.Equal("Salasanan on oltava vähintään 6 merkkiä pitkä.", new LocalizedIdentityErrors().PasswordTooShort(6).Description);
            Assert.Equal("Kenttä Viesti saa sisältää enintään 10 merkkiä.", ValidationText.Translate("The field Message must be a string with a maximum length of '10'."));
            var model = new Nulysa.Components.Pages.Billing.TransferInput { Amount = 1_000_000_000m };
            var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model), errors, true));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

}
