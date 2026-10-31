namespace Nulysa.Billing;

public sealed class BillingAccount
{
    public long Id { get; set; }
    public long? UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// S2 consumes this boundary in the ordinary billing path; it is NOT an issued invoice or coverage.
public sealed class InitialMembershipRequest
{
    public long BillingAccountId { get; set; }
    public DateOnly Anchor { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}
