using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Fluid;
using Fluid.Ast;
using Fluid.Parser;
using Fluid.Values;
using Nulysa.Localization;

namespace Nulysa.Communications;

public sealed record OperationalTemplateSource(string Language, string Subject, string Body);
public sealed record OperationalTemplateField(string Name, string Label, string Sample, bool Link = false, bool Required = false, bool Optional = false);

internal sealed class OperationalTemplateMarkup
{
    private const string LinkPrefix = "https://nulysa.invalid/";
    private static readonly FluidParser Parser = new();

    // Fluid renders one inert marker per output. Real values are inserted after Markdig has parsed the result.
    private readonly string markerPrefix = "NulysaMailField" + Guid.NewGuid().ToString("N");
    private readonly Regex marker;
    private readonly Dictionary<string, OperationalTemplateField> tokens = [];
    private readonly OperationalTemplateField[] optionalFields;
    private readonly MailVariant[] variants;

    public OperationalTemplateMarkup(OperationalTemplateSource source, IReadOnlyList<OperationalTemplateField> fields, bool preview = false)
    {
        if (source.Subject is null || source.Subject.Length > RichMail.SubjectLimit || source.Subject.Any(char.IsControl) ||
            (!preview && string.IsNullOrWhiteSpace(source.Subject)))
            throw Error("Enter a single-line subject up to 200 characters.");
        if (source.Body is null || source.Body.Length > MailMarkdown.OperationalSourceLimit)
            throw Error("The Markdown source is too long.");

        marker = new(markerPrefix + "[0-9]+End");
        optionalFields = fields.Where(field => field.Optional).ToArray();
        var subject = Compile(source.Subject.Trim(), subject: true);
        var body = Compile(source.Body, subject: false);
        // Optional scalars are the only conditions, so every possible layout can be validated before sending.
        variants = new MailVariant[1 << optionalFields.Length];
        for (var mask = 0; mask < variants.Length; mask++)
        {
            var context = new TemplateContext();
            var sample = fields.ToDictionary(field => field.Name, field => !field.Optional ? field.Sample : null);
            for (var i = 0; i < optionalFields.Length; i++)
            {
                var field = optionalFields[i];
                var present = (mask & (1 << i)) != 0;
                context.SetValue(field.Name, present);
                if (present)
                    sample[field.Name] = field.Sample;
            }

            var renderedSubject = subject.Render(context);
            var markdown = body.Render(context);
            var blocks = string.IsNullOrWhiteSpace(markdown) ? [] : MailMarkdown.Parse(source.Language, "Template", markdown, allowEmptySubject: false, operational: true).Body;
            var fieldsInBody = CheckPlacement(markdown, blocks);
            if (!preview && fields.FirstOrDefault(field => field.Required && !fieldsInBody.Contains(field.Name)) is { } missing)
                throw Error("Include {{{{{0}}}}} in the message body in every conditional branch.", missing.Name);
            variants[mask] = new(source.Language, renderedSubject, blocks);
            if (!preview)
            {
                var content = Render(sample);
                if (string.IsNullOrWhiteSpace(content.Subject) || string.IsNullOrWhiteSpace(content.Text))
                    throw Error("The subject and body must contain text when optional fields are empty.");
            }
        }

        IFluidTemplate Compile(string input, bool subject)
        {
            if (!Parser.TryParse(input, out var parsed, out var problem))
                throw Error("Invalid Liquid template: {0}", problem);
            return new FluidTemplate(Rewrite(((FluidTemplate)parsed).Statements, subject));
        }

        Statement[] Rewrite(IReadOnlyList<Statement> statements, bool subject) => statements.Select(statement =>
        {
            switch (statement)
            {
                case TextSpanStatement or RawStatement or CommentStatement:
                    return statement;
                case OutputStatement output when output.Filters is null or { Count: 0 }:
                    var field = Field(output.Expression);
                    if (field.Link && subject)
                        throw Error("Use {0} as a Markdown link destination.", field.Name);
                    var key = markerPrefix + tokens.Count + "End";
                    tokens.Add(key, field);
                    return new OutputStatement(new LiteralExpression(new StringValue(field.Link ? LinkPrefix + key : key)));
                case IfStatement conditional when conditional.ElseIfs is null or { Count: 0 }:
                    if (!Field(conditional.Condition).Optional)
                        throw Error("Use Liquid if blocks with optional fields.");
                    var otherwise = conditional.Else is null ? null : new ElseStatement(Rewrite(conditional.Else.Statements, subject));
                    return new IfStatement(conditional.Condition, Rewrite(conditional.Statements, subject), otherwise, []);
                default:
                    throw Error("Use Liquid fields, if and else blocks, comments, or raw blocks.");
            }
        }).ToArray();

        OperationalTemplateField Field(Expression expression)
        {
            if (expression is not MemberExpression { Segments: [IdentifierSegment name] })
                throw Error("Use a single named template field in Liquid expressions.");
            return fields.FirstOrDefault(field => field.Name == name.Identifier)
                ?? throw Error("Unknown template field: {0}.", name.Identifier);
        }
    }

    private HashSet<string> CheckPlacement(string markdown, MailBlock[] blocks)
    {
        var found = new HashSet<string>();
        foreach (var run in blocks.SelectMany(block => block.Runs))
        {
            foreach (Match match in marker.Matches(run.Text))
            {
                if (!tokens.TryGetValue(match.Value, out var field) ||
                    (field.Link && (run.Text != LinkPrefix + match.Value || run.Link != run.Text)))
                    throw Error("Use URL fields as complete Markdown link destinations.");
                found.Add(match.Value);
            }
            if (run.Link is null || !marker.IsMatch(run.Link))
                continue;
            if (!run.Link.StartsWith(LinkPrefix, StringComparison.Ordinal) ||
                !tokens.TryGetValue(run.Link[LinkPrefix.Length..], out var linkField) || !linkField.Link)
                throw Error("Use URL fields as complete Markdown link destinations.");
            found.Add(run.Link[LinkPrefix.Length..]);
        }

        if (!found.SetEquals(marker.Matches(markdown).Select(match => match.Value)))
            throw Error("Use template fields in visible text or as complete Markdown link destinations.");
        return found.Select(key => tokens[key].Name).ToHashSet();
    }

    public MailContent Render(IReadOnlyDictionary<string, string?> values)
    {
        var mask = 0;
        for (var i = 0; i < optionalFields.Length; i++)
            if (!string.IsNullOrWhiteSpace(values[optionalFields[i].Name]))
                mask |= 1 << i;

        var variant = variants[mask];
        var body = variant.Body.Select(block => block with { Runs = block.Runs.Select(RenderRun).ToArray() }).ToArray();
        var subject = new string(ReplaceText(variant.Subject).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return RichMail.Render(variant with { Subject = subject, Body = body });

        string ReplaceText(string text) => marker.Replace(text, match => values[tokens[match.Value].Name] ?? "");

        MailRun RenderRun(MailRun run)
        {
            if (run.Link is null || !marker.IsMatch(run.Link))
                return run with { Text = ReplaceText(run.Text) };
            var destination = values[tokens[run.Link[LinkPrefix.Length..]].Name];
            if (destination is null || !RichMail.SafeLink(destination))
                throw Error("An automatic mail link is invalid.");
            return run with { Link = destination, Text = run.Text == run.Link ? destination : ReplaceText(run.Text) };
        }
    }

    private static ValidationException Error(string message, params object[] args) =>
        new(UiText.T(message, args));
}
