using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Localization;
using Nulysa.Membership;

namespace Nulysa.Communications;

public sealed record OperationalTemplateLanguage(OperationalTemplateSource Source, bool Custom, string? Problem);
public sealed record OperationalTemplateView(MailKind Kind, IReadOnlyList<OperationalTemplateLanguage> Languages, string FallbackLanguage);
public sealed record OperationalTemplateProblem(MailKind Kind, string Language);

public sealed class OperationalTemplates(AppDbContext db, TimeProvider clock, IOptions<MembershipOptions> options, IConfiguration config)
{
    private sealed record StoredTemplates(Dictionary<string, OperationalTemplateSource> Liquid);

    // This is only for upgrading stored source from the previous syntax.
    private static readonly Regex LegacySyntax = new(
        @"\\\{\{[^{}\r\n]*\}\}|\\[\\{}]|\{\{#if ([a-z_]+)\}\}|\{\{/if\}\}|\{%",
        RegexOptions.Compiled);

    internal static readonly string[] BuiltInLanguages = OperationalTemplateCatalog.Languages;

    public async Task<IReadOnlyList<OperationalTemplateView>> List(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var saved = await db.OperationalMailTemplates.AsNoTracking().ToDictionaryAsync(x => x.Kind, ct);
        return OperationalTemplateCatalog.Kinds.Select(kind =>
        {
            var row = saved.GetValueOrDefault(kind);
            var overrides = Read(row);
            return new OperationalTemplateView(kind, BuiltInLanguages.Select(language =>
            {
                var shipped = OperationalTemplateCatalog.Default(kind, language).Source;
                var source = overrides.GetValueOrDefault(language);
                var problem = source is null ? null : Check(kind, source, out _);
                return new OperationalTemplateLanguage(source ?? shipped, source is not null, problem);
            }).ToArray(), Fallback(row));
        }).ToArray();
    }

    public async Task<IReadOnlyList<OperationalTemplateProblem>> Problems(ClaimsPrincipal actor, CancellationToken ct = default)
        => (await List(actor, ct)).SelectMany(template => template.Languages.Where(x => x.Problem is not null)
            .Select(x => new OperationalTemplateProblem(template.Kind, x.Source.Language))).ToArray();

    public static IReadOnlyList<OperationalTemplateField> Fields(MailKind kind) => OperationalTemplateCatalog.Fields(kind);

    public static MailContent Preview(MailKind kind, OperationalTemplateSource source)
    {
        var fields = Fields(kind);
        return new OperationalTemplateMarkup(source, fields, preview: true).Render(fields.ToDictionary(x => x.Name, x => (string?)x.Sample));
    }

    public Task Save(ClaimsPrincipal actor, MailKind kind, OperationalTemplateSource source, string fallbackLanguage, CancellationToken ct = default)
        => Update(actor, kind, source.Language, source, fallbackLanguage, ct);

    public Task Reset(ClaimsPrincipal actor, MailKind kind, string language, CancellationToken ct = default)
        => Update(actor, kind, language, null, null, ct);

    private async Task Update(ClaimsPrincipal actor, MailKind kind, string language, OperationalTemplateSource? source, string? fallbackLanguage, CancellationToken ct)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (!OperationalTemplateCatalog.Kinds.Contains(kind))
            throw new ValidationException("Choose an automatic mail template.");
        if (!BuiltInLanguages.Contains(language))
            throw new ValidationException("Choose a supported template language.");
        if (source is not null)
        {
            if (!BuiltInLanguages.Contains(fallbackLanguage))
                throw new ValidationException("Choose a supported fallback language.");
            _ = new OperationalTemplateMarkup(source, Fields(kind));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await DatabaseLocks.AcquireForIdentity(db, $"operational-template:{kind}", ct);
        var row = await db.OperationalMailTemplates.SingleOrDefaultAsync(x => x.Kind == kind, ct);
        var overrides = Read(row);
        if (source is null)
        {
            if (row is null)
                return;
            overrides.Remove(language);
        }
        else
        {
            if (source == OperationalTemplateCatalog.Default(kind, language).Source)
                overrides.Remove(language);
            else
                overrides[language] = source;
            if (row is null)
            {
                row = new() { Kind = kind };
                db.OperationalMailTemplates.Add(row);
            }
            row.FallbackLanguage = fallbackLanguage!;
        }

        row.Content = JsonSerializer.Serialize(new StoredTemplates(overrides));
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedBy = Authority.Id(actor);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    internal async Task<OutgoingMail> Render(MailKind kind, string preferredLanguage, string to, Dictionary<string, string?> values,
        CancellationToken ct, byte[]? pdf = null, string? fileName = null)
    {
        var row = await db.OperationalMailTemplates.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == kind, ct);
        var preferred = UiLanguages.Find(preferredLanguage)?.Code;
        var language = BuiltInLanguages.Contains(preferred) ? preferred! : Fallback(row);
        var shipped = OperationalTemplateCatalog.Default(kind, language).Markup;
        var source = Read(row).GetValueOrDefault(language);
        OperationalTemplateMarkup? custom = null;
        if (source is not null)
            Check(kind, source, out custom);

        values["association"] = config["Association:Name"] ?? "Nulysa";
        var content = (custom ?? shipped).Render(values);
        if (custom is not null && string.IsNullOrWhiteSpace(content.Subject))
            content = content with { Subject = shipped.Render(values).Subject };
        return new(to, content.Subject, content.Text, pdf, fileName, content.Html);
    }

