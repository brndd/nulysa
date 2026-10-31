using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Operations;
using Quartz;

namespace Nulysa.Integrations;

public sealed class BankOptions
{
    public bool Enabled { get; set; }
    public string SecretId { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string[] Accounts { get; set; } = [];
    public int StaleHours { get; set; } = 26;
}

public sealed class BankSyncState
{
    public string Account { get; set; } = "";
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastAlert { get; set; }
    public string Status { get; set; } = "Never synchronized";
}

public static class GoCardlessTransactions
{
    public static List<ReceiptInput> Parse(string account, JsonElement payload)
    {
        var result = new List<ReceiptInput>();
        foreach (var transaction in payload.GetProperty("transactions").GetProperty("booked").EnumerateArray())
        {
            var id = Text(transaction, "transactionId");
            if (string.IsNullOrWhiteSpace(id))
                id = Text(transaction, "internalTransactionId");
            if (string.IsNullOrWhiteSpace(id))
                throw new ValidationException("Booked transaction lacks a stable provider identifier.");
            var amountObject = transaction.GetProperty("transactionAmount");
            if (!decimal.TryParse(Text(amountObject, "amount"), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
                !DateOnly.TryParseExact(Text(transaction, "bookingDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var booked))
                throw new ValidationException("A booked transaction requires a valid amount and booking date.");
            var reference = Text(transaction, "remittanceInformationStructured");
            if (reference.Length == 0 && transaction.TryGetProperty("remittanceInformationStructuredArray", out var references))
            {
                var values = references.EnumerateArray().Select(x => x.GetString() ?? "").Distinct().ToList();
                reference = values.Count == 1 ? values[0] : string.Join(" / ", values);
            }
            var message = Text(transaction, "remittanceInformationUnstructured");
            if (transaction.TryGetProperty("remittanceInformationUnstructuredArray", out var messages))
                message = string.Join("\n", new[] { message }.Concat(messages.EnumerateArray().Select(x => x.GetString())).Where(x => !string.IsNullOrEmpty(x)).Distinct());
            var counterparty = Text(transaction, amount > 0 ? "debtorName" : "creditorName");
            if (transaction.TryGetProperty(amount > 0 ? "debtorAccount" : "creditorAccount", out var bankAccount))
                counterparty = string.Join(" / ", new[] { counterparty, Text(bankAccount, "iban") }.Where(x => x.Length > 0));
            if (amount != 0)
                result.Add(new("gocardless", account, id, amount, Text(amountObject, "currency"), booked, reference, message, counterparty));
        }
        return result;
    }

    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
}

public sealed class BankFeed(HttpClient http, IOptions<BankOptions> options, IServiceScopeFactory scopes, AppDbContext db,
    TimeProvider clock, ISchedulerFactory schedulers, MailQueue mail)
{
    public async Task Sync(CancellationToken ct = default)
    {
        if (!options.Value.Enabled)
            return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var accounts = options.Value.Accounts;
        var started = clock.GetUtcNow();
        try
        {
            using var tokenResponse = await http.PostAsJsonAsync("token/new/", new { secret_id = options.Value.SecretId, secret_key = options.Value.SecretKey }, deadline.Token);
            tokenResponse.EnsureSuccessStatusCode();
            using var tokenJson = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
            var token = tokenJson.RootElement.GetProperty("access").GetString();
            foreach (var account in accounts)
            {
                try
                {
                    await Update(account, false, "Synchronizing", ct);
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"accounts/{Uri.EscapeDataString(account)}/transactions/");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var response = await http.SendAsync(request, deadline.Token);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new BankAuthorizationException();
                    response.EnsureSuccessStatusCode();
                    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(deadline.Token), cancellationToken: deadline.Token);
                    foreach (var input in GoCardlessTransactions.Parse(account, json.RootElement))
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<BillingCommands>().Import(input, deadline.Token);
                    }
                    await Update(account, true, "Current", ct);
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    await Update(account, false, error is BankAuthorizationException ? "Authorization renewal required" : "Import failed", ct);
                    await Alert(account, ct);
                    throw;
                }
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            foreach (var account in accounts)
            {
                var state = await db.BankSyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Account == account, ct);
                if (state is null || state.LastAttempt < started || state.Status == "Synchronizing")
                    await Update(account, false, "Authentication or feed failed", ct);
                await Alert(account, ct);
            }
            throw new InvalidOperationException("Bank synchronization failed; inspect bank status and renew provider authorization if required.");
        }
    }

    private async Task Update(string account, bool success, string status, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BankSyncStates" ("Account", "LastAttempt", "LastSuccess", "Status") VALUES ({account}, {now}, {(success ? (DateTimeOffset?)now : null)}, {status})
            ON CONFLICT ("Account") DO UPDATE SET "LastAttempt" = EXCLUDED."LastAttempt", "Status" = EXCLUDED."Status",
              "LastSuccess" = COALESCE(EXCLUDED."LastSuccess", "BankSyncStates"."LastSuccess")
            """, ct);
    }

    public async Task CheckStale(CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return;
        foreach (var account in options.Value.Accounts)
        {
            var state = await db.BankSyncStates.AsNoTracking().SingleOrDefaultAsync(x => x.Account == account, ct);
            if (state?.LastSuccess is null || state.LastSuccess < clock.GetUtcNow().AddHours(-options.Value.StaleHours))
            {
                if (state is null)
                    await Update(account, false, "Never synchronized", ct);
                await Alert(account, ct);
            }
        }
    }

    private async Task Alert(string account, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        db.ChangeTracker.Clear();
        var state = await db.BankSyncStates.FromSqlInterpolated($"SELECT * FROM \"BankSyncStates\" WHERE \"Account\" = {account} FOR UPDATE").SingleAsync(ct);
        if (state.LastAlert > clock.GetUtcNow().AddHours(-24))
            return;
        var administrators = await (from assignment in db.UserRoles
                                    join role in db.Roles on assignment.RoleId equals role.Id
                                    join user in db.Users on assignment.UserId equals user.Id
                                    where role.NormalizedName == "ADMINISTRATOR" && user.Admitted && user.EmailConfirmed && user.PendingDeletionAt == null
                                    select user.Id).ToListAsync(ct);
        foreach (var id in administrators)
            await mail.Add(scheduler, Guid.NewGuid(), MailKind.BankAlert, id, ct: ct);
        if (administrators.Count > 0)
            state.LastAlert = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private sealed class BankAuthorizationException : Exception;
}

[DisallowConcurrentExecution]
public sealed class BankSyncJob(BankFeed feed, AppDbContext db, TimeProvider clock) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await feed.Sync(cancellationToken);
        await Observations.Record(db, "bank-sync", clock.GetUtcNow(), cancellationToken);
    }
}
