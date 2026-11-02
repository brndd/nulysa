namespace Nulysa.Billing;

public enum Benefit { Membership, Service, OneOff }
public enum LineState { Open, Fulfilled, Cancelled, Expired }
public enum PurchasePurpose { Initial, Renewal, Alignment, Resumption, OneOff }
public enum ReceiptState { New, Review, Posted, Ignored }
public enum MovementKind { ReceiptToLine, ReceiptToBilling, ReceiptToPurchase, BillingToLine, LineToBilling, LinePurchase, BillingToPurchase, PurchaseToBilling, PurchaseCredit, BillingRefund, PurchaseRefund, BillingReceiptReversal, PurchaseReceiptReversal }
public enum CoverageSource { Purchase, InitialFree, AdministratorGrant }
public enum DiscountKind { Percentage, MonthlyPrice }

public sealed class FeeDefinition
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public long? ServiceDefinitionId { get; set; }
    public bool Retired { get; set; }
}

public sealed class FeePrice
{
    public long Id { get; set; }
    public long FeeDefinitionId { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public decimal MonthlyAmount { get; set; }
}

public sealed class Discount
{
    public long Id { get; set; }
    public long BillingAccountId { get; set; }
    public long FeeDefinitionId { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public DiscountKind Kind { get; set; }
    public decimal Value { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
}

public sealed class BillingGroup
{
    public long Id { get; set; }
    public long BillingAccountId { get; set; }
    public int IntervalMonths { get; set; }
    public DateOnly Anchor { get; set; }
    public string PaymentReference { get; set; } = Billing.PaymentReference.Create('2');
}

public sealed class RecurringItem
{
    public long Id { get; set; }
    public long BillingGroupId { get; set; }
    public long FeeDefinitionId { get; set; }
    // Member uses UserId as its key; exactly one of these two owners must be present.
    public long? MembershipUserId { get; set; }
    public long? ServiceEnrollmentId { get; set; }
    public DateOnly StartsOn { get; set; }
    // Issuance advances this boundary, even while the issued line is still unpaid.
    public DateOnly NextUnissuedPeriodStartsOn { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}

public sealed class Invoice
{
    public long Id { get; set; }
    public long BillingAccountId { get; set; }
    public long? BillingGroupId { get; set; }
    public string? OneOffReference { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateOnly? DueOn { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int Revision { get; set; } = 1;
    public int NotifiedRevision { get; set; }
}

public sealed class InvoiceLine
{
    public long Id { get; set; }
    public long InvoiceId { get; set; }
    public long? RecurringItemId { get; set; }
    public long? ServiceEnrollmentId { get; set; }
    public long? ServiceDefinitionId { get; set; }
    public long? FeeDefinitionId { get; set; }
    public Benefit Benefit { get; set; }
    public PurchasePurpose Purpose { get; set; }
    public string PurchaseKey { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public DateOnly OriginalStartsOn { get; set; }
    public DateOnly OriginalEndsOn { get; set; }
    public DateOnly ChargeDueOn { get; set; }
    public DateOnly ExpiresOn { get; set; }
    public decimal Amount { get; set; }
    public string PricingJson { get; set; } = "[]";
    public string OriginalPricingJson { get; set; } = "[]";
    public LineState State { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
    public DateOnly? EffectiveOn { get; set; }
    public DateOnly? LastFundingOn { get; set; }
    public DateTimeOffset? GraceNotifiedAt { get; set; }
    public DateTimeOffset? ExpiryNotifiedAt { get; set; }
}

public sealed class Receipt
{
    public long Id { get; set; }
    public string Source { get; set; } = "";
    public string SourceAccount { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public DateOnly ReceivedOn { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
    public string Reference { get; set; } = "";
    public string Message { get; set; } = "";
    public string Counterparty { get; set; } = "";
    public ReceiptState State { get; set; }
    public string? ReviewReason { get; set; }
    public long? BillingAccountId { get; set; }
}

public sealed class MoneyMovement
{
    public long Id { get; set; }
    public long BillingAccountId { get; set; }
    public Guid OperationId { get; set; }
    public MovementKind Kind { get; set; }
    public decimal Amount { get; set; }
    public DateOnly EffectiveOn { get; set; }
    public long? ReceiptId { get; set; }
    public long? InvoiceLineId { get; set; }
    public long? DiscountId { get; set; }
    public long? ReversesMovementId { get; set; }
}

public sealed class CreditLot
{
    public long Id { get; set; }
    public long BillingAccountId { get; set; }
    public long ReceiptId { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal RemainingAmount { get; set; }
}

public sealed class Coverage
{
    public long Id { get; set; }
    public long BillingAccountId { get; set; }
    public Benefit Benefit { get; set; }
    public long? ServiceDefinitionId { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public CoverageSource Source { get; set; }
    public long? InvoiceLineId { get; set; }
    public Guid OperationId { get; set; }
    public string Origin { get; set; } = "";
}

public sealed class BillingOperation
{
    public Guid Id { get; set; }
    public long BillingAccountId { get; set; }
    public string Kind { get; set; } = "";
    public string InputHash { get; set; } = "";
    public long? ActorId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public long? ResultId { get; set; }
}

public sealed class LineAdjustment
{
    public long Id { get; set; }
    public long InvoiceLineId { get; set; }
    public Guid OperationId { get; set; }
    public decimal OldAmount { get; set; }
    public decimal NewAmount { get; set; }
    public DateOnly OldStart { get; set; }
    public DateOnly OldEnd { get; set; }
    public DateOnly NewStart { get; set; }
    public DateOnly NewEnd { get; set; }
    public string PricingJson { get; set; } = "[]";
}
