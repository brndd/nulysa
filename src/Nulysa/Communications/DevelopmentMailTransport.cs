using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nulysa.Communications;

public sealed class DevelopmentMailTransport(IMailTransport inner, IOptions<MailOptions> options, IConfiguration config, IHostEnvironment environment, TextWriter? output = null) : IMailTransport
{
    private readonly TextWriter output = output ?? Console.Out;

    public async Task<SendResult> Send(Guid id, OutgoingMail mail, CancellationToken ct)
    {
        if (!environment.IsDevelopment()) throw new InvalidOperationException("Development mail is forbidden in Production.");
        var settings = options.Value;
        // Printed before delivery so the verification link remains available even when sending fails.
        output.WriteLine($"--- Development mail {id:D} ---");
        output.WriteLine($"To: {mail.To}");
        output.WriteLine($"Subject: {mail.Subject}");
        output.WriteLine();
        output.WriteLine(mail.Text);
        output.WriteLine("---");
        var path = settings.SpoolPath ?? Path.Combine(config["DataProtection:Path"]!, "mail");
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A private, operator-readable development mailbox, never a public static asset or log.
        var file = Path.Combine(path, id.ToString("N") + ".json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(mail), ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return settings.Transport == "Smtp" ? await inner.Send(id, mail, ct) : new(SendOutcome.Accepted, id.ToString("N"));
    }
}
