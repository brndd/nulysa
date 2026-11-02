using Microsoft.EntityFrameworkCore;

namespace Nulysa.Data;

// Stable resource IDs in the fixed-lock namespace; add new purposes here rather than at call sites.
public enum DatabaseLock
{
    ScheduleInstallation = 1,
    RoleAdministration = 2,
    MailDispatchRate = 3,
    FeeCatalog = 4
}

public static class DatabaseLocks
{
    // PostgreSQL separates two-integer keys from the bigint keys used for hashed identities below.
    private const int FixedLockNamespace = 0;

    public static Task Acquire(AppDbContext db, DatabaseLock resource, CancellationToken ct = default)
    {
        RequireTransaction(db);
        if (!Enum.IsDefined(resource))
            throw new ArgumentOutOfRangeException(nameof(resource));

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({FixedLockNamespace}, {(int)resource})", ct);
    }

    // The caller supplies a canonical identity (normalized email or the complete source tuple).
    public static Task AcquireForIdentity(AppDbContext db, string identity, CancellationToken ct = default)
    {
        RequireTransaction(db);

        // Keep a stable hash seed so all processes derive the same 64-bit lock key.
        return db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))", ct);
    }

    private static void RequireTransaction(AppDbContext db)
    {
        // Without an outer transaction, PostgreSQL would release the lock after this statement.
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Database locks require an outer transaction.");
    }
}
