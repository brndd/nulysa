using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Integrations;
using Nulysa.Membership;
using Nulysa.Operations;
using Nulysa.Services;

namespace Nulysa.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<User, IdentityRole<long>, long>(options)
{
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<FailureObservation> FailureObservations => Set<FailureObservation>();
    public DbSet<MembershipApplication> Applications => Set<MembershipApplication>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<LifecycleAction> LifecycleActions => Set<LifecycleAction>();
    public DbSet<BillingAccount> BillingAccounts => Set<BillingAccount>();
    public DbSet<InitialMembershipRequest> InitialMembershipRequests => Set<InitialMembershipRequest>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<MailCampaign> MailCampaigns => Set<MailCampaign>();
    public DbSet<OperationalMailTemplate> OperationalMailTemplates => Set<OperationalMailTemplate>();
    public DbSet<MailDeliveryRetry> MailDeliveryRetries => Set<MailDeliveryRetry>();
    public DbSet<MailRateWindow> MailRateWindows => Set<MailRateWindow>();
    public DbSet<FeeDefinition> Fees => Set<FeeDefinition>();
    public DbSet<FeePrice> FeePrices => Set<FeePrice>();
    public DbSet<Discount> Discounts => Set<Discount>();
    public DbSet<BillingGroup> BillingGroups => Set<BillingGroup>();
    public DbSet<RecurringItem> RecurringItems => Set<RecurringItem>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<CreditLot> CreditLots => Set<CreditLot>();
    public DbSet<MoneyMovement> MoneyMovements => Set<MoneyMovement>();
    public DbSet<Coverage> Coverage => Set<Coverage>();
    public DbSet<BillingOperation> BillingOperations => Set<BillingOperation>();
    public DbSet<LineAdjustment> LineAdjustments => Set<LineAdjustment>();
    public DbSet<ServiceDefinition> ServiceDefinitions => Set<ServiceDefinition>();
    public DbSet<ServiceEnrollment> ServiceEnrollments => Set<ServiceEnrollment>();
    public DbSet<ServiceChange> ServiceChanges => Set<ServiceChange>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<BankConsentAttempt> BankConsentAttempts => Set<BankConsentAttempt>();
    public DbSet<BankSyncState> BankSyncStates => Set<BankSyncState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        BillingMapping.Configure(builder);
        builder.Entity<BankConnection>().HasIndex(x => new { x.Bank, x.IdentificationHash }).IsUnique();
        builder.Entity<BankConsentAttempt>().HasIndex(x => x.ExpiresAt);
        builder.Entity<BankSyncState>(e =>
        {
            e.HasKey(x => x.Account);
            e.Property(x => x.Account).HasMaxLength(200);
            e.Property(x => x.Status).HasMaxLength(80);
        });
        builder.Entity<User>(user =>
        {
            user.Property(x => x.Name).HasMaxLength(200);
            user.Property(x => x.Municipality).HasMaxLength(200);
            user.Property(x => x.UserName).IsRequired();
            user.Property(x => x.Language).HasMaxLength(10).HasDefaultValue("en");
            user.Property(x => x.Nickname).HasMaxLength(100);
            user.Property(x => x.MatrixId).HasMaxLength(200);
            user.Property(x => x.PendingEmail).HasMaxLength(256);
            user.Property(x => x.OidcSubject).HasMaxLength(256).IsRequired();
            user.HasIndex(x => x.OidcSubject).IsUnique();
            user.HasIndex(x => x.NormalizedEmail).IsUnique();
            user.Property(x => x.EmailNewsletters).HasDefaultValue(true);
            user.Ignore(x => x.MayLogIn);
        });
        builder.Entity<MembershipApplication>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint("CK_Application_Decision", "\"RejectedAt\" IS NULL OR \"VerifiedAt\" IS NOT NULL"));
            entity.Property(x => x.Email).HasMaxLength(256);
            entity.Property(x => x.NormalizedEmail).HasMaxLength(256);
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Municipality).HasMaxLength(200);
            entity.Property(x => x.Language).HasMaxLength(10);
            entity.Property(x => x.PhoneNumber).HasMaxLength(50);
            entity.Property(x => x.Nickname).HasMaxLength(100);
            entity.Property(x => x.MatrixId).HasMaxLength(200);
            entity.Property(x => x.Message).HasMaxLength(4000);
            entity.Property(x => x.RejectionExplanation).HasMaxLength(2000);
            entity.HasIndex(x => x.CreatedAt);
        });
        builder.Entity<Member>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint("CK_Member_Deletion", "(\"DeletionEligibleAt\" IS NULL) = (\"DeletionOperation\" IS NULL)"));
            entity.HasKey(x => x.UserId);
            entity.HasOne<User>().WithOne().HasForeignKey<Member>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<Approval>().HasIndex(x => x.UserId).IsUnique();
        builder.Entity<LifecycleAction>(entity =>
        {
            entity.Property(x => x.Action).HasMaxLength(30);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.HasIndex(x => x.UserId);
        });
        builder.Entity<BillingAccount>(entity =>
        {
            entity.HasOne<User>().WithOne().HasForeignKey<BillingAccount>(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => x.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");
        });
        builder.Entity<InitialMembershipRequest>(entity =>
        {
            entity.ToTable(table => table.HasCheckConstraint("CK_InitialRequest_Cancellation", "\"CancelledAt\" IS NULL OR \"CancelledAt\" >= \"RequestedAt\""));
            entity.HasKey(x => x.BillingAccountId);
            entity.HasOne<BillingAccount>().WithOne().HasForeignKey<InitialMembershipRequest>(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        });
        builder.Entity<Delivery>(entity =>
        {
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_Delivery_Attempts", "\"Attempts\" >= 0");
                table.HasCheckConstraint("CK_Delivery_Queue", """ "QueueVersion" >= 0 AND "AttemptLimit" >= "Attempts" """);
                table.HasCheckConstraint("CK_Delivery_Campaign", """
                    ("CampaignId" IS NOT NULL) = ("Kind" IN ('Newsletter', 'OfficialNotice', 'CampaignTest'))
                    AND ("OutgoingLanguage" IS NOT NULL) = ("CampaignId" IS NOT NULL)
                    AND ("PreferredLanguage" IS NOT NULL) = ("Kind" IN ('Newsletter', 'OfficialNotice'))
                    AND ("ProtectedTestMessage" IS NULL OR "Kind" = 'CampaignTest')
                    AND ("Kind" <> 'CampaignTest' OR "UserId" IS NULL)
                    """);
                table.HasCheckConstraint("CK_Delivery_Origin", "\"UserId\" IS NULL OR \"ApplicationId\" IS NULL");
                table.HasCheckConstraint("CK_Delivery_Acceptance", "\"State\" <> 'Accepted' OR \"AcceptedAt\" IS NOT NULL");
            });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ProviderId).HasMaxLength(200);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MembershipApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.State, x.CreatedAt });
            entity.Property(x => x.AttemptLimit).HasDefaultValue(MailQueue.AttemptsPerRound);
            entity.Property(x => x.PreferredLanguage).HasMaxLength(10);
            entity.Property(x => x.OutgoingLanguage).HasMaxLength(10);
            entity.HasOne<MailCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.CampaignId, x.State, x.CreatedAt });
        });
        builder.Entity<MailCampaign>(entity =>
        {
            entity.ToTable(table =>
            {
                table.HasCheckConstraint("CK_MailCampaign_Queued", """
                    ("QueuedAt" IS NULL AND "QueuedBy" IS NULL AND "RecipientCount" = 0)
                    OR ("QueuedAt" IS NOT NULL AND "QueuedBy" IS NOT NULL AND "RecipientCount" > 0)
                    """);
                table.HasCheckConstraint("CK_MailCampaign_Accepted", """
                    "AcceptedRecipients" BETWEEN 0 AND "RecipientCount"
                    """);
            });
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Content).HasColumnType("jsonb");
            entity.HasIndex(x => x.UpdatedAt);
        });
        builder.Entity<OperationalMailTemplate>(entity =>
        {
            entity.HasKey(x => x.Kind);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.Content).HasColumnType("jsonb");
            entity.Property(x => x.FallbackLanguage).HasMaxLength(10);
        });
        builder.Entity<MailDeliveryRetry>(entity =>
        {
            entity.HasKey(x => new { x.DeliveryId, x.QueueVersion });
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.HasOne<Delivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        });
        builder.Entity<MailRateWindow>(entity =>
        {
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.ToTable(table => table.HasCheckConstraint("CK_MailRateWindow", "\"Id\" = 1 AND \"Attempts\" >= 0"));
        });
        builder.Entity<Observation>(entity =>
        {
            entity.HasKey(x => x.Name);
            entity.Property(x => x.Name).HasMaxLength(80);
        });
        builder.Entity<FailureObservation>(entity =>
        {
            entity.HasKey(x => x.Job);
            entity.Property(x => x.Job).HasMaxLength(300);
        });
    }
}
