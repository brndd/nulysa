using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Operations;

namespace Nulysa.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<User, IdentityRole<long>, long>(options)
{
    public DbSet<Observation> Observations => Set<Observation>();
    public DbSet<FailureObservation> FailureObservations => Set<FailureObservation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        builder.Entity<User>(user =>
        {
            user.Property(x => x.Name).HasMaxLength(200);
            user.Property(x => x.OidcSubject).HasMaxLength(256).IsRequired();
            user.HasIndex(x => x.OidcSubject).IsUnique();
            user.HasIndex(x => x.NormalizedEmail).IsUnique();
            user.Ignore(x => x.MayLogIn);
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
