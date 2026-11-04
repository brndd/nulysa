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
            if (window.StartedAt.AddMinutes(1) <= clock.GetUtcNow())
            {
                window.StartedAt = clock.GetUtcNow();
                window.Attempts = 0;
            }
            if (window.Attempts >= options.Value.MaxPerMinute)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, (window.StartedAt.AddMinutes(1) - clock.GetUtcNow()).TotalSeconds));
                if (defer is not null)
                {
                    await defer(tx.GetDbTransaction(), delay, ct);
                    await tx.CommitAsync(ct);
                }
                return delay;
            }
            window.Attempts++;

            delivery.State = DeliveryState.Sending;
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
        var retryDelay = TimeSpan.FromSeconds(30);
        var deferred = false;
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var tx = await db.Database.BeginTransactionAsync(finish.Token))
        {
            db.ChangeTracker.Clear();
            // Match application commands' lock order; never hold a delivery while waiting for its applicant.
            if (rejectedApplication is { } applicationId)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Applications\" WHERE \"Id\" = {applicationId} FOR UPDATE", finish.Token);

            var delivery = await Lock(id, finish.Token);
            if (delivery is null || delivery.QueueVersion != queueVersion || delivery.Attempts != attempt)
                return null;

            delivery.State = result.Outcome switch
            {
                SendOutcome.Accepted => DeliveryState.Accepted,
                SendOutcome.Throttled when attempt < delivery.AttemptLimit => DeliveryState.Pending,
                SendOutcome.Rejected or SendOutcome.Throttled => DeliveryState.Failed,
                _ => DeliveryState.Review
            };
            deferred = delivery.State == DeliveryState.Pending;
            if (!deferred)
                delivery.ProtectedTestMessage = null;
            if (result.Outcome == SendOutcome.Accepted)
            {
                delivery.AcceptedAt = clock.GetUtcNow();
                delivery.ProviderId = result.ProviderId;
                // Never call enqueueing delivery. Rejection purges applicant data only after provider acceptance.
                if (delivery.Kind == MailKind.Rejection && delivery.ApplicationId is { } appId)
                    await db.Applications.Where(x => x.Id == appId && x.RejectedAt != null).ExecuteDeleteAsync(finish.Token);
            }
            await db.SaveChangesAsync(finish.Token);
            if (deferred && defer is not null)
                await defer(tx.GetDbTransaction(), retryDelay, finish.Token);
            await tx.CommitAsync(finish.Token);
        }
        return deferred ? retryDelay : null;
    }

    private Task<Delivery?> Lock(Guid id, CancellationToken ct)
        => db.Deliveries.FromSqlInterpolated($"SELECT * FROM \"Deliveries\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}
