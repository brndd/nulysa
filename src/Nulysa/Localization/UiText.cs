using System.Globalization;
using System.Resources;

namespace Nulysa.Localization;

// Original English phrases are stable resource keys, including for domain errors and stored feedback.
// Edit resource values for copy changes. Format whole messages so translators can reorder arguments.
public static class UiText
{
    private static readonly ResourceManager Resources = new("Nulysa.Localization.UiStrings", typeof(UiText).Assembly);

    public static string Date(DateOnly date) => T("{0:d MMM yyyy}", date);

    public static string T(string message, params object?[] arguments)
    {
        var text = Resources.GetString(message, CultureInfo.CurrentUICulture) ?? message;
        return arguments.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, arguments);
    }
}