    private string Fallback(OperationalMailTemplate? row) =>
        row is not null && BuiltInLanguages.Contains(row.FallbackLanguage) ? row.FallbackLanguage : options.Value.DefaultLanguage;

    private static string? Check(MailKind kind, OperationalTemplateSource source, out OperationalTemplateMarkup? markup)
    {
        markup = null;
        try
        {
            markup = new(source, Fields(kind));
        }
        catch (ValidationException error) { return error.Message; }
        return null;
    }

    private static Dictionary<string, OperationalTemplateSource> Read(OperationalMailTemplate? row)
    {
        if (row is null)
            return [];
        try
        {
            using var document = JsonDocument.Parse(row.Content);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var liquid = document.RootElement.TryGetProperty(nameof(StoredTemplates.Liquid), out var sources);
                var entries = (liquid ? sources : document.RootElement).Deserialize<Dictionary<string, OperationalTemplateSource>>() ?? [];
                return entries.Where(x => BuiltInLanguages.Contains(x.Key)).ToDictionary(x => x.Key, x =>
                {
                    var subject = x.Value?.Subject ?? "";
                    var body = x.Value?.Body ?? "";
                    return liquid ? new OperationalTemplateSource(x.Key, subject, body) : Upgrade(x.Key, subject, body);
                });
            }

            // Expand wrappers saved by the old editor to editable Markdown with the same wording.
            return RichMail.Read(row.Content).Where(x => BuiltInLanguages.Contains(x.Language)).ToDictionary(x => x.Language, x =>
            {
                var shipped = OperationalTemplateCatalog.Default(row.Kind, x.Language).Source;
                const string associationPrefix = "{{association}} — ";
                var defaultSubject = shipped.Subject.StartsWith(associationPrefix, StringComparison.Ordinal)
                    ? shipped.Subject[associationPrefix.Length..] : shipped.Subject;
                var subject = x.Subject.Replace("{%", "{% raw %}{%{% endraw %}", StringComparison.Ordinal)
                    .Replace("{{subject}}", defaultSubject, StringComparison.Ordinal);
                var body = MailMarkdown.ExpandOperationalDetails(x, shipped.Body);
                return new OperationalTemplateSource(x.Language, subject, body);
            });
        }
        catch (JsonException)
        {
            // Show affected languages for repair even if the stored document cannot be decoded.
            return BuiltInLanguages.ToDictionary(x => x, x => new OperationalTemplateSource(x, "", ""));
        }
    }

    private static OperationalTemplateSource Upgrade(string language, string subject, string body) =>
        new(language, Convert(subject, markdown: false), Convert(body, markdown: true));

    private static string Convert(string source, bool markdown) => LegacySyntax.Replace(source, match =>
    {
        if (match.Groups[1].Success)
            return "{% if " + match.Groups[1].Value + " %}";
        var token = match.Value;
        if (token == "{{/if}}")
            return "{% endif %}";
        if (token == "{%" || token.StartsWith(@"\{{", StringComparison.Ordinal))
            return "{% raw %}" + (token == "{%" ? token : token[1..]) + "{% endraw %}";
        if (markdown)
            return token;
        return token[1] is '{' or '}' ? "{% raw %}" + token[1] + "{% endraw %}" : token[1..];
    });
}
