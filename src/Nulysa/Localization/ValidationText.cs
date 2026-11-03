using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Filters;
using static Nulysa.Localization.UiText;

namespace Nulysa.Localization;

public static partial class ValidationText
{
    private static string Field(string name) => name.Split('.')[^1];

    public static string Translate(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return T("Check the submitted value.");
        var required = RequiredField().Match(message);
        if (required.Success)
            return T("The {0} field is required.", T(Field(required.Groups[1].Value)));
        var email = EmailField().Match(message);
        if (email.Success)
            return T("Enter a valid email address.");
        var mapping = InvalidField().Match(message);
        if (mapping.Success)
            return T("Check the value for {0}.", T(Field(mapping.Groups[1].Value)));
        var length = MaximumLength().Match(message);
        if (length.Success)
            return T("The {0} field must be at most {1} characters.", T(Field(length.Groups[1].Value)), length.Groups[2].Value);
        var range = Range().Match(message);
        if (range.Success)
            return T("The {0} field must be between {1} and {2}.", T(Field(range.Groups[1].Value)), range.Groups[2].Value, range.Groups[3].Value);
        var comparison = Comparison().Match(message);
        if (comparison.Success)
            return T("The password confirmation does not match.");
        return T(message);
    }

    [GeneratedRegex(@"^The field (.+) must be a string with a maximum length of '(\d+)'\.$")]
    private static partial Regex MaximumLength();
    [GeneratedRegex(@"^The field (.+) must be between (.+) and (.+)\.$")]
    private static partial Regex Range();
    [GeneratedRegex(@"^'.+' and '.+' do not match\.$")]
    private static partial Regex Comparison();
    [GeneratedRegex(@"^The (.+) field is required\.$")]
    private static partial Regex RequiredField();
    [GeneratedRegex(@"^The (.+) field is not a valid e-mail address\.$")]
    private static partial Regex EmailField();
    [GeneratedRegex(@"^The value '.+' is not valid for '(.+)'\.$")]
    private static partial Regex InvalidField();
}

public sealed class LocalizePageErrors : IPageFilter
{
    public void OnPageHandlerSelected(PageHandlerSelectedContext context) { }
    public void OnPageHandlerExecuting(PageHandlerExecutingContext context) { }
    public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
    {
        foreach (var state in context.ModelState.Values)
        {
            var errors = state.Errors.ToArray();
            state.Errors.Clear();
            foreach (var error in errors)
                state.Errors.Add(ValidationText.Translate(error.ErrorMessage));
        }
    }
}
