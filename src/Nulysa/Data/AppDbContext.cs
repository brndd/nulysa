using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Membership;
using Nulysa.Operations;

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
    public DbSet<MailRateWindow> MailRateWindows => Set<MailRateWindow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
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
                table.HasCheckConstraint("CK_Delivery_Origin", "\"UserId\" IS NULL OR \"ApplicationId\" IS NULL");
                table.HasCheckConstraint("CK_Delivery_Acceptance", "\"State\" <> 'Accepted' OR \"AcceptedAt\" IS NOT NULL");
            });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.ProviderId).HasMaxLength(200);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<MembershipApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.State, x.CreatedAt });
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
