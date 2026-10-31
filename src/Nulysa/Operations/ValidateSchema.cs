using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Billing;
using Nulysa.Data;
using Quartz;

namespace Nulysa.Operations;

public sealed class ValidateSchema(IServiceScopeFactory scopes, ISchedulerFactory schedulers) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("Pending schema migrations; run the release maintenance command first.");
        // A missing or mismatched renderer must fail startup, not every invoice later.
        InvoicePdf.VerifyRenderer(scope.ServiceProvider.GetRequiredService<IOptions<BillingOptions>>().Value.TypstPath);
        // Initializing validates Quartz's schema without starting acquisition in a web process.
        await schedulers.GetScheduler(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
