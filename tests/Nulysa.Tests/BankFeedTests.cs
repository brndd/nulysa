using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Integrations;
using Nulysa.Operations;
using Quartz;

namespace Nulysa.Tests;

[Trait("Group", "Billing")]
public sealed class BankFeedTests : BoardTest
{
    [Fact]
    public async Task Booked_feed_deduplicates_preserves_outgoing_details_and_ignore_and_alerts_for_failed_or_stale_authorization()
    {
        var board = await Board();
        var account = Guid.NewGuid().ToString();
        Settings["Bank:Enabled"] = "true";
        Settings["Bank:Accounts:0"] = account;
        Settings["Bank:SecretId"] = "synthetic-id";
        Settings["Bank:SecretKey"] = "synthetic-secret";
        var handler = new BankHandler();
        await using var app = Build("commands", builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(Clock);
            builder.Services.AddHttpClient<BankFeed>().ConfigurePrimaryHttpMessageHandler(() => handler);
        });
        var id = await Admit(app, board);
        var view = (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var booked = DateOnly.FromDateTime(Clock.Now.DateTime).ToString("yyyy-MM-dd");
        handler.Payload = JsonSerializer.Serialize(new
        {
            transactions = new
            {
                booked = new object[]
            {
                new { transactionId = "in-1", bookingDate = booked, valueDate = "2001-01-01", transactionAmount = new { amount = "10.00", currency = "EUR" },
                    remittanceInformationStructured = view.Invoices[0].Reference, remittanceInformationUnstructured = "Membership", debtorName = "Synthetic payer" },
                new { internalTransactionId = "out-1", bookingDate = booked, transactionAmount = new { amount = "-25.00", currency = "EUR" },
                    remittanceInformationUnstructured = "=HYPERLINK(\"untrusted\")", creditorName = "Synthetic landlord", creditorAccount = new { iban = "FI2112345600000785" } }
            },
                pending = new[] { new { transactionId = "must-not-import" } }
            }
        });
        await Run(app, sp => sp.GetRequiredService<BankFeed>().Sync());
        await using var db = Context();

        Assert.Equal(2, await db.Receipts.CountAsync());
        var outgoing = await db.Receipts.SingleAsync(x => x.ExternalId == "out-1");

        Assert.Equal("=HYPERLINK(\"untrusted\")", outgoing.Message);
        Assert.Contains("Synthetic landlord", outgoing.Counterparty);
        Assert.Contains("FI2112345600000785", outgoing.Counterparty);
        Assert.Equal(DateOnly.FromDateTime(Clock.Now.DateTime), (await db.Receipts.SingleAsync(x => x.ExternalId == "in-1")).ReceivedOn);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().IgnoreReceipt(board, outgoing.Id, "Reviewed bank expense"));
        await Run(app, sp => sp.GetRequiredService<BankFeed>().Sync());

