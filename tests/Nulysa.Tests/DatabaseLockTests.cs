using System.Data;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;

namespace Nulysa.Tests;

public sealed class DatabaseLockTests : DatabaseTest
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Named_locks_exclude_the_same_purpose_until_commit_or_rollback(bool commit)
    {
        foreach (var resource in Enum.GetValues<DatabaseLock>())
        {
            await using var holder = Context();
            await using var contender = Context();
            await using var holderTx = await holder.Database.BeginTransactionAsync();
            await using var contenderTx = await contender.Database.BeginTransactionAsync();

            await DatabaseLocks.Acquire(holder, resource);

            Assert.False(await TryNamedLock(contender, resource));
            // Even the equivalent numeric bigint key belongs to PostgreSQL's other key space.
            Assert.True(await contender.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock({(long)resource}) AS \"Value\"").SingleAsync());
            foreach (var other in Enum.GetValues<DatabaseLock>().Where(other => other != resource))
                Assert.True(await TryNamedLock(contender, other));

            if (commit)
                await holderTx.CommitAsync();
            else
                await holderTx.RollbackAsync();

            Assert.True(await TryNamedLock(contender, resource));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Identity_locks_coordinate_equal_keys_and_release_at_transaction_end(bool commit)
    {
        var identities = new[] { "MEMBER@EXAMPLE.TEST", "{\"Source\":\"test-bank\",\"ExternalId\":\"quoted'id\"}" };
        foreach (var identity in identities)
        {
            await using var holder = Context();
            await using var contender = Context();
            await using var holderTx = await holder.Database.BeginTransactionAsync();
            await using var contenderTx = await contender.Database.BeginTransactionAsync();

            await DatabaseLocks.AcquireForIdentity(holder, identity);

            Assert.False(await TryIdentityLock(contender, identity));
            Assert.True(await TryIdentityLock(contender, identity + "-other"));
            foreach (var resource in Enum.GetValues<DatabaseLock>())
                Assert.True(await TryNamedLock(contender, resource));

            if (commit)
                await holderTx.CommitAsync();
            else
                await holderTx.RollbackAsync();

            Assert.True(await TryIdentityLock(contender, identity));
        }
    }

    [Fact]
    public async Task Locks_require_an_outer_transaction_before_opening_a_connection()
    {
        await using var db = Context();

        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLocks.Acquire(db, DatabaseLock.FeeCatalog));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseLocks.AcquireForIdentity(db, "MEMBER@EXAMPLE.TEST"));

        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    // Nonblocking probes exercise exclusion without timing-dependent waits in the fast suite.
    private static Task<bool> TryNamedLock(AppDbContext db, DatabaseLock resource)
        => db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(0, {(int)resource}) AS \"Value\"").SingleAsync();

    private static Task<bool> TryIdentityLock(AppDbContext db, string identity)
        => db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({identity}, 0)) AS \"Value\"").SingleAsync();
}
