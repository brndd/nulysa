using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Nulysa.Communications;

public sealed class SmtpMailTransport(IOptions<MailOptions> options, IConfiguration config) : IMailTransport
{
    public async Task<SendResult> Send(Guid id, OutgoingMail mail, CancellationToken ct)
    {
        var settings = options.Value;
        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromEmail!, config["Association:Name"] ?? "Nulysa"),
            Subject = mail.Subject,
            Body = mail.Text,
            IsBodyHtml = false
        };
        message.To.Add(mail.To);
        if (mail.Html is not null)
        {
            // SmtpClient emits the Body separately when AlternateViews exist; make both MIME alternatives explicit.
            message.Body = "";
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(mail.Text, System.Text.Encoding.UTF8, "text/plain"));
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(mail.Html, System.Text.Encoding.UTF8, "text/html"));
        }
        if (mail.UnsubscribeUrl is { } url && new Uri(url).Scheme == Uri.UriSchemeHttps)
        {
            message.Headers.Add("List-Unsubscribe", $"<{url}>");
            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }
        if (mail.Pdf is not null)
            message.Attachments.Add(new Attachment(new MemoryStream(mail.Pdf), mail.FileName ?? "invoice.pdf", "application/pdf"));
        message.Headers.Add("X-Nulysa-Delivery-ID", id.ToString("D"));

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };
        if (!string.IsNullOrEmpty(settings.Username))
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        // The final SMTP acknowledgement can block without observing the send token.
        using var cancellation = deadline.Token.Register(client.Dispose);
        try
        {
            await client.SendMailAsync(message, deadline.Token);
            return new(SendOutcome.Accepted);
        }
        catch (SmtpException error) when ((int)error.StatusCode is >= 400 and < 500)
        {
            return new(SendOutcome.Throttled);
        }
        catch (SmtpException error) when ((int)error.StatusCode is >= 500 and < 600)
        {
            return new(SendOutcome.Rejected);
        }
        catch (Exception error) when (error is SmtpException or OperationCanceledException or SocketException or IOException or TimeoutException or InvalidOperationException)
        {
            // A timeout or lost reply may follow provider acceptance: never assume a resend is safe.
            return new(SendOutcome.Uncertain);
        }
    }
}
