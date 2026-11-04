using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Nulysa.Communications;
using Nulysa.Localization;

namespace Nulysa.Tests;

public sealed class MailMarkdownTests
{
    [Fact]
    public void Empty_campaign_body_validation_is_translated_for_finnish_admins()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fi");
            var error = Assert.Throws<ValidationException>(() => MailMarkdown.Parse("fi", "Otsikko", ""));
            Assert.Equal("Käytä tuettuja muotoiluja ja absoluuttisia HTTPS-, HTTP- tai mailto-linkkejä.", ValidationText.Translate(error.Message));
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Supported_formatting_reaches_the_mail_renderer_and_keeps_the_editable_source()
    {
        const string source = """
            ## Workshop evening

            Join **our _workshop_** and [sign up](https://example.test/?a=1&b=2).

            - Bring tools
            - Invite a friend

            > Beginners welcome.
            """;
        var message = MailMarkdown.Parse("en", "November news", source);
        var preview = MailMarkdown.Preview(message, newsletter: true);

        Assert.Equal(source, MailMarkdown.Source(message));
        Assert.Equal("November news", preview.Subject);
        Assert.Contains("<h2>Workshop evening</h2>", preview.Html);
        Assert.Contains("<em><strong>workshop</strong></em>", preview.Html);
        Assert.Contains("https://example.test/?a=1&amp;b=2", preview.Html);
        Assert.Contains("<ul><li>Bring tools</li><li>Invite a friend</li></ul>", preview.Html);
        Assert.Contains("<blockquote>Beginners welcome.</blockquote>", preview.Html);
        Assert.Contains("sign up (https://example.test/?a=1&b=2)", preview.Text);
        Assert.Contains("Unsubscribe from newsletters", preview.Text);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Hello <script>alert(1)</script>")]
    [InlineData("[unsafe](javascript:alert)")]
    [InlineData("![photo](https://example.test/photo.png)")]
    [InlineData("1. Numbered")]
    [InlineData("- [ ] Task")]
    [InlineData("`code`")]
    public void Unsupported_syntax_reports_its_source_line(string unsupported)
    {
        var error = Assert.Throws<ValidationException>(() => MailMarkdown.Parse("en", "Test", "Hello\n\n" + unsupported));
        Assert.Contains(3.ToString(CultureInfo.CurrentCulture), error.Message);
    }

    [Fact]
    public void Automatic_preview_supports_literal_braces_text_fields_and_typed_links()
    {
        var preview = OperationalTemplates.Preview(MailKind.Verification, new("en",
            @"{% raw %}{{example}}{% endraw %} and {% raw %}{{other}}{% endraw %} for {{association}}",
            @"{% raw %}{{example}}{% endraw %} and {% raw %}{{other}}{% endraw %} **{{association}}**. [Verify now]({{verification_url}})."));

        Assert.Equal("{{example}} and {{other}} for Example Hackerspace", preview.Subject);
        Assert.Contains("{{example}} and {{other}} <strong>Example Hackerspace</strong>", preview.Html);
        Assert.Contains("<a href=\"https://example.org/apply/verify?id=example\"", preview.Html);
        Assert.Equal(new MailContent("", "", ""), OperationalTemplates.Preview(MailKind.Verification, new("en", "", "")));
    }

    [Theory]
    [InlineData("{{retired_field}}")]
    [InlineData("{{ association | upcase }}")]
    [InlineData("{% include 'file' %}")]
    [InlineData("{% if rejection_reason %}Text{% endif %}")]
    [InlineData("{{verification_url}}")]
    [InlineData("[Verify](https://example.test/?token={{verification_url}})")]
    [InlineData("[Verify](https://example.test/{{association}})")]
    [InlineData("[Verify](https://example.test \"{{association}}\")")]
    [InlineData("{% if association %}\nHello\n{% endif %}")]
    public void Automatic_preview_rejects_unknown_or_misplaced_fields(string body)
    {
        Assert.Throws<ValidationException>(() => OperationalTemplates.Preview(MailKind.Verification, new("en", "", body)));
    }

    [Fact]
    public void Automatic_preview_renders_sample_optional_sections()
    {
        var preview = OperationalTemplates.Preview(MailKind.Rejection, new("en", "Decision",
            "Decision recorded.\n\n{% if rejection_reason %}\n**Reason:** {{rejection_reason}}\n{% else %}\nNo reason supplied.\n{% endif %}"));

        Assert.Contains("<strong>Reason:</strong> The association is currently at capacity.", preview.Html);
    }
}
