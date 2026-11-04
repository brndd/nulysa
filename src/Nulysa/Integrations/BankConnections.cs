using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Integrations;

public sealed class BankConnection
{
    public Guid Id { get; set; }
    public string Bank { get; set; } = "";
    public string IdentificationHash { get; set; } = "";
    public string Label { get; set; } = "";
    public Guid ApplicationId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AccountUid { get; set; }
    public DateOnly ImportFrom { get; set; }
    public DateTimeOffset ValidUntil { get; set; }
    public bool ExpiryReminderSent { get; set; }
}

public sealed class BankConsentAttempt
{
    public Guid Id { get; set; }
    public long AdministratorId { get; set; }
    public Guid ApplicationId { get; set; }
    public string Bank { get; set; } = "";
    public DateOnly ImportFrom { get; set; }
    public Guid? RenewalId { get; set; }
    public Guid? PreviousSessionId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool CallbackClaimed { get; set; }
    public Guid? SessionId { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? AccountChoices { get; set; }
}

public sealed record BankAccountChoice(Guid Uid, string Hash, string Label);
public sealed record BankChoice(string Name, int MaximumConsentSeconds);

public sealed class BankConnections(HttpClient http, IOptions<BankOptions> options, AppDbContext db, AssociationClock clock, TimeProvider time)
{
    private EnableBankingOptions Settings => options.Value.EnableBanking;
    public bool Enabled => options.Value.Enabled && options.Value.Provider == BankProvider.EnableBanking;
    public DateOnly DefaultImportFrom => Settings.ImportFrom ?? clock.Today;

    public Task<List<BankConnection>> List(CancellationToken ct) =>
        db.BankConnections.AsNoTracking().OrderBy(x => x.Bank).ThenBy(x => x.Label).ToListAsync(ct);

    public async Task<List<BankChoice>> Banks(CancellationToken ct)
    {
        EnsureEnabled();
        var data = await Request(HttpMethod.Get, "aspsps?country=FI&service=AIS&psu_type=business", null, ct);
        return data.GetProperty("aspsps").EnumerateArray()
            .Select(x => new BankChoice(x.GetProperty("name").GetString()!, x.GetProperty("maximum_consent_validity").GetInt32()))
            .Where(x => x.MaximumConsentSeconds > 0)
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Uri> Start(ClaimsPrincipal actor, string? bank, DateOnly? importFrom, Guid? renewalId, CancellationToken ct)
    {
        EnsureEnabled();
        await Authority.RequireAdministrator(db, actor, ct);
        BankConnection? renewal = null;
        if (renewalId is { } id)
        {
            renewal = await db.BankConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new ValidationException("Bank connection was not found.");
            bank = renewal.Bank;
            importFrom = renewal.ImportFrom;
        }
        if (importFrom is not { } firstDate || firstDate == default || firstDate > clock.Today)
            throw new ValidationException("Choose a first import date on or before today.");
        var choice = (await Banks(ct)).SingleOrDefault(x => x.Name == bank)
            ?? throw new ValidationException("Choose an available bank.");
        var attempt = new BankConsentAttempt
        {
            Id = Guid.NewGuid(),
            AdministratorId = Authority.Id(actor),
            ApplicationId = Settings.ApplicationId,
            Bank = choice.Name,
            ImportFrom = firstDate,
            RenewalId = renewalId,
            PreviousSessionId = renewal?.SessionId,
            ExpiresAt = clock.Now.AddMinutes(20)
        };
        var data = await Request(HttpMethod.Post, "auth", new
        {
            access = new { valid_until = clock.Now.AddSeconds(choice.MaximumConsentSeconds), transactions = true, balances = false },
            aspsp = new { name = choice.Name, country = "FI" },
            state = attempt.Id,
            redirect_url = Settings.CallbackUrl,
            psu_type = "business"
        }, ct);
        if (!Uri.TryCreate(data.GetProperty("url").GetString(), UriKind.Absolute, out var target) ||
            target.Scheme != Uri.UriSchemeHttps ||
            target.Host is not ("auth.enablebanking.com" or "tilisy-sandbox.enablebanking.com") || !target.IsDefaultPort ||
            target.UserInfo.Length != 0)
            throw new ValidationException("Enable Banking returned an unexpected authorization URL.");

        await db.BankConsentAttempts.Where(x => x.ExpiresAt <= clock.Now).ExecuteDeleteAsync(ct);
        db.BankConsentAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
        return target;
    }

    public async Task<bool> Complete(ClaimsPrincipal actor, Guid state, string? code, CancellationToken ct)
    {
        EnsureEnabled();
        await Authority.RequireAdministrator(db, actor, ct);
        var pending = Attempts(actor, state).Where(x => !x.CallbackClaimed);
        if (await pending.ExecuteUpdateAsync(x => x.SetProperty(a => a.CallbackClaimed, true), ct) != 1)
            throw new ValidationException("Bank authorization expired or was already used. Start again.");
        var attempt = await Attempts(actor, state).SingleAsync(ct);
        if (string.IsNullOrWhiteSpace(code))
        {
            db.BankConsentAttempts.Remove(attempt);
            await db.SaveChangesAsync(ct);
            throw new ValidationException("Bank authorization was cancelled.");
        }

        var data = await Request(HttpMethod.Post, "sessions", new { code }, ct);
        var session = data.GetProperty("session_id").GetGuid();
        var validUntil = data.GetProperty("access").GetProperty("valid_until").GetDateTimeOffset().ToUniversalTime();
        var bank = data.GetProperty("aspsp");
        if (session == Guid.Empty || validUntil <= clock.Now || bank.GetProperty("name").GetString() != attempt.Bank ||
            bank.GetProperty("country").GetString() != "FI")
            throw new ValidationException("Enable Banking returned an unexpected session.");

        var accounts = data.GetProperty("accounts").EnumerateArray().Select(AccountChoice)
            .Where(x => x.Uid != Guid.Empty && !string.IsNullOrWhiteSpace(x.Hash)).ToList();
        if (accounts.Count == 0)
            throw new ValidationException("No accounts were shared. Start again and choose an account at the bank.");

        if (attempt.RenewalId is { } id)
        {
            var old = await db.BankConnections.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var account = accounts.SingleOrDefault(x => x.Hash == old.IdentificationHash)
                ?? throw new ValidationException("The renewed consent did not include the original bank account.");
            var updated = await db.BankConnections.Where(x => x.Id == id && x.SessionId == attempt.PreviousSessionId)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.SessionId, session)
                    .SetProperty(a => a.ApplicationId, Settings.ApplicationId).SetProperty(a => a.AccountUid, account.Uid)
                    .SetProperty(a => a.ValidUntil, validUntil).SetProperty(a => a.ExpiryReminderSent, false), ct);
            if (updated != 1)
                throw new ValidationException("This bank connection has already changed. Start renewal again.");
            db.BankConsentAttempts.Remove(attempt);
        }
        else
        {
            attempt.SessionId = session;
            attempt.ValidUntil = validUntil;
            attempt.AccountChoices = JsonSerializer.Serialize(accounts);
        }
        await db.SaveChangesAsync(ct);
        return attempt.RenewalId is null;
    }

