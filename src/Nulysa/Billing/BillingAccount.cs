namespace Nulysa.Billing;

public sealed class BillingAccount
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string PurchaseDepositReference { get; set; } = PaymentReference.Create('1');
    public bool EmailInvoices { get; set; } = true;
    public string? ClaimName { get; set; }
    public string? ClaimEmail { get; set; }
}

// Retained S1 handover record; InvoiceId is populated atomically by the ordinary billing path.
public sealed class InitialMembershipRequest
{
    public long BillingAccountId { get; set; }
    public DateOnly Anchor { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public long? InvoiceId { get; set; }
}
