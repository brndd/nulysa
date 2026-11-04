using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Localization;
using Nulysa.Membership;

namespace Nulysa.Communications;

public sealed class OperationalTemplates(AppDbContext db, TimeProvider clock, IOptions<MembershipOptions> options, IConfiguration config)
{
    internal static readonly string[] BuiltInLanguages = ["en", "fi"];
    private static readonly MailKind[] Kinds =
    [
        MailKind.Verification, MailKind.Welcome, MailKind.Rejection, MailKind.DeletionWarning, MailKind.PasswordReset, MailKind.EmailChange,
        MailKind.Restoration, MailKind.Invoice, MailKind.MembershipGrace, MailKind.MembershipInactive, MailKind.BankAlert, MailKind.PasswordChanged
    ];

    public async Task<IReadOnlyList<OperationalTemplateView>> List(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var saved = await db.OperationalMailTemplates.AsNoTracking().ToDictionaryAsync(x => x.Kind, ct);
        return Kinds.Select(kind => saved.TryGetValue(kind, out var template)
            ? new OperationalTemplateView(kind, RichMail.Read(template.Content), template.FallbackLanguage, BuiltInLanguages.ToArray(), template.UpdatedAt)
            : new OperationalTemplateView(kind, [], options.Value.DefaultLanguage, BuiltInLanguages.ToArray(), null)).ToArray();
    }

    public async Task Save(ClaimsPrincipal actor, MailKind kind, IReadOnlyCollection<MailVariant> variants, string fallbackLanguage, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (!Kinds.Contains(kind))
            throw new ValidationException("Choose an automatic mail template.");
        var content = RichMail.Validate(variants, operational: true);
        var fallback = UiLanguages.Find(fallbackLanguage)?.Code;
        // Custom formatting cannot activate a language without a translation of the required operational details.
        if (content.Any(x => !BuiltInLanguages.Contains(x.Language)) || !content.Any(x => x.Language == fallback))
            throw new ValidationException("Choose a saved fallback language with operational translations.");

        var json = RichMail.Store(content);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OperationalMailTemplates" ("Kind", "Content", "FallbackLanguage", "UpdatedAt", "UpdatedBy")
            VALUES ({kind.ToString()}, CAST({json} AS jsonb), {fallback}, {clock.GetUtcNow()}, {Authority.Id(actor)})
            ON CONFLICT ("Kind") DO UPDATE SET "Content" = EXCLUDED."Content", "FallbackLanguage" = EXCLUDED."FallbackLanguage",
                "UpdatedAt" = EXCLUDED."UpdatedAt", "UpdatedBy" = EXCLUDED."UpdatedBy"
            """, ct);
    }

    public async Task Reset(ClaimsPrincipal actor, MailKind kind, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        await db.OperationalMailTemplates.Where(x => x.Kind == kind).ExecuteDeleteAsync(ct);
    }

    internal async Task<(string Language, MailVariant? Variant)> Resolve(MailKind kind, string preferredLanguage, CancellationToken ct)
    {
        var preferred = UiLanguages.Find(preferredLanguage)?.Code;
        var template = await db.OperationalMailTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == kind, ct);
        if (template is null)
            return (BuiltInLanguages.Contains(preferred) ? preferred! : options.Value.DefaultLanguage, null);
        var variants = RichMail.Read(template.Content);
        var variant = variants.SingleOrDefault(x => x.Language == preferred) ?? variants.Single(x => x.Language == template.FallbackLanguage);
        return (variant.Language, variant);
    }

    internal OutgoingMail Render(string to, string subject, string text, MailVariant? variant, byte[]? pdf = null, string? fileName = null)
    {
        var association = config["Association:Name"] ?? "Nulysa";
        if (variant is null)
            return new(to, association + " — " + subject, text, pdf, fileName);
        var content = RichMail.Render(variant, text, subject, association);
        return new(to, content.Subject, content.Text, pdf, fileName, content.Html);
    }
}
