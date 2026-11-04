using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Nulysa.Billing;

namespace Nulysa.Integrations;

public sealed class EnableBankingOptions
{
    public Guid ApplicationId { get; set; }
    public string PrivateKeyPath { get; set; } = "";
    public DateOnly? ImportFrom { get; set; }
    public string CallbackUrl { get; set; } = "";

    internal bool IsValid() => ApplicationId != Guid.Empty && Path.IsPathFullyQualified(PrivateKeyPath) &&
        Uri.TryCreate(CallbackUrl, UriKind.Absolute, out var callback) &&
        (callback.Scheme == Uri.UriSchemeHttps || (callback.IsLoopback && callback.Scheme == Uri.UriSchemeHttp)) &&
        callback.AbsolutePath == BankConnectionEndpoints.CallbackPath && callback.Query.Length == 0 &&
        callback.Fragment.Length == 0 && callback.UserInfo.Length == 0;
}

internal static class EnableBankingTransactions
{
    internal static async Task<string> Sign(EnableBankingOptions settings, TimeProvider clock, CancellationToken ct)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(settings.PrivateKeyPath, ct));
        var now = clock.GetUtcNow();
        var header = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            typ = "JWT", alg = "RS256", kid = settings.ApplicationId
        }));
        var claims = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = "enablebanking.com", aud = "api.enablebanking.com",
            iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds()
        }));
        var message = $"{header}.{claims}";
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(message), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{message}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    internal static async IAsyncEnumerable<List<ReceiptInput>> Fetch(HttpClient http, BankConnection connection,
        string token, [EnumeratorCancellation] CancellationToken ct)
    {
        var importFrom = connection.ImportFrom;
        var date = importFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = $"accounts/{connection.AccountUid:D}/transactions?transaction_status=BOOK&date_from={date}";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = "";
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                next.Length == 0 ? path : QueryHelpers.AddQueryString(path, "continuation_key", next));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                    if (Text(error.RootElement, "error") is "EXPIRED_SESSION" or "REVOKED_SESSION" or "CLOSED_SESSION" or "ASPSP_PSU_ACTION_REQUIRED")
                        throw new BankAuthorizationException();
                }
                catch (JsonException)
                {
                    // A gateway error may not have a JSON body.
                }
                response.EnsureSuccessStatusCode();
            }

            using var page = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            next = Text(page.RootElement, "continuation_key");
            if (next.Length > 0 && !seen.Add(next))
                throw new ValidationException("Enable Banking repeated a transaction continuation key.");
            yield return Parse(connection.Id.ToString(), importFrom, page.RootElement);
        } while (next.Length > 0);
    }

    private static List<ReceiptInput> Parse(string account, DateOnly importFrom, JsonElement page)
    {
        var result = new List<ReceiptInput>();
        foreach (var transaction in page.GetProperty("transactions").EnumerateArray())
        {
            if (Text(transaction, "status") != "BOOK")
                continue;
            if (!DateOnly.TryParseExact(Text(transaction, "booking_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var booked))
                throw new ValidationException("A booked transaction requires a booking date.");
            if (booked < importFrom)
                continue;

            var id = Text(transaction, "entry_reference");
            if (string.IsNullOrWhiteSpace(id))
                throw new ValidationException("A booked transaction requires a stable Enable Banking entry reference.");
            var amountObject = transaction.GetProperty("transaction_amount");
            if (!decimal.TryParse(Text(amountObject, "amount"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
                throw new ValidationException("A booked transaction requires a valid amount.");
            var direction = Text(transaction, "credit_debit_indicator");
            if (direction is not ("CRDT" or "DBIT"))
                throw new ValidationException("A booked transaction requires a credit or debit indicator.");
            if (amount == 0)
                continue;

            var party = direction == "CRDT" ? "debtor" : "creditor";
            var counterparty = string.Join(" / ", new[] { Text(Field(transaction, party), "name"),
                Text(Field(transaction, party + "_account"), "iban") }.Where(value => value.Length > 0));
            var remittance = Field(transaction, "remittance_information");
            var message = remittance.ValueKind == JsonValueKind.Array
                ? string.Join("\n", remittance.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrEmpty(value)).Distinct())
                : "";
            result.Add(new("enablebanking", account, id, direction == "CRDT" ? amount : -amount, Text(amountObject, "currency"),
                booked, Text(transaction, "reference_number"), message, counterparty));
        }
        return result;
    }

    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;

    private static string Text(JsonElement value, string name) =>
        Field(value, name) is { ValueKind: JsonValueKind.String } field ? field.GetString()! : "";
}
