namespace Nulysa.Communications;

public enum MailKind { Verification, Welcome, Rejection, DeletionWarning, PasswordReset, EmailChange, Restoration, Invoice, MembershipGrace, MembershipInactive, BankAlert }
public enum DeliveryState { Pending, Sending, Accepted, Review, Failed, Cancelled }

public sealed class Delivery
{
    public Guid Id { get; set; }
    public MailKind Kind { get; set; }
    public DeliveryState State { get; set; }
    public long? UserId { get; set; }
    public Guid? ApplicationId { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public int Attempts { get; set; }
    public string? ProviderId { get; set; }
    public long? InvoiceId { get; set; }
    public int? InvoiceRevision { get; set; }
}

public sealed class MailOptions
{
    public string PublicUrl { get; set; } = "http://127.0.0.1:5000/";
    public string Transport { get; set; } = "Development";
    public string? SpoolPath { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool EnableSsl { get; set; } = true;
    public string? FromEmail { get; set; }
    public int MaxPerMinute { get; set; } = 60;
}

public sealed record OutgoingMail(string To, string Subject, string Text, byte[]? Pdf = null, string? FileName = null);
public enum SendOutcome { Accepted, Throttled, Rejected, Uncertain }
public sealed record SendResult(SendOutcome Outcome, string? ProviderId = null);

public sealed class MailRateWindow
{
    public int Id { get; set; } = 1;
    public DateTimeOffset StartedAt { get; set; }
    public int Attempts { get; set; }
}

public interface IMailTransport
{
    Task<SendResult> Send(Guid id, OutgoingMail mail, CancellationToken ct);
}
