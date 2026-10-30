using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class KeycloakTests : OidcTest
{
    [Fact]
    public async Task Real_broker_preserves_identity_after_email_change_and_does_not_pretend_downstream_sessions_are_revoked()
    {
        var baseUrl = Environment.GetEnvironmentVariable("NULYSA_TEST_KEYCLOAK") ?? "http://127.0.0.1:58080";
        var uri = new Uri(baseUrl);
        Assert.True(uri.Scheme == "http" && uri.IsLoopback, "A disposable loopback Keycloak is required.");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var token = await Json(await http.PostAsync(baseUrl + "/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = "s0-admin",
            ["password"] = "synthetic-admin-password"
        })));
        var adminToken = token.GetProperty("access_token").GetString();
        var realm = "nulysa-s0-" + Guid.NewGuid().ToString("N");
        var issuer = baseUrl + "/realms/" + realm;
        var user = await CreateUser();
        await CreateClient(issuer + "/broker/mulysa/endpoint");
        await using var provider = await StartProvider();
        var downstreamBuilder = WebApplication.CreateBuilder();
        downstreamBuilder.Logging.ClearProviders();
        await using var downstream = downstreamBuilder.Build();
        var callback = "http://127.0.0.1:" + FreePort() + "/callback";
        downstream.Urls.Add(callback[..^"/callback".Length]);
        downstream.MapGet("/callback", () => "Synthetic downstream callback");
        await downstream.StartAsync();

        async Task<JsonElement> Admin(HttpMethod method, string path, object? data = null, HttpStatusCode expected = HttpStatusCode.NoContent)
        {
            using var request = new HttpRequestMessage(method, baseUrl + "/admin/" + path);
            request.Headers.Authorization = new("Bearer", adminToken);
            if (data is not null) request.Content = JsonContent.Create(data);
            var response = await http.SendAsync(request);
            if (expected == HttpStatusCode.OK) return await Json(response);
            Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync());
            return default;
        }

        await Admin(HttpMethod.Post, "realms", new { realm, enabled = true, sslRequired = "none" }, HttpStatusCode.Created);
        try
        {
            var executions = await Admin(HttpMethod.Get, $"realms/{realm}/authentication/flows/first%20broker%20login/executions", expected: HttpStatusCode.OK);
            foreach (var execution in executions.EnumerateArray())
            {
                if (!execution.TryGetProperty("providerId", out var name) || name.GetString() != "idp-review-profile") continue;
                var edited = JsonSerializer.Deserialize<Dictionary<string, object>>(execution.GetRawText())!;
                edited["requirement"] = "DISABLED";
                await Admin(HttpMethod.Put, $"realms/{realm}/authentication/flows/first%20broker%20login/executions", edited);
            }
            var upstream = Settings["Oidc:Issuer"]!;
            await Admin(HttpMethod.Post, $"realms/{realm}/identity-provider/instances", new
            {
                alias = "mulysa",
                providerId = "oidc",
                enabled = true,
                trustEmail = true,
                config = new Dictionary<string, string>
                {
                    ["authorizationUrl"] = upstream + "connect/authorize",
                    ["tokenUrl"] = upstream + "connect/token",
                    ["userInfoUrl"] = upstream + "connect/userinfo",
                    ["jwksUrl"] = upstream + ".well-known/jwks",
                    ["issuer"] = upstream,
                    ["clientId"] = "synthetic-broker",
                    ["clientSecret"] = Secret,
                    ["clientAuthMethod"] = "client_secret_post",
                    ["validateSignature"] = "true",
                    ["useJwksUrl"] = "true",
                    ["pkceEnabled"] = "true",
                    ["pkceMethod"] = "S256",
                    ["defaultScope"] = "openid email profile",
                    ["syncMode"] = "FORCE"
                }
            }, HttpStatusCode.Created);
            await Admin(HttpMethod.Post, $"realms/{realm}/clients", new
            {
                clientId = "synthetic-downstream",
                protocol = "openid-connect",
                publicClient = true,
                standardFlowEnabled = true,
                redirectUris = new[] { callback },
                attributes = new Dictionary<string, string> { ["pkce.code.challenge.method"] = "S256" }
            }, HttpStatusCode.Created);

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var original = await browser.NewContextAsync();
            var page = await original.NewPageAsync();

            async Task<(string State, string Nonce, string Verifier)> Begin(IPage target)
            {
                var state = Guid.NewGuid().ToString("N");
                var nonce = Guid.NewGuid().ToString("N");
                var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
                var url = QueryHelpers.AddQueryString(issuer + "/protocol/openid-connect/auth", new Dictionary<string, string?>
                {
                    ["client_id"] = "synthetic-downstream",
                    ["response_type"] = "code",
                    ["scope"] = "openid email profile",
                    ["redirect_uri"] = callback,
                    ["state"] = state,
                    ["nonce"] = nonce,
                    ["kc_idp_hint"] = "mulysa",
                    ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                    ["code_challenge_method"] = "S256"
                });
                await target.GotoAsync(url);
                return (state, nonce, verifier);
            }

            async Task<JsonWebToken> Finish(IPage target, (string State, string Nonce, string Verifier) pending)
            {
                // Assert the callback URL, not a load event that can precede this observer after redirects.
                await Assertions.Expect(target).ToHaveURLAsync(new Regex("^" + Regex.Escape(callback) + "\\?"), new() { Timeout = 20000 });
                var query = QueryHelpers.ParseQuery(new Uri(target.Url).Query);
                Assert.Equal(pending.State, query["state"]);
                var response = await Json(await http.PostAsync(issuer + "/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = "synthetic-downstream",
                    ["redirect_uri"] = callback,
                    ["code"] = query["code"].ToString(),
                    ["code_verifier"] = pending.Verifier
                })));
                var jwks = await http.GetStringAsync(issuer + "/protocol/openid-connect/certs");
                var result = await new JsonWebTokenHandler().ValidateTokenAsync(response.GetProperty("id_token").GetString(), new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = "synthetic-downstream",
                    IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.Zero
                });
                Assert.True(result.IsValid, result.Exception?.ToString());
                var jwt = (JsonWebToken)result.SecurityToken;
                Assert.Equal(pending.Nonce, jwt.GetClaim("nonce").Value);
                return jwt;
            }

            async Task Enter(IPage target, string email)
            {
                await target.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
                await target.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
                await target.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
            }

            var pending = await Begin(page);
            await Enter(page, Email);
            var first = await Finish(page, pending);
            Assert.Equal(Email, first.GetClaim("email").Value);
            await using (var db = Context())
                await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Email, "changed@example.test")
                    .SetProperty(u => u.NormalizedEmail, "CHANGED@EXAMPLE.TEST"));
            await using (var fresh = await browser.NewContextAsync())
            {
                var changed = await fresh.NewPageAsync();
                pending = await Begin(changed);
                await Enter(changed, "changed@example.test");
                var second = await Finish(changed, pending);
                Assert.Equal(first.Subject, second.Subject);
                Assert.Equal("changed@example.test", second.GetClaim("email").Value);
            }
            var brokerUsers = await Admin(HttpMethod.Get, $"realms/{realm}/users", expected: HttpStatusCode.OK);
            Assert.Equal(1, brokerUsers.GetArrayLength());
            Assert.Equal(first.Subject, brokerUsers[0].GetProperty("id").GetString());
            var links = await Admin(HttpMethod.Get, $"realms/{realm}/users/{first.Subject}/federated-identity", expected: HttpStatusCode.OK);
            Assert.Equal("mulysa", links[0].GetProperty("identityProvider").GetString());
            Assert.Equal(user.OidcSubject, links[0].GetProperty("userId").GetString());
            await using (var db = Context())
                await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Admitted, false));
            await using (var fresh = await browser.NewContextAsync())
            {
                var blocked = await fresh.NewPageAsync();
                await Begin(blocked);
                await Enter(blocked, "changed@example.test");
                await Assertions.Expect(blocked.GetByRole(AriaRole.Alert)).ToContainTextAsync("Unable to log in");
                Assert.Equal("/Account/Login", new Uri(blocked.Url).AbsolutePath);
            }
            pending = await Begin(page);
            Assert.Equal(first.Subject, (await Finish(page, pending)).Subject);
            await page.GotoAsync(upstream);
            Assert.Equal("/Account/Login", new Uri(page.Url).AbsolutePath);
        }
        finally { await Admin(HttpMethod.Delete, "realms/" + realm); }
    }
}
