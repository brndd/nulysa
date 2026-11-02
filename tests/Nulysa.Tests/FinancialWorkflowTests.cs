using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Components;

namespace Nulysa.Tests;

public sealed class FinancialWorkflowTests : BoardTest
{
    [Fact]
    public async Task Discount_form_includes_the_selected_through_date_even_for_a_single_day()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var url = $"/admin/members/{id}/actions/discount";
        var input = Hidden(await client.GetStringAsync(url));
        input["_handler"] = "adjustment";
        input["Adjustment.FeeId"] = "1";
        input["Adjustment.Kind"] = "Percentage";
        input["Adjustment.Value"] = "50";
        input["Adjustment.Start"] = "2026-06-03";
        input["Adjustment.Through"] = "2026-06-03";
        input["Adjustment.Reason"] = "Reviewed single-day concession";
        input["Adjustment.Confirmed"] = "false";

        var preview = await client.PostAsync(url, new FormUrlEncodedContent(input));
        var html = await preview.Content.ReadAsStringAsync();
        var confirm = Hidden(html);

        Assert.Contains("through 3 Jun 2026", WebUtility.HtmlDecode(html));
        Assert.Equal("2026-06-03", confirm["Adjustment.Through"]);
        await using var db = Context();
        Assert.Empty(await db.Discounts.ToListAsync());
        confirm["_handler"] = "adjustment";
        confirm["Adjustment.Confirmed"] = "true";

