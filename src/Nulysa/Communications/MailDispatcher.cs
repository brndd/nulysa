using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nulysa.Data;

namespace Nulysa.Communications;

public sealed class MailDispatcher(AppDbContext db, MailRenderer renderer, IMailTransport transport, TimeProvider clock, IOptions<MailOptions> options,
    ILogger<MailDispatcher> logger)
{
    public Task<TimeSpan?> Dispatch(Guid id, CancellationToken ct = default, int queueVersion = 0)
        => Dispatch(id, queueVersion, null, ct);

    internal async Task<TimeSpan?> Dispatch(Guid id, int queueVersion,
        Func<DbTransaction, TimeSpan, CancellationToken, Task>? defer, CancellationToken ct)
    {
        OutgoingMail? message;
        int attempt;
        Guid? rejectedApplication;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Membership and preference changes lock the account first. Use the same order when claiming bulk mail.
            var recipient = await db.Deliveries.AsNoTracking().Where(x => x.Id == id && x.CampaignId != null)
                .Select(x => x.UserId).SingleOrDefaultAsync(ct);
            if (recipient is { } userId)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR NO KEY UPDATE", ct);
            var delivery = await Lock(id, ct);
            if (delivery is null || delivery.QueueVersion != queueVersion)
                return null;
            if (delivery.State == DeliveryState.Sending)
            {
                delivery.State = DeliveryState.Review;
                delivery.ProtectedTestMessage = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return null;
            }
            if (delivery.State != DeliveryState.Pending)
                return null;
            if (delivery.RetryNotBefore is { } retryAt && retryAt > clock.GetUtcNow())
                return await Defer(tx, retryAt, ct);

            if (MailQueue.IsBulk(delivery.Kind))
            {
                // Skip rendering a bulk message while quota is unavailable. Reserve the slot under the lock below.
                var snapshot = await db.MailRateWindows.AsNoTracking().SingleOrDefaultAsync(ct);
                if (snapshot is not null && RateBlockedUntil(snapshot, clock.GetUtcNow()) is { } blockedUntil)
                    return await Defer(tx, blockedUntil, ct);
            }

            try
            {
                message = await renderer.Render(delivery, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // A broken template or renderer must not stay Pending and retry forever.
                logger.LogWarning("Mail render failed for delivery {DeliveryId} ({ErrorType}); recording review.",
                    id, error.GetType().Name);
                delivery.State = DeliveryState.Review;
                delivery.ProtectedTestMessage = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return null;
            }
            if (message is null)
            {
                delivery.State = DeliveryState.Cancelled;
                delivery.ProtectedTestMessage = null;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return null;
            }

            // One persisted dispatch rate, shared by every worker and every process.
            await DatabaseLocks.Acquire(db, DatabaseLock.MailDispatchRate, ct);
            var window = await db.MailRateWindows.SingleOrDefaultAsync(ct);
            if (window is null)
            {
                window = new() { StartedAt = clock.GetUtcNow() };
                db.MailRateWindows.Add(window);
            }
            var now = clock.GetUtcNow();
            if (RateBlockedUntil(window, now) is { } blockedUntilAfterRender)
                return await Defer(tx, blockedUntilAfterRender, ct);
            if (window.StartedAt.AddMinutes(1) <= now)
            {
                window.StartedAt = now;
                window.Attempts = 0;
            }
            window.Attempts++;

            delivery.State = DeliveryState.Sending;
            delivery.RetryNotBefore = null;
            delivery.StartedAt = clock.GetUtcNow();
            attempt = ++delivery.Attempts;
            rejectedApplication = delivery.Kind == MailKind.Rejection ? delivery.ApplicationId : null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        SendResult result;
        try
        {
            result = await transport.Send(id, message, ct);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Mail transport threw for delivery {DeliveryId}; recording the outcome as uncertain.", id);
            result = new(SendOutcome.Uncertain);
        }

        // Record acceptance/ambiguity even when shutdown cancelled the transport operation.
        DateTimeOffset? retryNotBefore = null;
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var tx = await db.Database.BeginTransactionAsync(finish.Token))
        {
            db.ChangeTracker.Clear();
            // Match application commands' lock order; never hold a delivery while waiting for its applicant.
            if (rejectedApplication is { } applicationId)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Applications\" WHERE \"Id\" = {applicationId} FOR UPDATE", finish.Token);

            var delivery = await Lock(id, finish.Token);
            var now = clock.GetUtcNow();
            DateTimeOffset? providerPause = null;
            if (result.Outcome == SendOutcome.ProviderThrottled)
            {
                // Apply the pause even if the recipient was erased during the SMTP exchange.
                await DatabaseLocks.Acquire(db, DatabaseLock.MailDispatchRate, finish.Token);
                var window = await db.MailRateWindows.SingleAsync(finish.Token);
                providerPause = now.AddMinutes(1);
                if (window.ProviderPausedUntil is null || window.ProviderPausedUntil < providerPause)
                    window.ProviderPausedUntil = providerPause;
                providerPause = window.ProviderPausedUntil;
            }
            if (delivery is null || delivery.QueueVersion != queueVersion || delivery.Attempts != attempt)
            {
                if (providerPause is not null)
                {
                    await db.SaveChangesAsync(finish.Token);
                    await tx.CommitAsync(finish.Token);
                }
                return null;
            }

            var firstAcceptance = delivery.AcceptedAt is null;
            delivery.State = result.Outcome switch
            {
                SendOutcome.Accepted => DeliveryState.Accepted,
                SendOutcome.Throttled or SendOutcome.ProviderThrottled when attempt < delivery.AttemptLimit => DeliveryState.Pending,
                SendOutcome.Rejected or SendOutcome.Throttled or SendOutcome.ProviderThrottled => DeliveryState.Failed,
                _ => DeliveryState.Review
            };
            if (delivery.State == DeliveryState.Pending)
                retryNotBefore = delivery.RetryNotBefore = providerPause ?? now.AddSeconds(30);
            else
                delivery.ProtectedTestMessage = null;
            if (result.Outcome == SendOutcome.Accepted)
            {
                delivery.AcceptedAt = clock.GetUtcNow();
                delivery.ProviderId = result.ProviderId;
                // Keep the aggregate when member erasure or retention removes the delivery record.
                if (firstAcceptance && delivery.CampaignId is { } campaignId && delivery.Kind != MailKind.CampaignTest)
                    await db.MailCampaigns.Where(x => x.Id == campaignId)
                        .ExecuteUpdateAsync(x => x.SetProperty(c => c.AcceptedRecipients, c => c.AcceptedRecipients + 1), finish.Token);
                // Never call enqueueing delivery. Rejection purges applicant data only after provider acceptance.
                if (delivery.Kind == MailKind.Rejection && delivery.ApplicationId is { } appId)
                    await db.Applications.Where(x => x.Id == appId && x.RejectedAt != null).ExecuteDeleteAsync(finish.Token);
            }
            await db.SaveChangesAsync(finish.Token);
            await tx.CommitAsync(finish.Token);
        }

        if (retryNotBefore is null)
            return null;
        if (defer is null)
            return retryNotBefore.Value - clock.GetUtcNow();

        // The known SMTP result is durable before touching Quartz. A failed trigger write
        // can be retried by Quartz or repaired from the Pending delivery after its delay.
        db.ChangeTracker.Clear();
        await using (var tx = await db.Database.BeginTransactionAsync(finish.Token))
        {
            var delivery = await Lock(id, finish.Token);
            if (delivery is null || delivery.State != DeliveryState.Pending || delivery.QueueVersion != queueVersion ||
                delivery.Attempts != attempt || delivery.RetryNotBefore is not { } persistedRetryAt)
                return null;
            return await Defer(tx, persistedRetryAt, finish.Token);
        }

        async Task<TimeSpan> Defer(IDbContextTransaction transaction, DateTimeOffset until, CancellationToken token)
        {
            var delay = TimeSpan.FromSeconds(Math.Max(1, (until - clock.GetUtcNow()).TotalSeconds));
            if (defer is not null)
            {
                await defer(transaction.GetDbTransaction(), delay, token);
                await transaction.CommitAsync(token);
            }
            return delay;
        }
    }

    private DateTimeOffset? RateBlockedUntil(MailRateWindow window, DateTimeOffset now)
    {
        var quotaResetsAt = window.StartedAt.AddMinutes(1);
        var blockedUntil = window.Attempts >= options.Value.MaxPerMinute && quotaResetsAt > now ? (DateTimeOffset?)quotaResetsAt : null;
        if (window.ProviderPausedUntil is { } pause && pause > now && (blockedUntil is null || pause > blockedUntil))
            return pause;
        return blockedUntil;
    }

    private Task<Delivery?> Lock(Guid id, CancellationToken ct)
        => db.Deliveries.FromSqlInterpolated($"SELECT * FROM \"Deliveries\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}
