using Microsoft.AspNetCore.WebUtilities;

namespace Nulysa.Components;

public enum MemberSection { Overview, Details, Billing, Actions }

public static class RegistryNavigation
{
    public static readonly IReadOnlyList<(MemberSection Section, string Label)> Sections =
    [
        (MemberSection.Overview, "Account overview"), (MemberSection.Details, "Details"),
        (MemberSection.Billing, "Billing details"), (MemberSection.Actions, "Financial actions")
    ];

    public static string ReturnUrl(string? value)
    {
        var isRegistry = value == "/admin/members" || value?.StartsWith("/admin/members?", StringComparison.Ordinal) == true;
        return isRegistry && IsSafe(value) ? value! : "/admin/members";
    }

    public static string MemberUrl(long id, string? returnUrl, MemberSection section = MemberSection.Overview)
    {
        var suffix = section switch
        {
            MemberSection.Details => "/details",
            MemberSection.Billing => "/billing",
            MemberSection.Actions => "/actions",
            _ => ""
        };
        return QueryHelpers.AddQueryString($"/admin/members/{id}{suffix}", "returnUrl", ReturnUrl(returnUrl));
    }

    public static string BackTo(long id, string? value, string? returnUrl, MemberSection fallback = MemberSection.Actions)
    {
        var root = $"/admin/members/{id}";
        var path = AdminNavigation.Path(value ?? "");
        var isMemberPage = path == root || path == root + "/details" || path == root + "/billing" || path == root + "/actions";
        var isMemberBill = path.StartsWith(root + "/billing/invoices/", StringComparison.Ordinal)
            && long.TryParse(path[(root.Length + "/billing/invoices/".Length)..], out _);
        return (isMemberPage || isMemberBill) && IsSafe(value) ? value! : MemberUrl(id, returnUrl, fallback);
    }

    public static string ActionUrl(long id, string action, string? returnUrl, string? backTo = null)
        => QueryHelpers.AddQueryString($"/admin/members/{id}/actions/{action}", new Dictionary<string, string?>
        {
            ["returnUrl"] = ReturnUrl(returnUrl),
            ["backTo"] = BackTo(id, backTo, returnUrl)
        });

    public static string BillUrl(long id, long invoiceId, string? returnUrl, string? backTo)
        => QueryHelpers.AddQueryString($"/admin/members/{id}/billing/invoices/{invoiceId}", new Dictionary<string, string?>
        {
            ["returnUrl"] = ReturnUrl(returnUrl),
            ["backTo"] = BackTo(id, backTo, returnUrl, MemberSection.Billing)
        });

    private static bool IsSafe(string? value) => value is not null && value.Length <= 8192
        && !Uri.UnescapeDataString(value).Any(character => char.IsControl(character) || character == '\\');
}
