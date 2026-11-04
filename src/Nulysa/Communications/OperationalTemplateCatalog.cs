namespace Nulysa.Communications;

internal static class OperationalTemplateCatalog
{
    private static readonly OperationalTemplateField Association = new("association", "Association name", "Example Hackerspace");
    private static readonly OperationalTemplateField Billing = Url("billing_url", "Billing page", "billing");
    private static readonly Dictionary<MailKind, OperationalTemplateField[]> Contracts = new()
    {
        [MailKind.Verification] = [Url("verification_url", "Email verification link", "apply/verify?id=example"), Required("verification_expires_at", "Verification expiry", "2026-11-09 18:00 +02:00")],
        [MailKind.Welcome] = [Url("password_url", "Set password link", "Account/SetPassword?id=example"), Billing],
        [MailKind.Rejection] = [Optional("rejection_reason", "Rejection explanation", "The association is currently at capacity.")],
        [MailKind.DeletionWarning] = [Required("deletion_eligible_at", "Earliest deletion time", "2026-12-04 18:00 +02:00"),
            Required("deletion_window_start", "Typical deletion window start", "2026-12-04"), Required("deletion_window_end", "Typical deletion window end", "2026-12-18"),
            Optional("unspent_funds", "Unspent funds", "42.00 EUR")],
        [MailKind.PasswordReset] = [Url("password_url", "Set password link", "Account/SetPassword?id=example")],
        [MailKind.EmailChange] = [Url("confirmation_url", "New address confirmation link", "Account/ConfirmEmail?id=example")],
        [MailKind.Restoration] = [],
        [MailKind.Invoice] = [new("invoice_number", "Payment request number", "1234"), Billing],
        [MailKind.MembershipGrace] = [Required("benefits_until", "Last day of member benefits", "2026-12-04"), Billing],
        [MailKind.MembershipInactive] = [Url("services_url", "Member services page", "services")],
        [MailKind.BankAlert] = [Url("payments_url", "Payment administration page", "admin/payments")],
        [MailKind.PasswordChanged] = [Required("password_changed_at", "Password change time", "2026-11-04 18:00 +02:00"),
            Url("password_reset_url", "Request a password reset", "Account/ForgotPassword")]
    };

    public static readonly string[] Languages = ["en", "fi"];
    public static IEnumerable<MailKind> Kinds => Contracts.Keys;
    private static readonly Dictionary<(MailKind Kind, string Language), (OperationalTemplateSource Source, OperationalTemplateMarkup Markup)> Defaults =
        (from kind in Kinds from language in Languages select (kind, language)).ToDictionary(key => key, key => Load(key.kind, key.language));

    public static IReadOnlyList<OperationalTemplateField> Fields(MailKind kind) => [Association, .. Contracts[kind]];
    public static (OperationalTemplateSource Source, OperationalTemplateMarkup Markup) Default(MailKind kind, string language) => Defaults[(kind, language)];

    public static void ValidateDefaults() => _ = Defaults.Count;

    private static (OperationalTemplateSource, OperationalTemplateMarkup) Load(MailKind kind, string language)
    {
        var name = $"Nulysa.Communications.Templates.{kind}.{language}.md";
        using var stream = typeof(OperationalTemplateCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing automatic mail template {name}.");
        using var reader = new StreamReader(stream);
        const string prefix = "Subject: ";
        var header = reader.ReadLine();
        if (header is null || !header.StartsWith(prefix, StringComparison.Ordinal) || reader.ReadLine() != "")
            throw new InvalidOperationException($"Expected a Subject header and blank line in {name}.");
        var source = new OperationalTemplateSource(language, header[prefix.Length..], reader.ReadToEnd().TrimEnd());
        try
        {
            return (source, new(source, Fields(kind)));
        }
        catch (System.ComponentModel.DataAnnotations.ValidationException error)
        {
            throw new InvalidOperationException($"Invalid automatic mail template {name}: {error.Message}", error);
        }
    }

    private static OperationalTemplateField Url(string name, string label, string path)
        => new(name, label, "https://example.org/" + path, Link: true, Required: true);

    private static OperationalTemplateField Required(string name, string label, string sample)
        => new(name, label, sample, Required: true);

    private static OperationalTemplateField Optional(string name, string label, string sample)
        => new(name, label, sample, Optional: true);
}
