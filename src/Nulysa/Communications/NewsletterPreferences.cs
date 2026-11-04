using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;

namespace Nulysa.Communications;

public sealed class NewsletterPreferences(AppDbContext db, IDataProtectionProvider protection, IOptions<MailOptions> options)
{
    public const string UnsubscribePath = "/communications/newsletter/unsubscribe";
    private ITimeLimitedDataProtector Tokens => protection.CreateProtector("NewsletterUnsubscribe.v1").ToTimeLimitedDataProtector();

    public async Task<bool> Get(ClaimsPrincipal actor, CancellationToken ct = default)
    {
        var id = Authority.Id(actor);
        return await db.Users.Where(x => x.Id == id && x.Admitted && x.PendingDeletionAt == null)
            .Select(x => (bool?)x.EmailNewsletters).SingleOrDefaultAsync(ct) ?? throw new UnauthorizedAccessException();
    }

    public async Task Set(ClaimsPrincipal actor, bool enabled, CancellationToken ct = default)
    {
        var id = Authority.Id(actor);
        var stamp = Guid.NewGuid().ToString();
        var updated = await db.Users.Where(x => x.Id == id && x.Admitted && x.PendingDeletionAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.EmailNewsletters, enabled).SetProperty(u => u.ConcurrencyStamp, stamp), ct);
        if (updated == 0)
            throw new UnauthorizedAccessException();
    }

    internal string UnsubscribeLink(User user)
    {
        var url = new Uri(new Uri(options.Value.PublicUrl), UnsubscribePath.TrimStart('/')).AbsoluteUri;
        var token = Tokens.Protect(JsonSerializer.Serialize(new Subscription(user.Id, user.Email!)), TimeSpan.FromDays(90));
        return QueryHelpers.AddQueryString(url, "token", token);
    }

    internal bool IsValidUnsubscribeToken(string? token) => ReadSubscription(token) is not null;

    public async Task<bool> Unsubscribe(string? token, CancellationToken ct = default)
    {
        if (ReadSubscription(token) is not { } subscription)
            return false;

        // Match the verified address from the send; an old address cannot change a new address's preference.
        var stamp = Guid.NewGuid().ToString();
        await db.Users.Where(x => x.Id == subscription.UserId && x.Admitted && x.EmailConfirmed && x.Email == subscription.Address && x.EmailNewsletters)
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.EmailNewsletters, false).SetProperty(u => u.ConcurrencyStamp, stamp), ct);
        return true;
    }

    private Subscription? ReadSubscription(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2048)
            return null;
        try
        {
            return JsonSerializer.Deserialize<Subscription>(Tokens.Unprotect(token));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private sealed record Subscription(long UserId, string Address);
}
