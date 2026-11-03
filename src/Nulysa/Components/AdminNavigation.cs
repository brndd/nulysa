namespace Nulysa.Components;

public sealed record AdminDestination(string Key, string LabelKey, string Href)
{
    public string Label => Nulysa.Localization.UiText.T(LabelKey);
}
public sealed record AdminSection(string Key, string LabelKey, IReadOnlyList<AdminDestination> Pages)
{
    public string Label => Nulysa.Localization.UiText.T(LabelKey);
}
public sealed record BreadcrumbItem(string Label, string? Href = null);

public static class AdminNavigation
{
    public static readonly AdminDestination Home = new("attention", "Needs attention", "/admin");
    public static readonly IReadOnlyList<AdminSection> Sections =
    [
        new("members", "Members",
        [
            new("registry", "Member registry", "/admin/members"),
            new("applications", "Application review", "/admin/applications")
        ]),
        new("finances", "Finances",
        [
            new("payments", "Payment review", "/admin/payments"),
            new("import", "Manual import", "/admin/payments/import"),
            new("accounting", "Accounting and exports", "/admin/payments/accounting"),
            new("retained", "Retained balances", "/admin/payments/retained-balances")
        ]),
        new("communications", "Communications", [new("mail", "Mail problems", "/admin/mail-attention")]),
        new("settings", "Association settings",
        [
            new("fees", "Fees and prices", "/admin/settings/fees"),
            new("services", "Services", "/admin/settings/services")
        ])
    ];

    public static string Path(string uri) => uri.Split('?', '#')[0].TrimEnd('/');

    public static AdminDestination? Resolve(string path)
    {
        path = Path(path);
        if (path.Equals(Home.Href, StringComparison.OrdinalIgnoreCase))
            return Home;
        // More specific destinations win before their parent record routes.
        return Sections.SelectMany(section => section.Pages)
            .OrderByDescending(page => page.Href.Length)
            .FirstOrDefault(page => path.Equals(page.Href, StringComparison.OrdinalIgnoreCase) || path.StartsWith(page.Href + "/", StringComparison.OrdinalIgnoreCase));
    }

    public static AdminSection? Section(string key) => Sections.SingleOrDefault(section => section.Pages.Any(page => page.Key == key));
}
