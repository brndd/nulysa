using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Accounts;
using Nulysa.Billing;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class BillingBrowserTests : MembershipTest
{
    [Fact]
    public async Task Billing_details_keeps_outstanding_bills_visible_while_histories_page_independently()
    {
        Clock.Now = DateTimeOffset.UtcNow;
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        var view = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var today = DateOnly.FromDateTime(Clock.Now.DateTime);
        for (var index = 1; index <= 12; index++)
        {
            var billId = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(), $"Past purchase {index}", "workshop", 1, today.AddDays(7)));
            var bill = await Run(commands, sp => sp.GetRequiredService<BillingQueries>().InvoiceForMember(board, billId!.Value));
            await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "association", $"past-{index}", 1, "EUR", today, bill.Reference)));
            await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "association", $"deposit-{index}", index, "EUR", today, view.DepositReference)));
        }
        var populated = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        Assert.Equal(13, populated.Invoices.Count);
        Assert.Equal(12, populated.Invoices.Count(invoice => invoice.Invoice.ClosedAt is not null || invoice.Remaining == 0));

        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.Context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "Nulysa.Language", Value = "en", Url = Settings["Oidc:Issuer"]! }]);
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.GotoAsync(root + "services");
        await Assertions.Expect(page.Locator("#funds .balance-cards > div")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs your attention", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Membership and payments", Exact = true }).GetByRole(AriaRole.Link, new() { Name = "Billing details", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Billing details", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("#outstanding-bills .bill-summary")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("#funds")).ToHaveCountAsync(0);
        Assert.Null(await page.Locator("#bill-history").GetAttributeAsync("open"));
        await page.GetByRole(AriaRole.Navigation, new() { Name = "On this page" }).GetByRole(AriaRole.Link, new() { Name = "Previous bills", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#bill-history .bill-summary")).ToHaveCountAsync(10);
        await page.GetByRole(AriaRole.Link, new() { Name = "Next bills", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#bill-history .bill-summary")).ToHaveCountAsync(2);

        await page.GetByRole(AriaRole.Navigation, new() { Name = "Transaction balances" }).GetByRole(AriaRole.Link, new() { Name = "Purchase balance", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#transactions tbody tr")).ToHaveCountAsync(10);
        await Assertions.Expect(page.Locator("#transactions tbody tr").First).ToContainTextAsync("12.00");
        await Assertions.Expect(page.Locator("#transactions tbody tr").First).ToContainTextAsync("78.00");
        await page.GetByRole(AriaRole.Link, new() { Name = "Next transactions", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#transactions tbody tr")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator("#transactions tbody tr").First).ToContainTextAsync("3.00");
        await Assertions.Expect(page.Locator("#bill-history .bill-summary")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator("#outstanding-bills .bill-summary")).ToHaveCountAsync(1);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Previous bill pages" }).GetByRole(AriaRole.Link, new() { Name = "Previous bills", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#bill-history .bill-summary")).ToHaveCountAsync(10);
        await Assertions.Expect(page.Locator("#transactions tbody tr")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator("#funds")).ToHaveCountAsync(0);
        await page.SetViewportSizeAsync(375, 812);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
    }

    [Fact]
    public async Task Static_billing_forms_cover_configuration_subscription_bank_review_transfers_stopping_and_private_PDFs()
    {
        Clock.Now = DateTimeOffset.UtcNow; // Browser cookie expiry uses its real wall clock.
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        var view = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var member = await browser.NewContextAsync();
        await using var administrator = await browser.NewContextAsync();
        var page = await member.NewPageAsync();
        var admin = await administrator.NewPageAsync();
        var invoiceTables = page.Locator("table").Filter(new() { Has = page.Locator("caption").Filter(new() { HasText = "Invoice #" }) });
        await page.Context.AddCookiesAsync([new Microsoft.Playwright.Cookie { Name = "Nulysa.Language", Value = "en", Url = Settings["Oidc:Issuer"]! }]);
        var root = Settings["Oidc:Issuer"]!;
        async Task LoginPage(IPage target, string email)
        {
            await target.GotoAsync(root + "Account/Login");
            await target.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
            await target.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
            await target.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
            await Assertions.Expect(target.GetByRole(AriaRole.Heading, new() { Name = "Member portal", Exact = true })).ToBeVisibleAsync();
        }
        await LoginPage(admin, "board@example.test");
        var configurationPage = await admin.GotoAsync(root + "admin/settings/services/new");

        Assert.True(configurationPage!.Ok, "Configuration page failed: " + string.Join(" ", await admin.Locator(".titleerror").AllTextContentsAsync()));
        Assert.Contains("Code", await admin.ContentAsync());
        await admin.GetByLabel("Code").FillAsync("door");
        await admin.GetByLabel("Name", new() { Exact = true }).FillAsync("Door");
        await admin.GetByLabel("Accounting category").FillAsync("door");
        await admin.GetByLabel("Members may subscribe").CheckAsync();
        await admin.GetByLabel("Free days for new subscribers").FillAsync("0");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save service definition" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Link, new() { Name = "Door", Exact = true })).ToBeVisibleAsync();
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId != null);
        await admin.GotoAsync(root + "admin/settings/fees/" + fee.Id);
        await admin.GetByLabel("Monthly EUR").FillAsync("30");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add price", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Cell, new() { Name = "30.00", Exact = true })).ToBeVisibleAsync();

        await LoginPage(page, Email);
        await page.GotoAsync(root + "profile");
        await page.GetByLabel("Send invoices via email").UncheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save communication preferences" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Send invoices via email")).Not.ToBeCheckedAsync();
        await page.GotoAsync(root + "services");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Account overview", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Issued bills", Exact = true })).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Button, new() { Name = "Subscribe", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Review service change" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Region, new() { Name = "Review service change" })).ToContainTextAsync("30.00 EUR");
        Assert.Equal(0, await db.ServiceEnrollments.CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm service change" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Service updated");
        await page.GotoAsync(root + "billing");
        await Assertions.Expect(page.Locator("table")).ToHaveCountAsync(0);
        await page.GetByRole(AriaRole.Link, new() { Name = "View bill and payment instructions", Exact = true }).First.ClickAsync();
        await Assertions.Expect(invoiceTables.GetByRole(AriaRole.Row).Filter(new() { HasText = "Door" })).ToContainTextAsync("30.00");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Pay by bank transfer" })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".payment-instructions")).ToContainTextAsync(view.Invoices[0].Reference);
        await page.SetViewportSizeAsync(375, 812);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.SetViewportSizeAsync(1280, 800);
        await page.GetByRole(AriaRole.Link, new() { Name = "Back to payments", Exact = true }).ClickAsync();

        Assert.False((await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == view.AccountId)).EmailInvoices);

        async Task Import(string externalId, string reference, string amount)
        {
            await admin.GotoAsync(root + "admin/payments/import");
            await admin.GetByLabel("Source bank account ID").FillAsync("synthetic-bank-account");
            await admin.GetByLabel("External transaction ID").FillAsync(externalId);
            await admin.GetByLabel("Amount", new() { Exact = true }).FillAsync(amount);
            await admin.GetByLabel("Original reference").FillAsync(reference);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Import transaction" }).ClickAsync();
            await Assertions.Expect(admin.GetByLabel("External transaction ID")).ToBeEmptyAsync();
        }
        await Import("partial", view.Invoices[0].Reference, "15");
        await page.GotoAsync(root + $"billing/invoices/{view.Invoices[0].Invoice.Id}");
        await Assertions.Expect(invoiceTables.GetByRole(AriaRole.Row).Filter(new() { HasText = "Membership" })).ToContainTextAsync("Completed");
        await Assertions.Expect(invoiceTables.GetByRole(AriaRole.Row).Filter(new() { HasText = "Door" })).ToContainTextAsync("5.00");
        await Import("unknown", "not-a-reference", "7");
        var receipt = await db.Receipts.SingleAsync(x => x.ExternalId == "unknown");
        await admin.GotoAsync(root + $"admin/payments/{receipt.Id}?purpose=billing&account={view.AccountId}");
        await admin.GetByLabel("Reason", new() { Exact = true }).FillAsync("Verified bank payment and member");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Confirm payment", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToContainTextAsync("Payment updated");

        await Import("deposit", view.DepositReference, "10");
        await page.GotoAsync(root + "services");
        await page.GetByText("Move funds between balances", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("Amount (EUR)", new() { Exact = true }).FillAsync("9999");
        await page.GetByRole(AriaRole.Button, new() { Name = "Transfer available funds" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("exceeds your available source balance");
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByLabel("Amount (EUR)", new() { Exact = true })).ToHaveValueAsync("9999");
        await page.GetByLabel("Amount (EUR)", new() { Exact = true }).FillAsync("10");
        await page.GetByLabel("From / to").SelectOptionAsync("false");
        await page.GetByRole(AriaRole.Button, new() { Name = "Transfer available funds" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Review transfer" })).ToBeVisibleAsync();
        var beforeTransfer = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!.Balances;
        Assert.Equal(10, beforeTransfer.PurchaseBalance);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm transfer" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("Transfer complete");
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToHaveCountAsync(0);
        await page.GotoAsync(root + $"billing/invoices/{view.Invoices[0].Invoice.Id}");
        await Assertions.Expect(invoiceTables.GetByRole(AriaRole.Row).Filter(new() { HasText = "Door" })).ToContainTextAsync("22.00");
        await page.GotoAsync(root + "services");
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop renewal" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Review service change" })).ToBeVisibleAsync();
        Assert.Null((await db.ServiceEnrollments.AsNoTracking().SingleAsync()).EndedAt);
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm service change" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Subscribe", Exact = true })).ToBeVisibleAsync();
        await page.GetByLabel("Membership (currently").SelectOptionAsync("3");
        await page.GetByRole(AriaRole.Button, new() { Name = "Change billing period" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Membership (currently 3 months)")).ToBeVisibleAsync();

        await admin.GotoAsync(root + "admin/members/" + id + "/actions/one-off");
        await admin.GetByLabel("Description", new() { Exact = true }).FillAsync("Workshop");
        await admin.GetByLabel("Category", new() { Exact = true }).FillAsync("workshop");
        await admin.GetByLabel("Amount EUR", new() { Exact = true }).FillAsync("5");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Review invoice", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Button, new() { Name = "Create invoice", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToContainTextAsync("Changes saved");
        await page.GotoAsync(root + "billing");
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "Download PDF" }).First.ClickAsync());
        var path = Path.Combine(DirectoryPath, "browser-invoice.pdf");
        await download.SaveAsAsync(path);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(path))[..8]));
        var workshop = await db.Invoices.AsNoTracking().SingleAsync(x => x.OneOffReference != null);
        await Import("workshop", "unknown-workshop-reference", "5");
        var workshopReceipt = await db.Receipts.AsNoTracking().SingleAsync(x => x.ExternalId == "workshop");
        await admin.GotoAsync(root + $"admin/payments/{workshopReceipt.Id}?purpose=invoice&account={view.AccountId}");
        await admin.GetByLabel("Reason", new() { Exact = true }).FillAsync("Verified workshop payment");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose an available destination");
        await admin.GetByLabel("One-off bill", new() { Exact = true }).SelectOptionAsync(workshop.Id.ToString());
        await admin.GetByRole(AriaRole.Button, new() { Name = "Review payment", Exact = true }).ClickAsync();
        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == workshopReceipt.Id)).State);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Confirm payment", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Status)).ToContainTextAsync("Payment updated");

        Assert.Equal(ReceiptState.Posted, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == workshopReceipt.Id)).State);
        await Assertions.Expect(admin.GetByRole(AriaRole.Row).Filter(new() { HasText = "unknown-workshop-reference" })).ToHaveCountAsync(0);
        await page.GotoAsync(root + $"billing/invoices/{workshop.Id}");
        await Assertions.Expect(invoiceTables.GetByRole(AriaRole.Row).Filter(new() { HasText = "Workshop" })).ToContainTextAsync("Completed");
        await page.GotoAsync(root + $"billing/invoices/{view.Invoices[0].Invoice.Id}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Pay by bank transfer" })).ToHaveCountAsync(0);
        await page.GotoAsync(root + "billing?transactionsPage=-1&pageSize=0");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Billing details", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Transaction history", Exact = true })).ToBeVisibleAsync();
        await page.GotoAsync(root + "admin/payments");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Access denied", Exact = true })).ToBeVisibleAsync();
        foreach (var export in new[] { "export", "receipts-export" })
        {
            var invalid = await administrator.APIRequest.GetAsync(root + "admin/payments/" + export + "?start=2026-06-02&end=2026-06-01");

            Assert.Equal(400, invalid.Status);
        }

        Assert.Equal(22m, (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!.Balances.BillingCredit);
        Assert.Equal(0m, (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }
}
