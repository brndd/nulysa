using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Nulysa.Communications;

namespace Nulysa.Tests;

public sealed class MailMarkdownTests
{
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
    public void Automatic_details_require_one_standalone_marker_and_render_as_a_block()
    {
        const string token = MailMarkdown.OperationalDetails;
        var variant = MailMarkdown.Parse("en", "{{subject}}", $"Hello\n\n{token}\n\nThanks", operational: true);
        var preview = MailMarkdown.Preview(variant, details: "Your real details", sampleSubject: "Sample");
        Assert.Equal("Sample", preview.Subject);
        Assert.Contains("<p>Your real details</p>", preview.Html);
        Assert.Throws<ValidationException>(() => MailMarkdown.Parse("en", "Subject", token));
        Assert.Throws<ValidationException>(() => MailMarkdown.Parse("en", "Subject", $"Hello {token}", operational: true));
        Assert.Throws<ValidationException>(() => MailMarkdown.Parse("en", "Subject", $"{token}\n\n{token}", operational: true));
    }
}
