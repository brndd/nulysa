using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nulysa.Accounts;
using Nulysa.Operations;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Nulysa.Tests;

public abstract class OidcTest : DatabaseTest
{
    protected const string Email = "synthetic@example.test";
    protected const string Password = "Synthetic-password-123!";
    protected const string Secret = "synthetic-client-secret-not-for-production";
    protected const string Redirect = "https://broker.example.test/callback";
    protected readonly ManualClock Clock = new();

    protected async Task<User> CreateUser()
    {
        await using var scope = Maintenance.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { Email = Email, UserName = Email, EmailConfirmed = true, Admitted = true, Name = "Synthetic Member" };

        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return user;
    }

    protected async Task CreateClient(string redirect = Redirect, string id = "synthetic-broker")
    {
        await using var scope = Maintenance.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = id,
            ClientSecret = Secret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri(redirect) },
            Permissions = { Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code, Permissions.Scopes.Email, Permissions.Scopes.Profile },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });
    }

    protected async Task<WebApplication> StartProvider()
    {
        var app = Build("web", b => b.Services.AddSingleton<TimeProvider>(Clock));
        app.Urls.Add(Settings["Oidc:Issuer"]!);
        await app.StartAsync();
        return app;
    }

    // Existing request journeys assert English copy explicitly; localisation tests opt into automatic selection.
    protected HttpClient Client(CookieContainer? cookies = null, string? language = "en")
    {
        cookies ??= new CookieContainer();
        if (language is not null)
            cookies.Add(new Uri(Settings["Oidc:Issuer"]!), new Cookie("Nulysa.Language", language, "/"));
        return new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
        { BaseAddress = new Uri(Settings["Oidc:Issuer"]!), Timeout = TimeSpan.FromSeconds(15) };
    }

    protected static async Task<HttpResponseMessage> Login(HttpClient client, string email = Email)
    {
        var html = await client.GetStringAsync("Account/Login");
        var match = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>");

        Assert.True(match.Success, html);
        return await client.PostAsync("Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value)
        }));
    }

    protected static string AuthorizeUrl(string scope = "openid email profile offline_access", string redirect = Redirect,
        string method = "S256", string? challenge = null) => QueryHelpers.AddQueryString("connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = "synthetic-broker",
            ["redirect_uri"] = redirect,
            ["scope"] = scope,
            ["state"] = "synthetic-state",
            ["nonce"] = "synthetic-nonce",
            ["code_challenge"] = challenge ?? Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))),
            ["code_challenge_method"] = method
        });
    protected const string Verifier = "synthetic-pkce-verifier-with-at-least-43-ascii-characters";

    protected static async Task<string> Code(HttpClient client, string scope = "openid email profile offline_access")
    {
        var response = await client.GetAsync(AuthorizeUrl(scope));

        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);

        Assert.Equal("synthetic-state", query["state"]);
        Assert.True(query.ContainsKey("code"), response.Headers.Location.ToString());
        return query["code"].ToString();
    }

    protected static Task<HttpResponseMessage> Redeem(HttpClient client, string code, string secret = Secret, string verifier = Verifier)
        => client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = "synthetic-broker",
            ["client_secret"] = secret,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier
        }));

    protected static Task<HttpResponseMessage> Refresh(HttpClient client, JsonElement tokens)
        => client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
            ["client_id"] = "synthetic-broker",
            ["client_secret"] = Secret
        }));

    protected static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }

    protected async Task<JsonElement> Tokens(HttpClient client)
    {

        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        return await Json(await Redeem(client, await Code(client)));
    }
}

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override DateTimeOffset GetUtcNow() => Now;
}

[Trait("Group", "Protocol")]
public sealed class OidcTests : OidcTest
{
    [Fact]
    public async Task Code_flow_signature_issuer_audience_nonce_scoped_claims_and_email_independent_subject()
    {
        var user = await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        var tokens = await Tokens(client);
        var discovery = await Json(await client.GetAsync(".well-known/openid-configuration"));

        Assert.Equal(Settings["Oidc:Issuer"], discovery.GetProperty("issuer").GetString());
        var jwks = await client.GetStringAsync(discovery.GetProperty("jwks_uri").GetString());
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.GetProperty("id_token").GetString(), new TokenValidationParameters
        {
            ValidIssuer = Settings["Oidc:Issuer"],
            ValidAudience = "synthetic-broker",
            IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.Zero
        });

        Assert.True(validation.IsValid, validation.Exception?.ToString());
        var jwt = (JsonWebToken)validation.SecurityToken;

