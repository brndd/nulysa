using System.ComponentModel.DataAnnotations;
using Nulysa.Communications;
using Nulysa.Localization;

namespace Nulysa.Components;

public sealed class MarkdownMailInput(UiLanguage language, MailVariant? original, bool enabled, string emptySubject = "", string emptySource = "")
{
    private readonly string initialSource = original is null ? emptySource : MailMarkdown.Source(original);

    public UiLanguage Language { get; } = language;
    public bool Enabled { get; set; } = enabled;
    public string Subject { get; set; } = original?.Subject ?? emptySubject;
    public string Source { get; set; } = original is null ? emptySource : MailMarkdown.Source(original);

    public MailVariant Read(bool operational) => Read(operational, allowEmptySubject: false);

    public MailVariant ReadPreview(bool operational)
    {
        return string.IsNullOrWhiteSpace(Source)
            ? new(Language.Code, Subject.Trim(), [])
            : Read(operational, allowEmptySubject: true);
    }

    private MailVariant Read(bool operational, bool allowEmptySubject)
    {
        // Preserve any structured content that predates this editor when its body is left alone.
        if (original is { Markdown: null } && Source == initialSource)
            return RichMail.Validate([original with { Subject = Subject }], operational, allowEmptySubject)[0];
        return MailMarkdown.Parse(Language.Code, Subject, Source, operational, allowEmptySubject);
    }

    public static MailVariant[] ReadEnabled(IEnumerable<MarkdownMailInput> inputs, bool operational)
    {
        var variants = new List<MailVariant>();
        foreach (var input in inputs.Where(x => x.Enabled))
        {
            try { variants.Add(input.Read(operational)); }
            catch (ValidationException error)
            {
                throw new ValidationException(UiText.T("{0}: {1}", input.Language.Name, ValidationText.Translate(error.Message)));
            }
        }
        return RichMail.Validate(variants, operational);
    }
}
