using System.Globalization;
using System.Resources;

namespace Nulysa.Localization;

// English source phrases are resource keys. Format whole messages so translators can reorder arguments.
public static class UiText
{
    private static readonly ResourceManager Resources = new("Nulysa.Localization.UiStrings", typeof(UiText).Assembly);

    public static string T(string message, params object?[] arguments)
    {
        var text = Resources.GetString(message, CultureInfo.CurrentUICulture) ?? message;
        return arguments.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, arguments);
    }
}