        Assert.Equal(2, await db.Receipts.CountAsync());
        Assert.Equal(ReceiptState.Ignored, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == outgoing.Id)).State);
        Assert.Equal(Clock.Now, (await db.BankSyncStates.AsNoTracking().SingleAsync()).LastSuccess);
        var start = DateOnly.FromDateTime(Clock.Now.DateTime);
        var csv = await Run(app, sp => sp.GetRequiredService<BillingQueries>().ExportReceipts(board, start, start.AddDays(1)));

        Assert.Contains("\"-25.00\"", csv);
        Assert.Contains("\"'=HYPERLINK", csv);
        Assert.Contains("Ignored", csv);
        Assert.Contains("Synthetic landlord", csv);
        Assert.Contains("Reviewed bank expense", csv);
        Assert.DoesNotContain("out-1", await Run(app, sp => sp.GetRequiredService<BillingQueries>().ExportReceipts(board, start.AddDays(1), start.AddDays(2))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().ExportReceipts(Actor(id), start, start.AddDays(1))));
        handler.Deny = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(app, sp => sp.GetRequiredService<BankFeed>().Sync()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(app, sp => sp.GetRequiredService<BankFeed>().Sync()));
        Assert.Equal("Authorization renewal required", (await db.BankSyncStates.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await db.Deliveries.Where(x => x.Kind == MailKind.BankAlert).ToListAsync());
        Clock.Now = Clock.Now.AddHours(27);
        await Run(app, sp => sp.GetRequiredService<BankFeed>().CheckStale(CancellationToken.None));

        Assert.Equal(2, await db.Deliveries.CountAsync(x => x.Kind == MailKind.BankAlert));
    }

    [Fact]
    public async Task Enable_Banking_signs_and_imports_booked_pages_and_reports_expired_consent()
    {
        var keyPath = Path.GetTempFileName();
        try
        {
            using var rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            var appId = Guid.NewGuid();
            var uid = Guid.NewGuid();
            var cutoff = DateOnly.FromDateTime(Clock.Now.DateTime);
            Settings["Bank:Enabled"] = "true";
            Settings["Bank:Provider"] = "EnableBanking";
            Settings["Bank:EnableBanking:ApplicationId"] = appId.ToString();
            Settings["Bank:EnableBanking:PrivateKeyPath"] = keyPath;
            Settings["Bank:EnableBanking:ImportFrom"] = cutoff.ToString("yyyy-MM-dd");
            Settings["Bank:EnableBanking:CallbackUrl"] = "https://nulysa.example.test/admin/payments/bank/callback";
            var handler = new EnableBankingHandler();
            await using var app = Build("commands", builder =>
            {
                builder.Services.AddSingleton<TimeProvider>(Clock);
                builder.Services.AddHttpClient<BankFeed>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
            var board = await Board();
            var member = await Admit(app, board);
            var invoice = Assert.Single((await Run(app, sp => sp.GetRequiredService<BillingQueries>().Member(board, member)))!.Invoices);

            const string next = "page+/= &2";
            var denySecondPage = true;
            var omitEntryReference = false;
            var requests = 0;
            handler.Respond = request =>
            {
                requests++;
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("https", request.RequestUri!.Scheme);
                Assert.Equal("api.enablebanking.com", request.RequestUri.Host);
                Assert.Equal($"/accounts/{uid:D}/transactions", request.RequestUri.AbsolutePath);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                Assert.Equal("BOOK", query["transaction_status"].ToString());
                Assert.Equal(cutoff.ToString("yyyy-MM-dd"), query["date_from"].ToString());

                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                var token = request.Headers.Authorization!.Parameter!.Split('.');
                Assert.Equal(3, token.Length);
                Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"),
                    WebEncoders.Base64UrlDecode(token[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(token[0]));
                Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
                Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
                Assert.Equal(appId, header.RootElement.GetProperty("kid").GetGuid());
                using var claims = JsonDocument.Parse(WebEncoders.Base64UrlDecode(token[1]));
                Assert.Equal("enablebanking.com", claims.RootElement.GetProperty("iss").GetString());
                Assert.Equal("api.enablebanking.com", claims.RootElement.GetProperty("aud").GetString());
                Assert.Equal(Clock.Now.ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
                Assert.Equal(Clock.Now.AddMinutes(5).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());

                if (!query.TryGetValue("continuation_key", out var cursor))
                    return Reply(HttpStatusCode.OK, new
                    {
                        continuation_key = next,
                        transactions = new object[]
                        {
                            new { status = "PDNG" },
                            new { status = "BOOK", booking_date = cutoff.AddDays(-1) },
                            new { status = "BOOK", booking_date = cutoff, value_date = cutoff.AddDays(-1),
                                entry_reference = "credit-1", transaction_id = $"ephemeral-{requests}", credit_debit_indicator = "CRDT",
                                transaction_amount = new { currency = "EUR", amount = "10.00" }, reference_number = invoice.Reference,
                                remittance_information = new[] { "Membership" }, debtor = new { name = "Synthetic payer" } }
                        }
                    });

                Assert.Equal(next, cursor.ToString());
                if (denySecondPage)
                    return Reply(HttpStatusCode.UnprocessableEntity, new { error = "EXPIRED_SESSION" });
                return Reply(HttpStatusCode.OK, new
                {
                    transactions = new[]
                    {
                        new { status = "BOOK", booking_date = cutoff, entry_reference = omitEntryReference ? null : "debit-1",
                            transaction_id = "details-only", credit_debit_indicator = "DBIT", creditor = new { name = "Synthetic landlord" },
                            creditor_account = new { iban = "FI2112345600000785" }, transaction_amount = new { currency = "EUR", amount = "25.00" } }
                    }
                });
            };
            Task Sync() => Run(app, sp => sp.GetRequiredService<BankFeed>().Sync());

            await using var db = Context();
            var connectionId = Guid.NewGuid();
            db.BankConnections.Add(new()
            {
                Id = connectionId, ApplicationId = appId, AccountUid = uid, SessionId = Guid.NewGuid(),
                Bank = "Mock ASPSP", Label = "Test account", IdentificationHash = "fixture-hash",
                ImportFrom = cutoff, ValidUntil = Clock.Now.AddDays(30)
            });
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(Sync);
            var state = await db.BankSyncStates.AsNoTracking().SingleAsync();
            Assert.Equal("Authorization renewal required", state.Status);
            Assert.Null(state.LastSuccess);
            Assert.Equal(connectionId.ToString(), state.Account);
            Assert.Single(await db.Deliveries.Where(x => x.Kind == MailKind.BankAlert).ToListAsync());
            Assert.Equal("credit-1", (await db.Receipts.SingleAsync()).ExternalId);

            denySecondPage = false;
            omitEntryReference = true;
            await Assert.ThrowsAsync<InvalidOperationException>(Sync);
            Assert.Equal("Import failed", (await db.BankSyncStates.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(1, await db.Receipts.CountAsync());

            omitEntryReference = false;
            await Sync();
            uid = Guid.NewGuid();
            await db.BankConnections.Where(x => x.Id == connectionId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.AccountUid, uid));
            await Sync();
            Assert.Equal(8, requests);
            var receipts = await db.Receipts.AsNoTracking().OrderBy(x => x.ExternalId).ToListAsync();
            Assert.Equal(2, receipts.Count);
            Assert.All(receipts, receipt =>
            {
                Assert.Equal("enablebanking", receipt.Source);
                Assert.Equal(connectionId.ToString(), receipt.SourceAccount);
                Assert.Equal(cutoff, receipt.ReceivedOn);
            });
            Assert.Equal(invoice.Reference, receipts[0].Reference);
            Assert.Equal(10m, receipts[0].Amount);
            Assert.Equal("Membership", receipts[0].Message);
            Assert.Equal(-25m, receipts[1].Amount);
            Assert.Equal("Synthetic landlord / FI2112345600000785", receipts[1].Counterparty);
            Assert.Equal(Clock.Now, (await db.BankSyncStates.AsNoTracking().SingleAsync()).LastSuccess);

            Clock.Now = Clock.Now.AddHours(25);
            await Sync();
            await db.BankConnections.Where(x => x.Id == connectionId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ValidUntil, Clock.Now.AddDays(10)));
            await Run(app, sp => sp.GetRequiredService<BankFeed>().CheckStale(CancellationToken.None));
            await Run(app, sp => sp.GetRequiredService<BankFeed>().CheckStale(CancellationToken.None));
            Assert.True(await db.BankConnections.Where(x => x.Id == connectionId).Select(x => x.ExpiryReminderSent).SingleAsync());
            Assert.Equal(2, await db.Deliveries.CountAsync(x => x.Kind == MailKind.BankAlert));
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Theory]
    [InlineData("auth.enablebanking.com")]
    [InlineData("tilisy-sandbox.enablebanking.com")]
    public async Task Admin_connects_and_renews_only_the_same_bank_account_through_the_browser_flow(string authorizationHost)
    {
        var keyPath = Path.GetTempFileName();
        try
        {
            using var rsa = RSA.Create(2048);
            await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            const string page = "/admin/payments/bank";
            const string callback = page + "/callback";
            const string bank = "Mock ASPSP";
            Settings["Bank:Enabled"] = "true";
            Settings["Bank:Provider"] = "EnableBanking";
            Settings["Bank:EnableBanking:ApplicationId"] = Guid.NewGuid().ToString();
            Settings["Bank:EnableBanking:PrivateKeyPath"] = keyPath;
            Settings["Bank:EnableBanking:CallbackUrl"] = Settings["Oidc:Issuer"]!.TrimEnd('/') + callback;
            var uid = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var originalHash = "synthetic-account";
            var hash = originalHash;
            var state = Guid.Empty;
            var redemptions = 0;
            var authorizationUrl = new Uri($"https://{authorizationHost}/ais/start?sessionid=fixture");
            var handler = new EnableBankingHandler
            {
                Respond = request =>
                {
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    switch (request.RequestUri!.AbsolutePath)
                    {
                        case "/aspsps":
                            Assert.Contains("psu_type=business", request.RequestUri.Query);
                            return Reply(HttpStatusCode.OK, new { aspsps = new[] { new { name = bank, maximum_consent_validity = 15552000 } } });
                        case "/auth":
                            Assert.Equal(HttpMethod.Post, request.Method);
                            using (var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()))
                            {
                                state = body.RootElement.GetProperty("state").GetGuid();
                                Assert.Equal(Settings["Bank:EnableBanking:CallbackUrl"], body.RootElement.GetProperty("redirect_url").GetString());
                                Assert.True(body.RootElement.GetProperty("access").GetProperty("transactions").GetBoolean());
                            }
                            return Reply(HttpStatusCode.OK, new { url = authorizationUrl });
                        case "/sessions":
                            redemptions++;
                            Assert.Equal(HttpMethod.Post, request.Method);
                            return Reply(HttpStatusCode.OK, new
                            {
                                session_id = sessionId, access = new { valid_until = Clock.Now.AddDays(160) },
                                aspsp = new { name = bank, country = "FI" },
                                accounts = new[] { new { uid, identification_hash = hash, name = "Synthetic account",
                                    account_id = new { iban = "FI2112345600000785" } } }
                            });
                        default:
                            throw new InvalidOperationException("Unexpected provider request.");
                    }
                }
            };
            await Board();
            await CreateUser();
            await using var app = Build("web", builder =>
            {
                builder.Services.AddSingleton<TimeProvider>(Clock);
                builder.Services.AddHttpClient<BankFeed>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
            app.Urls.Add(Settings["Oidc:Issuer"]!);
            await app.StartAsync();
            using var client = Client();
            using var member = Client();
            Assert.Equal(HttpStatusCode.Redirect, (await Login(client, "board@example.test")).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await Login(member)).StatusCode);
            var html = await client.GetStringAsync(page);
            Assert.Contains("Connect bank account", html);
            Assert.Matches("<button[^>]*disabled[^>]*>Import now</button>", html);
            Assert.Contains("Save a bank connection to enable imports.", html);
            var start = new Dictionary<string, string> { ["bank"] = bank, ["importFrom"] = Clock.Now.AddDays(-1).ToString("yyyy-MM-dd") };
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(page + "/start", new FormUrlEncodedContent(start))).StatusCode);
            start["__RequestVerificationToken"] = Token(html);
            Assert.Equal(authorizationUrl, (await client.PostAsync(page + "/start", new FormUrlEncodedContent(start))).Headers.Location);

            var callbackUrl = $"{callback}?code=synthetic-code&state={state:D}";
            var rejected = await member.GetAsync(callbackUrl);
            Assert.True(rejected.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
            Assert.Equal(0, redemptions);
            Assert.Equal(page, (await client.GetAsync($"{callback}?code=synthetic-code&state={Guid.NewGuid()}")).Headers.Location!.OriginalString);
            Assert.Equal(0, redemptions);
            var selection = (await client.GetAsync(callbackUrl)).Headers.Location!.OriginalString;
            Assert.Equal($"{page}?selection={state:D}", selection);
            Assert.Equal(page, (await client.GetAsync(callbackUrl)).Headers.Location!.OriginalString);
            Assert.Equal(1, redemptions);
            var chooser = await client.GetStringAsync(selection);
            Assert.Contains("0785", chooser);
            Assert.Contains("Save bank connection", chooser);
            var connect = new Dictionary<string, string>
            {
                ["selection"] = state.ToString(), ["account"] = uid.ToString(), ["__RequestVerificationToken"] = Token(chooser)
            };
            Assert.Equal(page, (await client.PostAsync(page + "/connect", new FormUrlEncodedContent(connect))).Headers.Location!.OriginalString);
            await using var db = Context();
            var original = await db.BankConnections.AsNoTracking().SingleAsync();
            Assert.Equal(uid, original.AccountUid);

            var connected = await client.GetStringAsync(page);
            Assert.Contains("<button>Import now</button>", connected);
            await Schedules.Install(Scheduler, Clock, TimeZoneInfo.Utc);
            var job = new JobKey("bank-sync", Schedules.Group);
            var scheduled = (await Scheduler.GetTriggersOfJob(job)).Count;
            var sync = new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(connected) };
            Assert.Equal(page, (await client.PostAsync(page + "/sync", new FormUrlEncodedContent(sync))).Headers.Location!.OriginalString);
            Assert.Equal(scheduled + 1, (await Scheduler.GetTriggersOfJob(job)).Count);
            Assert.Contains("Bank import queued successfully. Refresh this page in a moment to check the result.",
                await client.GetStringAsync(page));

            var renew = new Dictionary<string, string>
            {
                ["renewalId"] = original.Id.ToString(), ["__RequestVerificationToken"] = Token(await client.GetStringAsync(page))
            };
            uid = Guid.NewGuid();
            sessionId = Guid.NewGuid();
            hash = "different-account";
            Assert.Equal(authorizationUrl, (await client.PostAsync(page + "/start", new FormUrlEncodedContent(renew))).Headers.Location);
            await client.GetAsync($"{callback}?code=wrong-account&state={state:D}");
            Assert.Equal(original.AccountUid, (await db.BankConnections.AsNoTracking().SingleAsync()).AccountUid);

            hash = originalHash;
            Assert.Equal(authorizationUrl, (await client.PostAsync(page + "/start", new FormUrlEncodedContent(renew))).Headers.Location);
            await client.GetAsync($"{callback}?code=renewed&state={state:D}");
            var updated = await db.BankConnections.AsNoTracking().SingleAsync();
            Assert.Equal(original.Id, updated.Id);
            Assert.Equal(original.ImportFrom, updated.ImportFrom);
            Assert.Equal(uid, updated.AccountUid);
            Assert.Equal(sessionId, updated.SessionId);
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void Parsing_requires_booked_date_and_stable_identity_instead_of_guessing_from_payer_or_value_date()
    {
        using var missing = JsonDocument.Parse("""{"transactions":{"booked":[{"valueDate":"2026-01-01","debtorName":"Someone","transactionAmount":{"amount":"5","currency":"EUR"}}]}}""");

        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() => GoCardlessTransactions.Parse("account", missing.RootElement));
        using var referenceArray = JsonDocument.Parse("""{"transactions":{"booked":[{"transactionId":"one","bookingDate":"2026-01-01","remittanceInformationStructuredArray":["111","222"],"transactionAmount":{"amount":"5","currency":"EUR"}}]}}""");

        Assert.Equal("111 / 222", Assert.Single(GoCardlessTransactions.Parse("account", referenceArray.RootElement)).Reference);
    }

    private sealed class BankHandler : HttpMessageHandler
    {
        public string Payload { get; set; } = "";
        public bool Deny { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("token/new/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access\":\"synthetic-token\"}", Encoding.UTF8, "application/json") });

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(Deny ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) { Content = new StringContent(Payload, Encoding.UTF8, "application/json") });
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, object payload) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };

    private sealed class EnableBankingHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = null!;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }
}