        Assert.Equal("synthetic-nonce", jwt.GetClaim("nonce").Value);
        Assert.Equal(user.OidcSubject, jwt.Subject);
        Assert.Equal(Email, jwt.GetClaim("email").Value);
        await using (var db = Context())
            await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Email, "changed@example.test")
                .SetProperty(u => u.NormalizedEmail, "CHANGED@EXAMPLE.TEST"));
        var changed = await Json(await Redeem(client, await Code(client)));
        var info = await UserInfo(client, changed);

        Assert.Equal(user.OidcSubject, info.GetProperty("sub").GetString());
        Assert.Equal("changed@example.test", info.GetProperty("email").GetString());
        var minimal = await Json(await Redeem(client, await Code(client, "openid")));
        var scoped = await UserInfo(client, minimal);

        Assert.Equal(["sub"], scoped.EnumerateObject().Select(x => x.Name));
    }

    private static async Task<JsonElement> UserInfo(HttpClient client, JsonElement tokens)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "connect/userinfo");
        request.Headers.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());
        return await Json(await client.SendAsync(request));
    }

    [Fact]
    public async Task Narrow_refresh_scope_does_not_disclose_profile_claims_or_expand_the_original_grant()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        var original = await Tokens(client);
        var narrow = await Json(await client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = original.GetProperty("refresh_token").GetString()!,
            ["client_id"] = "synthetic-broker",
            ["client_secret"] = Secret,
            ["scope"] = "openid"
        })));
        foreach (var name in new[] { "access_token", "id_token" })
        {
            var jwt = new JsonWebToken(narrow.GetProperty(name).GetString());

            Assert.False(jwt.TryGetClaim("email", out _));
            Assert.False(jwt.TryGetClaim("name", out _));
        }

        Assert.Equal(["sub"], (await UserInfo(client, narrow)).EnumerateObject().Select(x => x.Name));
        // A replacement refresh token retains the original grant, as required by RFC 6749 section 6.
        var restored = await Json(await Refresh(client, narrow));

        Assert.Equal(Email, (await UserInfo(client, restored)).GetProperty("email").GetString());
        var expanded = await client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = restored.GetProperty("refresh_token").GetString()!,
            ["client_id"] = "synthetic-broker",
            ["client_secret"] = Secret,
            ["scope"] = "openid private"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, expanded.StatusCode);
    }

    [Fact]
    public async Task Wrong_secret_PKCE_redirect_scope_plain_challenge_and_unsupported_grants_are_rejected()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        await Login(client);
        var code = await Code(client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Redeem(client, code, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Redeem(client, code, verifier: Verifier + "wrong")).StatusCode);
        foreach (var url in new[] { AuthorizeUrl(redirect: "https://unregistered.example.test/steal"), AuthorizeUrl(scope: "openid private"),
            AuthorizeUrl(method: "plain", challenge: Verifier), "connect/authorize?client_id=synthetic-broker&response_type=code&redirect_uri=" + Uri.EscapeDataString(Redirect) })
        {
            var response = await client.GetAsync(url);

            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Redirect);
            Assert.False(response.Headers.Location?.Host == "unregistered.example.test");
            if (response.Headers.Location is { } location)

                Assert.Contains("error=", location.Query);
        }
        foreach (var grant in new[] { "password", "client_credentials", "urn:ietf:params:oauth:grant-type:device_code" })
        {
            var response = await client.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["grant_type"] = grant, ["client_id"] = "synthetic-broker", ["client_secret"] = Secret }));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("connect/register")).StatusCode);
    }

    [Fact]
    public async Task Prompt_login_and_max_age_require_a_fresh_login_before_issuing_a_code()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();

        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        foreach (var parameter in new[] { "prompt=login", "max_age=0" })
        {
            var response = await client.GetAsync(AuthorizeUrl() + "&" + parameter);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/Account/Login", response.Headers.Location!.AbsolutePath);
            var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
            var returnUrl = query["ReturnUrl"].ToString();

            Assert.DoesNotContain("prompt=", returnUrl);
            Assert.DoesNotContain("max_age=", returnUrl);
            Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
            var retried = await client.GetAsync(returnUrl);

            Assert.Equal(HttpStatusCode.Redirect, retried.StatusCode);
            Assert.Contains("code=", retried.Headers.Location!.Query);
        }
    }

    [Fact]
    public async Task Prompt_none_with_max_age_returns_login_required_without_showing_the_login_page()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();

        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        var response = await client.GetAsync(AuthorizeUrl() + "&prompt=none&max_age=0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);

        Assert.Equal("login_required", query["error"].ToString());
        Assert.Equal("synthetic-state", query["state"].ToString());
    }

    [Fact]
    public async Task Abandoning_a_fresh_login_keeps_the_existing_session()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();

        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        var challenged = await client.GetAsync(AuthorizeUrl() + "&prompt=login");

        Assert.Equal("/Account/Login", challenged.Headers.Location!.AbsolutePath);
        Assert.NotEmpty(await Code(client));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Account_gate_blocks_existing_cookie_code_refresh_and_userinfo(bool pendingDeletion)
    {
        var user = await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        var tokens = await Tokens(client);
        var code = await Code(client);
        await using (var db = Context())
        {
            if (pendingDeletion)
                await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.PendingDeletionAt, DateTimeOffset.UtcNow));
            else
                await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Admitted, false));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Redeem(client, code)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(client, tokens)).StatusCode);
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, "connect/userinfo");
        infoRequest.Headers.Authorization = new("Bearer", tokens.GetProperty("access_token").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(infoRequest)).StatusCode);
        var authorization = await client.GetAsync(AuthorizeUrl());

        Assert.Equal(HttpStatusCode.Redirect, authorization.StatusCode);
        Assert.Contains("/Account/Login", authorization.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(client)).StatusCode); // Generic validation error, not a new session.
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Refresh_expires_at_365_idle_days_even_without_cleanup_and_success_renews_lifetime(int boundarySeconds, bool valid)
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        var issued = Clock.Now;
        var tokens = await Tokens(client);
        Clock.Now = issued.AddDays(365).AddSeconds(boundarySeconds);
        var response = await Refresh(client, tokens);
        if (!valid)
        {

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return;
        }

        var renewed = await Json(response);
        Clock.Now = issued.AddDays(600);

        Assert.Equal(HttpStatusCode.OK, (await Refresh(client, renewed)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Refresh(client, tokens)).StatusCode); // Rolling token was consumed.
    }

    [Fact]
    public async Task Concurrent_code_redemption_has_only_one_success_and_replay_is_rejected()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        await Login(client);
        var code = await Code(client);
        var responses = await Task.WhenAll(Redeem(client, code), Redeem(client, code));

        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.BadRequest, (await Redeem(client, code)).StatusCode);
        await using var db = Context();

        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM \"OpenIddictTokens\" WHERE \"Type\" = {TokenTypeIdentifiers.AccessToken}").SingleAsync());
    }

    [Fact]
    public async Task Cookie_and_signing_keys_survive_restart_and_shared_web_instances_and_forms_require_antiforgery()
    {
        await CreateUser();
        await CreateClient();
        var cookies = new CookieContainer();
        string keys;
        JsonElement tokens;
        await using (var first = await StartProvider())
        {
            using var client = Client(cookies);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Email"] = Email, ["Password"] = Password }))).StatusCode);
            tokens = await Tokens(client);
            keys = await client.GetStringAsync(".well-known/jwks");

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
            await first.StopAsync();
        }
        await using var second = await StartProvider();
        using var again = Client(cookies);

        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/")).StatusCode);
        Assert.Equal(keys, await again.GetStringAsync(".well-known/jwks"));
        var refreshed = await Json(await Refresh(again, tokens));
        await using var third = Build("web", b => b.Services.AddSingleton<TimeProvider>(Clock));
        var internalUrl = "http://127.0.0.1:" + FreePort() + "/";
        third.Urls.Add(internalUrl);
        await third.StartAsync();
        using var concurrent = Client(cookies);
        concurrent.BaseAddress = new Uri(internalUrl);

        Assert.Equal(HttpStatusCode.OK, (await concurrent.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(concurrent, refreshed)).StatusCode);
    }

    [Fact]
    public async Task Cleanup_removes_obsolete_records_without_breaking_usable_refresh_chain()
    {
        await CreateUser();
        await CreateClient();
        await using var app = await StartProvider();
        using var client = Client();
        var tokens = await Tokens(client);
        await using var db = Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "OpenIddictTokens" SET "CreationDate" = {DateTime.UtcNow.AddDays(-4)};
            UPDATE "OpenIddictTokens" SET "ExpirationDate" = {DateTime.UtcNow.AddDays(-2)} WHERE "Type" = {TokenTypeIdentifiers.AccessToken};
            """);

        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM \"OpenIddictTokens\" WHERE \"Type\" = {TokenTypeIdentifiers.AccessToken} AND \"ExpirationDate\" < now()").SingleAsync());
        await using var scope = app.Services.CreateAsyncScope();
        var job = ActivatorUtilities.CreateInstance<AuthenticationCleanupJob>(scope.ServiceProvider);
        await job.Execute(null!);

        Assert.Equal(HttpStatusCode.OK, (await Refresh(client, tokens)).StatusCode);
        var counts = await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM \"OpenIddictTokens\" WHERE \"Type\" = {TokenTypeIdentifiers.AccessToken} AND \"ExpirationDate\" < now()").SingleAsync();

        Assert.Equal(0, counts);
    }
}
