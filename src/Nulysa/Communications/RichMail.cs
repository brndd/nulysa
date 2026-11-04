using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nulysa.Localization;

namespace Nulysa.Communications;

// The editor exchanges this document, never raw HTML. Both outputs are rendered on the server.
public enum MailBlockKind { Paragraph, Heading, Bullet, Quote, OperationalDetails }
public sealed record MailRun(string Text, bool Bold = false, bool Italic = false, string? Link = null);
public sealed record MailBlock(MailBlockKind Kind, MailRun[] Runs);
public sealed record MailVariant(string Language, string Subject, MailBlock[] Body)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Markdown { get; init; }
}
public sealed record MailContent(string Subject, string Text, string Html);

internal static class RichMail
{
    public const int SubjectLimit = 200;
    private const int BodyLimit = 32_000;

    public static MailVariant[] Validate(IReadOnlyCollection<MailVariant> variants, bool allowEmptySubject = false, bool operational = false)
    {
        if (variants.Count == 0 || variants.Count > UiLanguages.Supported.Count)
            throw new ValidationException("Provide at least one supported message language.");

        // Expanded wrappers can carry a full legacy body plus the operational section.
        var bodyLimit = operational ? 36_000 : BodyLimit;
        var blockLimit = operational ? 110 : 100;
        var sourceLimit = operational ? MailMarkdown.OperationalSourceLimit : MailMarkdown.SourceLimit;
        var languages = new HashSet<string>(StringComparer.Ordinal);
        return variants.Select(variant =>
        {
            if (variant is null)
                throw new ValidationException("Provide a message for every saved variant.");
            var language = UiLanguages.Find(variant.Language)?.Code;
            if (language is null || !languages.Add(language))
                throw new ValidationException("Provide only one variant per supported language.");
            if ((!allowEmptySubject && string.IsNullOrWhiteSpace(variant.Subject)) || variant.Subject.Length > SubjectLimit || variant.Subject.Any(char.IsControl))
                throw new ValidationException("Enter a single-line subject up to 200 characters.");
            if (variant.Markdown is { } markdown && markdown.Length > sourceLimit)
                throw new ValidationException("The Markdown source is too long.");
            if (variant.Body is not { Length: > 0 } || variant.Body.Length > blockLimit || variant.Body.Any(block => block is null ||
                !Enum.IsDefined(block.Kind) || block.Kind == MailBlockKind.OperationalDetails ||
                block.Runs is not { Length: > 0 and <= 100 } || block.Runs.All(run => string.IsNullOrWhiteSpace(run?.Text)) ||
                block.Runs.Any(run => run?.Text is null || run.Text.Length > bodyLimit || run.Text.Contains('\0') ||
                    (run.Link is not null && !SafeLink(run.Link)))))
                throw new ValidationException("Use supported formatting and absolute HTTPS, HTTP or mailto links.");
            if (variant.Body.SelectMany(block => block.Runs).Sum(run => (long)run.Text.Length + (run.Link?.Length ?? 0)) > bodyLimit)
                throw new ValidationException("The message body is too long.");
            return variant with { Language = language, Subject = variant.Subject.Trim() };
        }).OrderBy(variant => variant.Language, StringComparer.Ordinal).ToArray();
    }

    public static string Store(MailVariant[] variants) => JsonSerializer.Serialize(variants);
    public static MailVariant[] Read(string json) => JsonSerializer.Deserialize<MailVariant[]>(json)!;

    public static MailContent Render(MailVariant variant)
    {
        var text = new StringBuilder();
        var html = new StringBuilder();
        var list = false;
        foreach (var block in variant.Body)
        {
            if (list && block.Kind != MailBlockKind.Bullet)
            {
                html.Append("</ul>");
                list = false;
            }
            if (text.Length > 0)
                text.Append(list ? "\n" : "\n\n");
            var tag = block.Kind switch
            {
                MailBlockKind.Heading => "h2",
                MailBlockKind.Bullet => "li",
                MailBlockKind.Quote => "blockquote",
                _ => "p"
            };
            if (block.Kind == MailBlockKind.Bullet)
            {
                if (!list)
                    html.Append("<ul>");
                list = true;
                text.Append("- ");
            }
            html.Append('<').Append(tag).Append('>');
            foreach (var run in block.Runs)
            {
                var formatted = EncodeLines(run.Text);
                text.Append(run.Text);
                if (run.Bold)
                    formatted = $"<strong>{formatted}</strong>";
                if (run.Italic)
                    formatted = $"<em>{formatted}</em>";
                if (run.Link is { } href && SafeLink(href))
                {
                    formatted = $"<a href=\"{WebUtility.HtmlEncode(href)}\" rel=\"noopener noreferrer\">{formatted}</a>";
                    if (href != run.Text)
                        text.Append(" (").Append(href).Append(')');
                }
                html.Append(formatted);
            }
            html.Append("</").Append(tag).Append('>');
        }
        if (list)
            html.Append("</ul>");

        return new(variant.Subject, text.ToString(), html.ToString());
    }

    public static MailContent WithUnsubscribe(MailContent content, string language, string? url = null)
    {
        var label = UiText.ForCulture("Unsubscribe from newsletters", CultureInfo.GetCultureInfo(language));
        return content with
        {
            Text = content.Text + "\n\n" + label + (url is null ? "" : ": " + url),
            Html = content.Html + (url is null ? $"<p>{label}</p>" :
                $"<p><a href=\"{WebUtility.HtmlEncode(url)}\" rel=\"noopener noreferrer\">{label}</a></p>")
        };
    }

    private static string EncodeLines(string value) => WebUtility.HtmlEncode(value).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br>");
    internal static bool SafeLink(string href) => href.Length is > 0 and <= 2048 && href.All(c => !char.IsControl(c) && !char.IsWhiteSpace(c) && c != '\\') &&
        Uri.TryCreate(href, UriKind.Absolute, out var uri) &&
        ((uri.Scheme is "https" or "http" && uri.Host.Length > 0 && uri.UserInfo.Length == 0) ||
            (uri.Scheme == "mailto" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath.Length > 0 &&
                !href.Contains('%')));
}
