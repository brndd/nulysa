using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Communications;

public sealed class MailDispatcher(AppDbContext db, MailRenderer renderer, IMailTransport transport, TimeProvider clock, IOptions<MailOptions> options,
    ILogger<MailDispatcher> logger)
{
    public async Task Dispatch(Guid id, CancellationToken ct = default)
    {
        OutgoingMail? message;
        int attempt;
        Guid? rejectedApplication;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // One persisted provider-wide dispatch rate, shared by every worker instance.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72134503)", ct);
            var delivery = await Lock(id, ct);
            if (delivery is null) return;
            if (delivery.State == DeliveryState.Sending)
            {
                delivery.State = DeliveryState.Review;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return;
            }
            if (delivery.State != DeliveryState.Pending) return;
            message = await renderer.Render(delivery, ct);
            if (message is null)
            {
                delivery.State = DeliveryState.Cancelled;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return;
            }
            var window = await db.MailRateWindows.SingleOrDefaultAsync(ct);
            if (window is null) { window = new() { StartedAt = clock.GetUtcNow() }; db.MailRateWindows.Add(window); }
            if (window.StartedAt.AddMinutes(1) <= clock.GetUtcNow()) { window.StartedAt = clock.GetUtcNow(); window.Attempts = 0; }
            if (window.Attempts >= options.Value.MaxPerMinute) throw new JobExecutionException("Mail provider rate limit; dispatch deferred.");
            window.Attempts++;
            delivery.State = DeliveryState.Sending;
            delivery.StartedAt = clock.GetUtcNow();
            attempt = ++delivery.Attempts;
            rejectedApplication = delivery.Kind == MailKind.Rejection ? delivery.ApplicationId : null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        SendResult result;
        try { result = await transport.Send(id, message, ct); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Mail transport threw for delivery {DeliveryId}; recording the outcome as uncertain.", id);
            result = new(SendOutcome.Uncertain);
        }
        // Record acceptance/ambiguity even when shutdown cancelled the HTTP operation.
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var tx = await db.Database.BeginTransactionAsync(finish.Token))
        {
            db.ChangeTracker.Clear();
            // Match application commands' lock order; never hold a delivery while waiting for its applicant.
            if (rejectedApplication is { } applicationId)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Applications\" WHERE \"Id\" = {applicationId} FOR UPDATE", finish.Token);
            var delivery = await Lock(id, finish.Token);
            if (delivery is null || delivery.Attempts != attempt) return;
            delivery.State = result.Outcome switch
            {
                SendOutcome.Accepted => DeliveryState.Accepted,
                SendOutcome.Throttled when attempt < 3 => DeliveryState.Pending,
                SendOutcome.Rejected or SendOutcome.Throttled => DeliveryState.Failed,
                _ => DeliveryState.Review
            };
            if (result.Outcome == SendOutcome.Accepted)
            {
                delivery.AcceptedAt = clock.GetUtcNow(); delivery.ProviderId = result.ProviderId;
                // Never call enqueueing delivery. Rejection purges applicant data only after provider acceptance.
                if (delivery.Kind == MailKind.Rejection && delivery.ApplicationId is { } appId)
                    await db.Applications.Where(x => x.Id == appId && x.RejectedAt != null).ExecuteDeleteAsync(finish.Token);
            }
            await db.SaveChangesAsync(finish.Token);
            await tx.CommitAsync(finish.Token);
        }
        if (result.Outcome == SendOutcome.Throttled && attempt < 3) throw new JobExecutionException("Mail provider throttled dispatch.");
    }

    private Task<Delivery?> Lock(Guid id, CancellationToken ct)
        => db.Deliveries.FromSqlInterpolated($"SELECT * FROM \"Deliveries\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
}
