using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nulysa.Data;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Nulysa.Accounts;

public static class Oidc
{
    public static void Configure(OpenIddictServerBuilder server, IConfiguration config, bool development)
    {
        var issuer = new Uri(config["Oidc:Issuer"] ?? throw new InvalidOperationException("Oidc:Issuer is required."));
        if (!development && issuer.Scheme != "https") throw new InvalidOperationException("OIDC requires HTTPS.");
        server.SetIssuer(issuer)
            .SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token")
            .SetUserInfoEndpointUris("connect/userinfo")
            .AllowAuthorizationCodeFlow().AllowRefreshTokenFlow()
            .RequireProofKeyForCodeExchange()
            .RegisterScopes(Scopes.Email, Scopes.Profile)
            .SetRefreshTokenLifetime(TimeSpan.FromDays(365))
            .UseReferenceRefreshTokens()
            .DisableAccessTokenEncryption()
            .AddSigningKey(LoadKey(config["Oidc:SigningKeyPath"]))
            .AddEncryptionKey(LoadKey(config["Oidc:EncryptionKeyPath"]));
        server.Configure(options => options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

        // OpenIddict's built-in check is strictly '<'; the policy's expiry boundary is inclusive.
        server.AddEventHandler<OpenIddictServerEvents.ValidateTokenContext>(handler => handler
            .SetOrder(OpenIddictServerHandlers.Protection.ValidateExpirationDate.Descriptor.Order + 1)
            .UseInlineHandler(context =>
            {
                if (context.Principal?.GetExpirationDate() is { } expiry && expiry <= context.Options.TimeProvider.GetUtcNow())
                    context.Reject(Errors.InvalidToken, "The token has expired.");
                return default;
            }));

        var aspnet = server.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough();
        if (development) aspnet.DisableTransportSecurityRequirement();
    }

    private static RsaSecurityKey LoadKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Persistent OIDC key paths are required.");
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(path));
        _ = rsa.ExportParameters(includePrivateParameters: true);
        if (rsa.KeySize < 2048) throw new InvalidOperationException("OIDC RSA keys must be at least 2048 bits.");
        var key = new RsaSecurityKey(rsa);
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }
}

[ApiExplorerSettings(IgnoreApi = true)]
public sealed class OidcController(AppDbContext db, UserManager<User> users) : Controller
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    [HttpGet("~/connect/authorize")]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        var request = HttpContext.GetOpenIddictServerRequest()!;
        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!cookie.Succeeded)
        {
            if (request.HasPromptValue(PromptValues.None)) return Deny(Errors.LoginRequired);
            return Challenge(new AuthenticationProperties { RedirectUri = Request.PathBase + Request.Path + Request.QueryString },
                IdentityConstants.ApplicationScheme);
        }
        var user = await users.GetUserAsync(cookie.Principal!);
        if (user is null || !user.MayLogIn) return Deny(Errors.AccessDenied);
        if (request.HasPromptValue(PromptValues.Login) || request.MaxAge is not null)
        {
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            var query = QueryHelpers.ParseQuery(Request.QueryString.Value);
            query.Remove("prompt");
            query.Remove("max_age");
            return Challenge(new AuthenticationProperties
            {
                RedirectUri = Request.PathBase + Request.Path + QueryString.Create(query)
            }, IdentityConstants.ApplicationScheme);
        }
        return SignIn(Principal(user, request.GetScopes()), Scheme);
    }

    [HttpPost("~/connect/token"), IgnoreAntiforgeryToken]
    public async Task<IActionResult> Token(CancellationToken ct)
    {
        var result = await HttpContext.AuthenticateAsync(Scheme);
        if (!result.Succeeded) return Deny(Errors.InvalidGrant);
        var subject = result.Principal!.GetClaim(Claims.Subject);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.OidcSubject == subject, ct);
        if (user is null || !user.MayLogIn) return Deny(Errors.InvalidGrant);
        var request = HttpContext.GetOpenIddictServerRequest()!;
        var scopes = result.Principal.GetScopes();
        var disclosed = request.IsRefreshTokenGrantType() && !string.IsNullOrEmpty(request.Scope)
            ? scopes.Intersect(request.GetScopes()) : scopes;
        return SignIn(Principal(user, scopes, result.Principal, disclosed), Scheme);
    }

    [HttpGet("~/connect/userinfo")]
    public async Task<IActionResult> UserInfo(CancellationToken ct)
    {
        var result = await HttpContext.AuthenticateAsync(Scheme);
        if (!result.Succeeded) return Challenge(Scheme);
        var subject = result.Principal!.GetClaim(Claims.Subject);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.OidcSubject == subject, ct);
        if (user is null || !user.MayLogIn) return Deny(Errors.InvalidToken);
        var claims = new Dictionary<string, object?> { [Claims.Subject] = user.OidcSubject };
        if (result.Principal.HasScope(Scopes.Email))
        {
            claims[Claims.Email] = user.Email;
            claims[Claims.EmailVerified] = user.EmailConfirmed;
        }
        if (result.Principal.HasScope(Scopes.Profile)) claims[Claims.Name] = user.Name;
        return Ok(claims);
    }

    private static ClaimsPrincipal Principal(User user, IEnumerable<string> scopes, ClaimsPrincipal? existing = null,
        IEnumerable<string>? disclosedScopes = null)
    {
        var principal = existing ?? new ClaimsPrincipal(new ClaimsIdentity(Scheme, Claims.Name, Claims.Role));
        principal.SetClaim(Claims.Subject, user.OidcSubject).SetClaim(Claims.Email, user.Email)
            .SetClaim(Claims.EmailVerified, user.EmailConfirmed).SetClaim(Claims.Name, user.Name);
        principal.SetScopes(scopes);
        principal.SetResources("nulysa");
        var disclosed = (disclosedScopes ?? scopes).ToHashSet(StringComparer.Ordinal);
        principal.SetDestinations(claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Email or Claims.EmailVerified when disclosed.Contains(Scopes.Email) => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Name when disclosed.Contains(Scopes.Profile) => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => []
        });
        return principal;
    }

    private ForbidResult Deny(string error) => Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "This account or credential is unavailable."
    }), Scheme);
}
