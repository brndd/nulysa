using Nulysa.Data;

namespace Nulysa.Billing;

public sealed class BillingAccount
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string PurchaseDepositReference { get; set; }
    public bool EmailInvoices { get; set; } = true;
    public string? ClaimName { get; set; }
    public string? ClaimEmail { get; set; }

    internal static async Task<BillingAccount> Create(AppDbContext db, long userId, DateTimeOffset createdAt, CancellationToken ct = default)
        => new()
        {
            UserId = userId,
            CreatedAt = createdAt,
            PurchaseDepositReference = await PaymentReference.Allocate(db, PaymentReferenceKind.PurchaseDeposit, ct)
        };
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