    public async Task<List<BankAccountChoice>> Choices(ClaimsPrincipal actor, Guid state, CancellationToken ct)
    {
        EnsureEnabled();
        await Authority.RequireAdministrator(db, actor, ct);
        var choices = await Attempts(actor, state).Select(x => x.AccountChoices).SingleOrDefaultAsync(ct);
        return choices is null ? [] : JsonSerializer.Deserialize<List<BankAccountChoice>>(choices)!;
    }

    public async Task Connect(ClaimsPrincipal actor, Guid state, Guid accountUid, CancellationToken ct)
    {
        EnsureEnabled();
        await Authority.RequireAdministrator(db, actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var attempt = await Attempts(actor, state).AsNoTracking().SingleOrDefaultAsync(ct);
        if (attempt?.AccountChoices is null || attempt.SessionId is null || attempt.ValidUntil is not { } expiry || expiry <= clock.Now)
            throw new ValidationException("Bank authorization expired or was already used. Start again.");
        var account = JsonSerializer.Deserialize<List<BankAccountChoice>>(attempt.AccountChoices)!
            .SingleOrDefault(x => x.Uid == accountUid) ?? throw new ValidationException("Choose one of the authorized accounts.");
        if (await Attempts(actor, state).ExecuteDeleteAsync(ct) != 1)
            throw new ValidationException("Bank authorization expired or was already used. Start again.");
        db.BankConnections.Add(new()
        {
            Id = Guid.NewGuid(),
            ApplicationId = attempt.ApplicationId,
            Bank = attempt.Bank,
            Label = account.Label,
            IdentificationHash = account.Hash,
            AccountUid = account.Uid,
            SessionId = attempt.SessionId.Value,
            ImportFrom = attempt.ImportFrom,
            ValidUntil = expiry
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "IX_BankConnections_Bank_IdentificationHash" })
        {
            throw new ValidationException("This bank account is already connected. Use Renew consent.");
        }
        await tx.CommitAsync(ct);
    }

    private IQueryable<BankConsentAttempt> Attempts(ClaimsPrincipal actor, Guid id)
    {
        var owner = Authority.Id(actor);
        return db.BankConsentAttempts.Where(x => x.Id == id && x.AdministratorId == owner &&
            x.ApplicationId == Settings.ApplicationId && x.ExpiresAt > clock.Now);
    }

    private void EnsureEnabled()
    {
        if (!Enabled)
            throw new ValidationException("Enable Banking is disabled.");
    }

    private async Task<JsonElement> Request(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await EnableBankingTransactions.Sign(Settings, time, ct));
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
    }

    private static BankAccountChoice AccountChoice(JsonElement account)
    {
        var name = account.TryGetProperty("name", out var nameField) ? nameField.GetString() : null;
        var iban = account.TryGetProperty("account_id", out var id) && id.ValueKind == JsonValueKind.Object &&
            id.TryGetProperty("iban", out var ibanField) ? ibanField.GetString() : null;
        var uid = account.GetProperty("uid").GetGuid();
        var suffix = iban is { Length: >= 4 } ? iban[^4..] : uid.ToString("N")[^8..];
        return new(uid, account.GetProperty("identification_hash").GetString() ?? "", $"{name ?? "Account"} · •••• {suffix}");
    }
}
