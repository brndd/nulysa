using System.ComponentModel.DataAnnotations;

namespace Nulysa.Billing;

// Account ownership is checked under the account lock; a destination describes only the intended purpose.
public abstract record ReceiptDestination
{
    private ReceiptDestination() { }

    public sealed record RecurringGroup(long GroupId) : ReceiptDestination;
    public sealed record OneOffInvoice(long InvoiceId) : ReceiptDestination;
    public sealed record PurchaseDeposit : ReceiptDestination;
    public sealed record BillingCredit : ReceiptDestination;

    internal bool Includes(Invoice invoice) => this switch
    {
        RecurringGroup group => invoice.BillingGroupId == group.GroupId,
        OneOffInvoice oneOff => invoice.Id == oneOff.InvoiceId,
        _ => false
    };

    // Preserve the persisted operation fingerprint when the in-memory routing representation changes.
    internal object OperationInput(long receiptId, long? accountId)
    {
        long? groupId = this is RecurringGroup group ? group.GroupId : null;
        var toDeposit = this is PurchaseDeposit;
        return this is OneOffInvoice oneOff
            ? new { receiptId, accountId, groupId, toDeposit, invoiceId = (long?)oneOff.InvoiceId }
            : new { receiptId, accountId, groupId, toDeposit };
    }

    // Only the form boundary accepts a selection; the financial workflow has exactly one purpose.
    public static ReceiptDestination FromSelection(long? groupId, bool purchaseDeposit, long? invoiceId = null)
    {
        if (invoiceId is not null && (groupId is not null || purchaseDeposit))
            throw new ValidationException("Select a one-off invoice belonging to this account, without a group or deposit destination.");
        if (purchaseDeposit && groupId is not null)
            throw new ValidationException("Purchase deposits are unavailable or conflict with a group selection.");

        return invoiceId is { } invoice ? new OneOffInvoice(invoice)
            : groupId is { } group ? new RecurringGroup(group)
            : purchaseDeposit ? new PurchaseDeposit() : new BillingCredit();
    }
}
