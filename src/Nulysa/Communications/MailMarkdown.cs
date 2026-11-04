using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Nulysa.Localization;

namespace Nulysa.Communications;

public static class MailMarkdown
{
    internal const string OperationalDetails = "{{operational_details}}";
    public const int SourceLimit = 40_000;
    // Automatic templates can include reconstructed Markdown escapes and expanded operational details.
    public const int OperationalSourceLimit = 128_000;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation().UsePipeTables().UseTaskLists().Build();

    public static MailVariant Parse(string language, string subject, string source)
        => Parse(language, subject, source, allowEmptySubject: false);

    internal static MailVariant Parse(string language, string subject, string source, bool allowEmptySubject, bool operational = false)
    {
        if (source.Length > (operational ? OperationalSourceLimit : SourceLimit))
            throw new ValidationException("The Markdown source is too long.");

        var blocks = new List<MailBlock>();
        foreach (var block in Markdown.Parse(source, Pipeline))
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    blocks.Add(Convert(paragraph, MailBlockKind.Paragraph));
                    break;
                case HeadingBlock heading:
                    blocks.Add(Convert(heading, MailBlockKind.Heading));
                    break;
                case QuoteBlock { Count: 1 } quote when quote[0] is ParagraphBlock paragraph:
                    blocks.Add(Convert(paragraph, MailBlockKind.Quote));
                    break;
                case ListBlock { IsOrdered: false } list:
                    foreach (var item in list)
                    {
                        if (item is not ListItemBlock { Count: 1 } entry || entry[0] is not ParagraphBlock paragraph)
                            throw Unsupported(item);
                        blocks.Add(Convert(paragraph, MailBlockKind.Bullet));
                    }
                    break;
                default:
                    throw Unsupported(block);
            }
        }
        return RichMail.Validate([new(language, subject, [.. blocks]) { Markdown = source }], allowEmptySubject: allowEmptySubject, operational: operational)[0];
    }

    public static MailContent Preview(MailVariant variant, bool newsletter = false)
    {
        var content = RichMail.Render(variant);
        return newsletter ? RichMail.WithUnsubscribe(content, variant.Language) : content;
    }

    private static MailBlock Convert(LeafBlock block, MailBlockKind kind)
    {
        if (block.Inline is not { } content)
            throw Unsupported(block);
        var runs = new List<MailRun>();
        Add(content, new(""));
        return new(kind, [.. runs]);

        void Add(ContainerInline container, MailRun style)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        Append(literal.Content.ToString(), style);
                        break;
                    case HtmlEntityInline entity:
                        Append(entity.Transcoded.ToString(), style);
                        break;
                    case LineBreakInline line:
                        Append(line.IsHard ? "\n" : " ", style);
                        break;
                    case EmphasisInline { DelimiterCount: 1 or 2 or 3 } emphasis:
                        Add(emphasis, style with
                        {
                            Bold = style.Bold || emphasis.DelimiterCount >= 2,
                            Italic = style.Italic || emphasis.DelimiterCount % 2 == 1
                        });
                        break;
                    case LinkInline { IsImage: false, Url: { } url } link when RichMail.SafeLink(url):
                        Add(link, style with { Link = url });
                        break;
                    case AutolinkInline autoLink:
                        var destination = autoLink.IsEmail ? "mailto:" + autoLink.Url : autoLink.Url;
                        if (!RichMail.SafeLink(destination))
                            throw Unsupported(inline);
                        Append(autoLink.Url, style with { Link = destination });
                        break;
                    default:
                        throw Unsupported(inline);
                }
            }
        }

        void Append(string text, MailRun style)
        {
            if (text.Length == 0)
                return;
            if (runs.Count > 0 && runs[^1] with { Text = "" } == style)
                runs[^1] = style with { Text = runs[^1].Text + text };
            else
                runs.Add(style with { Text = text });
        }
    }

    // Drafts saved before the Markdown editor have only their structured body.
    public static string Source(MailVariant variant)
    {
        if (variant.Markdown is not null)
            return variant.Markdown;
        return string.Join("\n\n", variant.Body.Select(block =>
        {
            if (block.Kind == MailBlockKind.OperationalDetails)
                return OperationalDetails;
            var body = string.Concat(block.Runs.Select(Run));
            return block.Kind switch
            {
                MailBlockKind.Heading => "## " + body,
                MailBlockKind.Bullet => "- " + body.Replace("\n", "\n  "),
                MailBlockKind.Quote => "> " + body.Replace("\n", "\n> "),
                _ => body
            };
        }));
    }

    internal static string ExpandOperationalDetails(MailVariant wrapper, string details)
    {
        var source = Source(wrapper);
        var marker = FindMarker(source);
        if (marker is null && wrapper.Markdown is not null)
        {
            source = Source(wrapper with { Markdown = null });
            marker = FindMarker(source);
        }
        return marker is { } span
            ? EscapeLiquid(source[..span.Start]) + details + EscapeLiquid(source[(span.End + 1)..])
            : EscapeLiquid(source);

        static SourceSpan? FindMarker(string markdown)
        {
            foreach (var paragraph in Markdown.Parse(markdown, Pipeline).OfType<ParagraphBlock>())
            {
                var span = paragraph.Span;
                if (markdown.AsSpan(span.Start, span.Length).Trim().SequenceEqual(OperationalDetails))
                    return span;
            }
            return null;
        }

        // Older wrapper bodies treated template delimiters in prose as literal Markdown.
        static string EscapeLiquid(string text) => Regex.Replace(text, @"\{(?=[{%])", @"{\");
    }

    private static string Run(MailRun run)
    {
        var text = Regex.Replace(run.Text.Replace("\r\n", "\n").Replace('\r', '\n'),
            @"[\\`*_{}\[\]()#+.!<>|&~>-]", match => "\\" + match.Value).Replace("\n", "  \n");
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return text;
        var start = text.IndexOf(trimmed, StringComparison.Ordinal);
        var mark = (run.Bold ? "**" : "") + (run.Italic ? "*" : "");
        var formatted = mark + trimmed + mark;
        if (run.Link is { } link)
            formatted = $"[{formatted}](<{link.Replace("<", "%3C").Replace(">", "%3E")}>)";
        return text[..start] + formatted + text[(start + trimmed.Length)..];
    }

    private static ValidationException Unsupported(MarkdownObject element) =>
        new(UiText.T("Unsupported Markdown or link on line {0}.", element.Line + 1));
}
