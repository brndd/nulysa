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
        var configurationPage = await admin.GotoAsync(root + "admin/billing-configuration");

        Assert.True(configurationPage!.Ok, "Configuration page failed: " + string.Join(" ", await admin.Locator(".titleerror").AllTextContentsAsync()));
        Assert.Contains("Stable code", await admin.ContentAsync());
        await admin.GetByLabel("Stable code").FillAsync("door");
        await admin.GetByLabel("Name", new() { Exact = true }).FillAsync("Door");
        await admin.GetByLabel("Accounting category").FillAsync("door");
        await admin.GetByLabel("Members may subscribe").CheckAsync();
        await admin.GetByLabel("Initial free calendar days").FillAsync("0");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save service definition" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Cell, new() { NameRegex = new System.Text.RegularExpressions.Regex(@"^#\d+ Door$") })).ToBeVisibleAsync();
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId != null);
        await admin.GetByLabel("Fee", new() { Exact = true }).SelectOptionAsync(fee.Id.ToString());
        await admin.GetByLabel("Monthly EUR").FillAsync("30");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Add price", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByText("30.00 EUR/month")).ToBeVisibleAsync();

        await LoginPage(page, Email);
        await page.GotoAsync(root + "billing");
        await page.GetByLabel("Email invoice PDFs").UncheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save preference" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Email invoice PDFs")).Not.ToBeCheckedAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Subscribe", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Door" })).ToContainTextAsync("30.00");

        Assert.False((await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == view.AccountId)).EmailInvoices);

        async Task Import(string externalId, string reference, string amount)
        {
            await admin.GotoAsync(root + "admin/payments");
            await admin.GetByLabel("Source bank account ID").FillAsync("synthetic-bank-account");
            await admin.GetByLabel("External transaction ID").FillAsync(externalId);
            await admin.GetByLabel("Amount", new() { Exact = true }).FillAsync(amount);
            await admin.GetByLabel("Original reference").FillAsync(reference);
            await admin.GetByRole(AriaRole.Button, new() { Name = "Import source fact and match" }).ClickAsync();
            await Assertions.Expect(admin.GetByLabel("External transaction ID")).ToBeEmptyAsync();
        }
        await Import("partial", view.Invoices[0].Reference, "15");
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Membership" })).ToContainTextAsync("Fulfilled");
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Door" })).ToContainTextAsync("5.00");
        await Import("unknown", "not-a-reference", "7");
        var receipt = await db.Receipts.SingleAsync(x => x.ExternalId == "unknown");
        await admin.GetByLabel("Receipt ID", new() { Exact = true }).FillAsync(receipt.Id.ToString());
        await admin.GetByLabel("Disposition / purpose").SelectOptionAsync("billing");
        await admin.GetByLabel("Financial account ID").FillAsync(view.AccountId.ToString());
        await admin.GetByLabel("Reason", new() { Exact = true }).FillAsync("Verified bank payment and member");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save reviewed disposition" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Row).Filter(new() { HasText = "not-a-reference" })).ToHaveCountAsync(0);

        await Import("deposit", view.DepositReference, "10");
        await page.ReloadAsync();
        await page.GetByLabel("Amount (EUR)", new() { Exact = true }).FillAsync("10");
        await page.GetByRole(AriaRole.Button, new() { Name = "Transfer available funds" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Door" })).ToContainTextAsync("22.00");
        await page.GetByRole(AriaRole.Button, new() { Name = "Stop renewal; keep remaining covered time" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Subscribe", Exact = true })).ToBeVisibleAsync();
        await page.GetByLabel("Membership (currently").SelectOptionAsync("3");
        await page.GetByRole(AriaRole.Button, new() { Name = "Change next unissued period" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Membership (currently 3 months)")).ToBeVisibleAsync();

        await admin.GotoAsync(root + "admin/billing/" + view.AccountId);
        await admin.GetByLabel("Description", new() { Exact = true }).FillAsync("Workshop");
        await admin.GetByLabel("Category", new() { Exact = true }).FillAsync("workshop");
        await admin.GetByLabel("Amount EUR", new() { Exact = true }).FillAsync("5");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Issue one-off request" }).ClickAsync();
        await Assertions.Expect(admin.GetByLabel("Description", new() { Exact = true })).ToBeEmptyAsync();
        await page.ReloadAsync();
        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "Download current PDF" }).First.ClickAsync());
        var path = Path.Combine(DirectoryPath, "browser-invoice.pdf");
        await download.SaveAsAsync(path);

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(path))[..8]));
        var workshop = await db.Invoices.AsNoTracking().SingleAsync(x => x.OneOffReference != null);
        await Import("workshop", "unknown-workshop-reference", "5");
        var workshopReceipt = await db.Receipts.AsNoTracking().SingleAsync(x => x.ExternalId == "workshop");
        await admin.GetByLabel("Receipt ID", new() { Exact = true }).FillAsync(workshopReceipt.Id.ToString());
        await admin.GetByLabel("Disposition / purpose").SelectOptionAsync("invoice");
        await admin.GetByLabel("Financial account ID").FillAsync(view.AccountId.ToString());
        await admin.GetByLabel("Reason", new() { Exact = true }).FillAsync("Verified workshop payment");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save reviewed disposition" }).ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose a one-off invoice ID");
        await Assertions.Expect(admin.GetByLabel("Disposition / purpose")).ToHaveValueAsync("invoice");
        await admin.GetByLabel("Invoice ID (one-off assignment only)").FillAsync(workshop.Id.ToString());
        var reviewKey = await admin.Locator("input[name='Review.Key']").InputValueAsync();
        foreach (var invalidAccount in new[] { "", long.MaxValue.ToString() })
        {
            await admin.GetByLabel("Financial account ID").FillAsync(invalidAccount);
            var response = await admin.RunAndWaitForResponseAsync(() => admin.GetByRole(AriaRole.Button, new() { Name = "Save reviewed disposition" }).ClickAsync(),
                response => response.Request.Method == "POST" && response.Url == root + "admin/payments");

            Assert.Equal(200, response.Status);
            await Assertions.Expect(admin.GetByRole(AriaRole.Alert)).ToContainTextAsync("Choose an existing financial account ID");
            await Assertions.Expect(admin.GetByLabel("Disposition / purpose")).ToHaveValueAsync("invoice");
            await Assertions.Expect(admin.GetByLabel("Invoice ID (one-off assignment only)")).ToHaveValueAsync(workshop.Id.ToString());
            await Assertions.Expect(admin.GetByLabel("Reason", new() { Exact = true })).ToHaveValueAsync("Verified workshop payment");

            Assert.Equal(reviewKey, await admin.Locator("input[name='Review.Key']").InputValueAsync());
            Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == workshopReceipt.Id)).State);
            Assert.False(await db.MoneyMovements.AnyAsync(x => x.ReceiptId == workshopReceipt.Id));
        }
        await admin.GetByLabel("Financial account ID").FillAsync(view.AccountId.ToString());
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save reviewed disposition" }).ClickAsync();
        await Assertions.Expect(admin.GetByLabel("Receipt ID", new() { Exact = true })).ToHaveValueAsync("0");

        Assert.Equal(ReceiptState.Posted, (await db.Receipts.AsNoTracking().SingleAsync(x => x.Id == workshopReceipt.Id)).State);
        await Assertions.Expect(admin.GetByRole(AriaRole.Row).Filter(new() { HasText = "unknown-workshop-reference" })).ToHaveCountAsync(0);
        await page.GotoAsync(root + "billing");
        await Assertions.Expect(page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Workshop" })).ToContainTextAsync("Fulfilled");
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
