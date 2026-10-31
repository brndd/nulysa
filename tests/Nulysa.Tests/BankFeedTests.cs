using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Integrations;

namespace Nulysa.Tests;

[Trait("Group", "Billing")]
public sealed class BankFeedTests : MembershipTest
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
}
