using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Membership;
using Nulysa.Services;

namespace Nulysa.Billing;

public static class BillingMapping
{
    public static void Configure(ModelBuilder builder)
    {
        // Leave the 17-digit space to previously issued references.
        builder.HasSequence<long>(PaymentReference.SequenceName).HasMax(99_999_999_999_999);

        builder.Entity<BillingAccount>(e =>
        {
            e.Property(x => x.PurchaseDepositReference).HasMaxLength(20);
            e.HasIndex(x => x.PurchaseDepositReference).IsUnique();
            e.Property(x => x.ClaimName).HasMaxLength(200);
            e.Property(x => x.ClaimEmail).HasMaxLength(256);
        });
        builder.Entity<InitialMembershipRequest>().HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<ServiceDefinition>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(80);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.ToTable(t => t.HasCheckConstraint("CK_Service_Durations", "\"InitialFreeDays\" >= 0 AND \"RenewalGraceDays\" >= 0 AND \"Priority\" >= 0"));
        });
        builder.Entity<ServiceEnrollment>(e =>
        {
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ServiceDefinition>().WithMany().HasForeignKey(x => x.ServiceDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.UserId, x.ServiceDefinitionId }).IsUnique().HasFilter("\"EndedAt\" IS NULL");
            e.Property(x => x.EndReason).HasConversion(
                new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<ServiceEndReason, string>(
                    reason => reason.Code(), code => ServiceEndReasons.FromCode(code))).HasMaxLength(40);
        });
        builder.Entity<ServiceChange>(e =>
        {
            e.HasOne<ServiceEnrollment>().WithMany().HasForeignKey(x => x.ServiceEnrollmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<BillingOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Action).HasMaxLength(40);
        });
        builder.Entity<FeeDefinition>(e =>
        {
            e.HasOne<ServiceDefinition>().WithOne().HasForeignKey<FeeDefinition>(x => x.ServiceDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ServiceDefinitionId).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Category).HasMaxLength(80);
            e.HasData(new FeeDefinition { Id = 1, Name = "Membership", Category = "membership" });
        });
        builder.Entity<FeePrice>(e =>
        {
            e.HasOne<FeeDefinition>().WithMany().HasForeignKey(x => x.FeeDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.FeeDefinitionId, x.EffectiveOn }).IsUnique();
            e.Property(x => x.MonthlyAmount).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("CK_Price_Amount", "\"MonthlyAmount\" >= 0"));
        });
        builder.Entity<Discount>(e =>
        {
            e.Property(x => x.EndExclusive).HasColumnName("EndsOn");
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FeeDefinition>().WithMany().HasForeignKey(x => x.FeeDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Value).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("CK_Discount", "\"StartsOn\" < \"EndsOn\" AND \"Value\" >= 0 AND (\"Kind\" <> 'Percentage' OR \"Value\" <= 100)"));
        });
        builder.Entity<BillingGroup>(e =>
        {
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BillingAccountId, x.IntervalMonths }).IsUnique();
            e.Property(x => x.PaymentReference).HasMaxLength(20);
            e.HasIndex(x => x.PaymentReference).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Group_Interval", "\"IntervalMonths\" BETWEEN 1 AND 120"));
        });
        builder.Entity<RecurringItem>(e =>
        {
            e.Property(x => x.MembershipUserId).HasColumnName("MembershipId");
            e.Property(x => x.NextUnissuedPeriodStartsOn).HasColumnName("NextOn");
            e.HasOne<BillingGroup>().WithMany().HasForeignKey(x => x.BillingGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FeeDefinition>().WithMany().HasForeignKey(x => x.FeeDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Member>().WithMany().HasForeignKey(x => x.MembershipUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ServiceEnrollment>().WithMany().HasForeignKey(x => x.ServiceEnrollmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.MembershipUserId).IsUnique().HasFilter("\"EndedAt\" IS NULL AND \"MembershipId\" IS NOT NULL");
            e.HasIndex(x => x.ServiceEnrollmentId).IsUnique().HasFilter("\"EndedAt\" IS NULL AND \"ServiceEnrollmentId\" IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("CK_Item_Owner", "(\"MembershipId\" IS NULL) <> (\"ServiceEnrollmentId\" IS NULL)"));
        });
        builder.Entity<Invoice>(e =>
        {
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingGroup>().WithMany().HasForeignKey(x => x.BillingGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.BillingGroupId).IsUnique().HasFilter("\"ClosedAt\" IS NULL AND \"BillingGroupId\" IS NOT NULL");
            e.HasIndex(x => x.OneOffReference).IsUnique();
            e.Property(x => x.OneOffReference).HasMaxLength(20);
            e.ToTable(t => t.HasCheckConstraint("CK_Invoice_Routing", "(\"BillingGroupId\" IS NULL) <> (\"OneOffReference\" IS NULL)"));
        });
        builder.Entity<InvoiceLine>(e =>
        {
            e.Property(x => x.EndExclusive).HasColumnName("EndsOn");
            e.Property(x => x.OriginalEndExclusive).HasColumnName("OriginalEndsOn");
            e.Property(x => x.PaymentCutoffExclusive).HasColumnName("ExpiresOn");
            e.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RecurringItem>().WithMany().HasForeignKey(x => x.RecurringItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ServiceEnrollment>().WithMany().HasForeignKey(x => x.ServiceEnrollmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<ServiceDefinition>().WithMany().HasForeignKey(x => x.ServiceDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FeeDefinition>().WithMany().HasForeignKey(x => x.FeeDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Benefit).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PurchaseKey).HasMaxLength(160);
            e.HasIndex(x => x.PurchaseKey).IsUnique();
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Category).HasMaxLength(80);
            e.Property(x => x.PricingJson).HasColumnType("jsonb");
            e.Property(x => x.OriginalPricingJson).HasColumnType("jsonb");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Line_PricePeriod", "\"Amount\" >= 0 AND \"StartsOn\" < \"EndsOn\" AND \"OriginalStartsOn\" < \"OriginalEndsOn\"");
                t.HasCheckConstraint("CK_Line_Benefit", "(\"Benefit\" = 'Service') = (\"ServiceDefinitionId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_Line_Fulfilment", "(\"State\" = 'Fulfilled') = (\"FulfilledAt\" IS NOT NULL)");
            });
        });
        builder.Entity<Receipt>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(40);
            e.Property(x => x.SourceAccount).HasMaxLength(200);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.HasIndex(x => new { x.Source, x.SourceAccount, x.ExternalId }).IsUnique();
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Reference).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(4000);
            e.Property(x => x.Counterparty).HasMaxLength(1000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ReviewReason).HasMaxLength(1000);
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Receipt_Amount", "\"Amount\" <> 0"));
        });
        builder.Entity<BillingOperation>(e =>
        {
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Kind).HasMaxLength(40);
            e.Property(x => x.InputHash).HasMaxLength(64);
            e.Property(x => x.Reason).HasMaxLength(1000);
        });
        builder.Entity<CreditLot>(e =>
        {
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.OriginalAmount).HasPrecision(18, 2);
            e.Property(x => x.RemainingAmount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.BillingAccountId, x.ReceivedOn });
            e.ToTable(t => t.HasCheckConstraint("CK_CreditLot_Amount",
                "\"OriginalAmount\" > 0 AND \"RemainingAmount\" >= 0 AND \"RemainingAmount\" <= \"OriginalAmount\""));
        });
        builder.Entity<MoneyMovement>(e =>
        {
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InvoiceLine>().WithMany().HasForeignKey(x => x.InvoiceLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Discount>().WithMany().HasForeignKey(x => x.DiscountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MoneyMovement>().WithOne().HasForeignKey<MoneyMovement>(x => x.ReversesMovementId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.EffectiveOn);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Movement_Amount", "\"Amount\" > 0");
                t.HasCheckConstraint("CK_Movement_Receipt", "(\"Kind\" IN ('ReceiptToLine','ReceiptToBilling','ReceiptToPurchase','BillingReceiptReversal','PurchaseReceiptReversal')) = (\"ReceiptId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_Movement_Reversal", "(\"Kind\" IN ('BillingReceiptReversal','PurchaseReceiptReversal')) = (\"ReversesMovementId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_Movement_Line", "(\"Kind\" IN ('ReceiptToLine','BillingToLine','LineToBilling','LinePurchase','PurchaseCredit')) = (\"InvoiceLineId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_Movement_Discount", "(\"Kind\" = 'PurchaseCredit') = (\"DiscountId\" IS NOT NULL)");
            });
        });
        builder.Entity<Coverage>(e =>
        {
            e.Property(x => x.EndExclusive).HasColumnName("EndsOn");
            e.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ServiceDefinition>().WithMany().HasForeignKey(x => x.ServiceDefinitionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InvoiceLine>().WithMany().HasForeignKey(x => x.InvoiceLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Benefit).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Origin).HasMaxLength(200);
            e.HasIndex(x => x.Origin).IsUnique();
            e.HasIndex(x => new { x.BillingAccountId, x.ServiceDefinitionId }).IsUnique().HasFilter("\"Source\" = 'InitialFree'");
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Coverage_Period", "\"StartsOn\" < \"EndsOn\"");
                t.HasCheckConstraint("CK_Coverage_Benefit", "\"Benefit\" IN ('Membership','Service') AND (\"Benefit\" = 'Service') = (\"ServiceDefinitionId\" IS NOT NULL)");
                t.HasCheckConstraint("CK_Coverage_Source", "(\"Source\" = 'Purchase') = (\"InvoiceLineId\" IS NOT NULL)");
            });
        });
        builder.Entity<LineAdjustment>(e =>
        {
            e.Property(x => x.OldEndExclusive).HasColumnName("OldEnd");
            e.Property(x => x.NewEndExclusive).HasColumnName("NewEnd");
            e.HasOne<InvoiceLine>().WithMany().HasForeignKey(x => x.InvoiceLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BillingOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.OldAmount).HasPrecision(18, 2);
            e.Property(x => x.NewAmount).HasPrecision(18, 2);
            e.Property(x => x.PricingJson).HasColumnType("jsonb");
        });
    }
}