        var saved = await client.PostAsync(url, new FormUrlEncodedContent(confirm));

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var discount = await db.Discounts.SingleAsync();
        Assert.Equal(new DateOnly(2026, 6, 3), discount.StartsOn);
        Assert.Equal(new DateOnly(2026, 6, 4), discount.EndExclusive);
    }

    private static Dictionary<string, string> Hidden(string html)
    {
        var values = new Dictionary<string, string>();
        foreach (Match tag in Regex.Matches(html, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            var name = Regex.Match(tag.Value, "name=\"([^\"]+)\"");
            var value = Regex.Match(tag.Value, "value=\"([^\"]*)\"");
            if (name.Success && value.Success)
                values[WebUtility.HtmlDecode(name.Groups[1].Value)] = WebUtility.HtmlDecode(value.Groups[1].Value);
        }
        return values;
    }

    [Fact]
    public async Task Named_adjustments_preview_without_writes_then_confirm_grants_discounts_credits_refunds_reversals_and_cancellation()
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var initial = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "membership", 10, "EUR", new(2026, 6, 1), initial.Invoices[0].Reference)));
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        await using var db = Context();
        async Task Apply(string task, Dictionary<string, string> fields)
        {
            var origin = RegistryNavigation.MemberUrl(id, "/admin/members?q=New&state=active&page=2", MemberSection.Billing) + "&billsPage=2&transactionsPage=2&balance=purchase";
            var url = RegistryNavigation.ActionUrl(id, task, "/admin/members?q=New&state=active&page=2", origin);
            var get = await client.GetStringAsync(url);
            var input = Hidden(get);
            input["_handler"] = "adjustment";
            input["Adjustment.Key"] = Guid.NewGuid().ToString();
            input["Adjustment.Confirmed"] = "false";
            input["Adjustment.Reason"] = "Reviewed synthetic correction / bank reference";
            foreach (var field in fields)
                input[field.Key] = field.Value;
            var count = await db.BillingOperations.CountAsync();

            var preview = await client.PostAsync(url, new FormUrlEncodedContent(input));
            var html = await preview.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Contains("Review proposed effect", html);
            Assert.Equal(count, await db.BillingOperations.CountAsync());
            var confirm = Hidden(html);
            Assert.Equal(input["Adjustment.Key"], confirm["Adjustment.Key"]);
            confirm["_handler"] = "adjustment";
            confirm["Adjustment.Confirmed"] = "true";
            var saved = await client.PostAsync(url, new FormUrlEncodedContent(confirm));
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
            Assert.Equal(origin, saved.Headers.Location!.PathAndQuery);
        }
        await Apply("grant", new() { ["Adjustment.Months"] = "1" });
        Assert.Contains(await db.Coverage.ToListAsync(), period => period.EndExclusive == new DateOnly(2026, 8, 1));
        await Apply("discount", new() { ["Adjustment.FeeId"] = "1", ["Adjustment.Kind"] = "Percentage", ["Adjustment.Value"] = "50", ["Adjustment.Start"] = "2026-06-03", ["Adjustment.Through"] = "2026-06-30" });
        var discount = await db.Discounts.SingleAsync();
        var line = await db.InvoiceLines.SingleAsync(line => line.State == LineState.Fulfilled);
        await Apply("credit", new() { ["Adjustment.DiscountId"] = discount.Id.ToString(), ["Adjustment.LineId"] = line.Id.ToString() });
        Assert.True(await db.MoneyMovements.AnyAsync(movement => movement.Kind == MovementKind.PurchaseCredit));
        await Apply("withdraw", new() { ["Adjustment.DiscountId"] = discount.Id.ToString() });
        Assert.NotNull((await db.Discounts.AsNoTracking().SingleAsync()).WithdrawnAt);
        var deposit = await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "deposit", 20, "EUR", new(2026, 6, 1), initial.DepositReference)));
        await Apply("refund", new() { ["Adjustment.Value"] = "3", ["Adjustment.Purchase"] = "true" });
        Assert.Equal(3, await db.MoneyMovements.Where(movement => movement.Kind == MovementKind.PurchaseRefund).SumAsync(movement => movement.Amount));
        var reverse = await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "reverse", 4, "EUR", new(2026, 6, 1), initial.DepositReference)));
        await Apply("unassign", new() { ["Adjustment.ReceiptId"] = reverse.ToString() });
        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(receipt => receipt.Id == reverse)).State);
        var bill = await Run(commands, services => services.GetRequiredService<BillingCommands>().OneOff(board, initial.AccountId, Guid.NewGuid(), "Workshop", "workshop", 5, new(2026, 6, 20)));
        await Apply("cancel", new() { ["Adjustment.InvoiceId"] = bill.ToString()! });
        Assert.Equal(LineState.Cancelled, (await db.InvoiceLines.AsNoTracking().SingleAsync(row => row.InvoiceId == bill)).State);
        Assert.Equal(0, (await Run(commands, services => services.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    public async Task Receipt_selection_rejects_foreign_bills_and_requires_review_before_confirming_money()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var other = await Admit(commands, board, "other@example.test");
        var first = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var second = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, other)))!;
        var foreign = await Run(commands, services => services.GetRequiredService<BillingCommands>().OneOff(board, second.AccountId, Guid.NewGuid(), "Other workshop", "workshop", 5, DateOnly.FromDateTime(Clock.Now.DateTime).AddDays(14)));
        var receipt = await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "unknown", 5, "EUR", DateOnly.FromDateTime(Clock.Now.DateTime), "unrecognized")));
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var url = $"admin/payments/{receipt}?purpose=invoice&account={first.AccountId}";
        var input = Hidden(await client.GetStringAsync(url));
        input["_handler"] = "review-receipt";
        input["Input.Target"] = foreign.ToString()!;
        input["Input.Reason"] = "Verified bank facts";
        input["Input.Confirmed"] = "true";
        var invalid = await client.PostAsync(url, new FormUrlEncodedContent(input));
        Assert.Contains("Choose an available destination", await invalid.Content.ReadAsStringAsync());
        await using var db = Context();
        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(row => row.Id == receipt)).State);
        url = $"admin/payments/{receipt}?purpose=billing&account={first.AccountId}";
        input["Input.Confirmed"] = "false";
        var preview = await client.PostAsync(url, new FormUrlEncodedContent(input));
        Assert.Contains("Review proposed disposition", await preview.Content.ReadAsStringAsync());
        Assert.False(await db.MoneyMovements.AnyAsync(movement => movement.ReceiptId == receipt));
        var confirm = Hidden(await preview.Content.ReadAsStringAsync());
        confirm["_handler"] = "review-receipt";
        confirm["Input.Confirmed"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(url, new FormUrlEncodedContent(confirm))).StatusCode);
        Assert.Equal(ReceiptState.Posted, (await db.Receipts.AsNoTracking().SingleAsync(row => row.Id == receipt)).State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(commands, services => services.GetRequiredService<AdministrationBillingQueries>().Receipt(Actor(id), receipt)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("admin/payments/" + long.MaxValue)).StatusCode);
    }

    [Theory]
    [InlineData(-5, "EUR")]
    [InlineData(5, "USD")]
    public async Task Outgoing_and_foreign_currency_receipts_cannot_preview_an_allocation_but_can_be_ignored(decimal amount, string currency)
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var account = (await Run(commands, services => services.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var receipt = await Run(commands, services => services.GetRequiredService<BillingCommands>().Import(new("test", "bank", "unsupported", amount, currency, DateOnly.FromDateTime(Clock.Now.DateTime), "unrecognized")));
        await using var web = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var url = $"admin/payments/{receipt}?purpose=billing&account={account.AccountId}";
        var input = Hidden(await client.GetStringAsync(url));
        input["_handler"] = "review-receipt";
        input["Input.Reason"] = "Reviewed non-allocatable source facts";
        input["Input.Confirmed"] = "false";

        var response = await client.PostAsync(url, new FormUrlEncodedContent(input));

        Assert.Contains("Only incoming EUR transactions can be assigned", await response.Content.ReadAsStringAsync());
        await using var db = Context();
        Assert.False(await db.MoneyMovements.AnyAsync(movement => movement.ReceiptId == receipt));
        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(row => row.Id == receipt)).State);
        url = $"admin/payments/{receipt}?purpose=ignore";
        var preview = await client.PostAsync(url, new FormUrlEncodedContent(input));
        var confirm = Hidden(await preview.Content.ReadAsStringAsync());
        confirm["_handler"] = "review-receipt";
        confirm["Input.Confirmed"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync(url, new FormUrlEncodedContent(confirm))).StatusCode);
        Assert.Equal(ReceiptState.Ignored, (await db.Receipts.AsNoTracking().SingleAsync(row => row.Id == receipt)).State);
        Assert.False(await db.MoneyMovements.AnyAsync(movement => movement.ReceiptId == receipt));
    }
}
