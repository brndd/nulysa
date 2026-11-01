using Microsoft.AspNetCore.WebUtilities;

namespace Nulysa.Components;

public static class RegistryNavigation
{
    public static string ReturnUrl(string? value)
    {
        var isRegistry = value == "/admin/members" || value?.StartsWith("/admin/members?", StringComparison.Ordinal) == true;
        var isSafe = isRegistry && !value!.Any(character => char.IsControl(character) || character == '\\');
        return isSafe ? value! : "/admin/members";
    }

    public static string MemberUrl(long id, string? returnUrl, bool billing = false)
        => QueryHelpers.AddQueryString($"/admin/members/{id}" + (billing ? "/billing" : ""), "returnUrl", ReturnUrl(returnUrl));
}
