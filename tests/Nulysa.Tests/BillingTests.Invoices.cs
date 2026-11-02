using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Tests;

public sealed partial class BillingTests
{
    [Fact]
    public async Task One_off_reference_uses_the_same_funding_path_without_recurring_coverage_or_wallet_sweep()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, account, Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        view = await View(app, board, id);
        var one = view.Invoices.Single(x => x.Invoice.Id == invoiceId);

        Assert.Null(one.Invoice.BillingGroupId);
        await Pay(app, one.Reference, 15);
        view = await View(app, board, id);

        Assert.Equal(12.50m, view.Invoices.Single(x => x.Invoice.Id == invoiceId).Paid);
        Assert.Empty(view.Coverage);
        Assert.Equal(2.50m, view.Balances.Held);
        Assert.Equal(0, view.Balances.BillingCredit);
        var other = await Admit(app, board, "other@example.test");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().Account(Actor(other), account)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().Review(Actor(id))));
    }

    [Fact]
    [Trait("Acceptance", "BILL")]
    public async Task One_off_due_date_is_inclusive_and_the_following_day_is_late()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var firstId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(),
            "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        var secondId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(),
            "Repair", "repair", 12.50m, new(2026, 6, 20)));
        var oneOffs = (await View(app, board, id)).Invoices;
        var first = oneOffs.Single(x => x.Invoice.Id == firstId);
        var second = oneOffs.Single(x => x.Invoice.Id == secondId);
        On(6, 20);
        await Pay(app, first.Reference, 12.50m, new(2026, 6, 20));
        On(6, 21);
        await Pay(app, second.Reference, 12.50m, new(2026, 6, 21));
        view = await View(app, board, id);

        Assert.Equal(LineState.Fulfilled, view.Invoices.Single(x => x.Invoice.Id == firstId).Lines.Single().Line.State);
        Assert.Equal(LineState.Expired, view.Invoices.Single(x => x.Invoice.Id == secondId).Lines.Single().Line.State);
        Assert.Equal(12.50m, view.Balances.BillingCredit);
        Assert.Equal(0m, view.Balances.Held);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Acceptance", "BILL")]
    public async Task Reviewed_receipt_targets_a_one_off_invoice_before_recurring_bills_with_ownership_and_retry_checks(bool delayed)
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var other = await Admit(app, board, "other@example.test");
        var otherView = await View(app, board, other);
        var invoiceId = await Run(app, sp => sp.GetRequiredService<BillingCommands>().OneOff(board, view.AccountId, Guid.NewGuid(), "Workshop", "workshop", 12.50m, new(2026, 6, 20)));
        var receipt = await Pay(app, "missing-reference", 12.50m, new(2026, 6, 1), "workshop");
        if (delayed)
        {
            On(6, 21);
            await Scan(app, view.AccountId);
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(Actor(id), receipt, view.AccountId, new ReceiptDestination.OneOffInvoice(invoiceId!.Value), Guid.NewGuid(), "Not administrator")));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, otherView.AccountId, new ReceiptDestination.OneOffInvoice(invoiceId!.Value), Guid.NewGuid(), "Wrong account")));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, ReceiptDestination.FromSelection(view.Groups.Single().Id, false, invoiceId), Guid.NewGuid(), "Conflicting purpose")));

        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, new ReceiptDestination.OneOffInvoice(view.Invoices[0].Invoice.Id), Guid.NewGuid(), "Not a one-off")));
        var key = Guid.NewGuid();
        foreach (var invalidAccount in new[] { 0L, -1L, long.MaxValue })
        {
            var error = await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
                .AssignReceipt(board, receipt, invalidAccount, new ReceiptDestination.OneOffInvoice(invoiceId!.Value), key, "Verified workshop payment")));

            Assert.Equal("Choose an existing financial account ID.", error.Message);
        }
        await using var db = Context();

        Assert.Equal(ReceiptState.Review, (await db.Receipts.AsNoTracking().SingleAsync()).State);
        Assert.Empty(await db.MoneyMovements.ToListAsync());
        Assert.False(await db.BillingOperations.AnyAsync(x => x.Id == key));
        async Task Assign(long? target) => await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AssignReceipt(board, receipt, view.AccountId, ReceiptDestination.FromSelection(null, false, target), key, "Verified workshop payment"));
        await Assign(invoiceId);
        await Assign(invoiceId);

        await Assert.ThrowsAsync<ValidationException>(() => Assign(null));
        Assert.Equal(receipt, await Pay(app, "missing-reference", 12.50m, new(2026, 6, 1), "workshop"));
        var after = await View(app, board, id);

        Assert.Equal(12.50m, after.Invoices.Single(x => x.Invoice.Id == invoiceId).Paid);
        Assert.Equal(0m, after.Invoices.Single(x => x.Invoice.Id == view.Invoices[0].Invoice.Id).Paid);
        Assert.Equal(new Balances(0, 0, 0), after.Balances);
        Assert.Empty(after.Coverage);
        Assert.Equal("missing-reference", (await db.Receipts.AsNoTracking().SingleAsync()).Reference);
        var reconciliation = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board));

        Assert.Equal(0m, reconciliation.Difference);
        Assert.Empty(reconciliation.InvalidLines);
        Assert.Empty(reconciliation.InvalidReceipts);
    }

    [Fact]
    [Trait("Acceptance", "MAIL")]
    public async Task Invoice_render_failure_moves_the_delivery_to_review_instead_of_retrying()
    {
        Settings["Billing:Iban"] = "FI2112345600000785";
        var templates = Path.Combine(DirectoryPath, "broken-invoice");
        Directory.CreateDirectory(templates);
        await File.WriteAllTextAsync(Path.Combine(templates, "lasku.typ"), "#panic(\"broken template\")");
        Settings["Billing:TemplateDirectory"] = templates;
        On(6, 1);
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        await using var db = Context();
        var delivery = await db.Deliveries.SingleAsync(x => x.UserId == id && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.Empty(transport.Sent);
        Assert.Equal(DeliveryState.Review, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == delivery.Id)).State);
    }

    [Fact]
    public async Task Bill_detail_queries_enforce_ownership_and_return_current_funding()
    {
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");
        var view = await View(app, board, id);
        var invoiceId = view.Invoices[0].Invoice.Id;

        var detail = await Run(app, sp => sp.GetRequiredService<BillingQueries>().InvoiceForMember(Actor(id), invoiceId));

        Assert.Equal(view.Invoices[0].Reference, detail.Reference);
        Assert.Equal(view.Invoices[0].Remaining, detail.Remaining);
        Assert.NotEmpty(detail.Lines);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().InvoiceForMember(Actor(other), invoiceId)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Run(app, sp => sp.GetRequiredService<BillingQueries>().InvoiceForMember(Actor(id), long.MaxValue)));
    }

    [Fact]
    public async Task Typst_renders_generic_barcode_invoice_and_mail_checks_revision_preference_and_ownership()
    {
        Settings["Billing:Iban"] = "FI2112345600000785";
        On(6, 1);
        var board = await Board();
        var transport = new RecordingMail();
        await using var app = CommandsApp(transport);
        var id = await Admit(app, board);
        var other = await Admit(app, board, "other@example.test");
        var profile = Input().Profile;
        profile.Name = "Literal #panic(\"not executable\")";
        await Run(app, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().UpdateProfile(Actor(id), id, profile));
        var view = await View(app, board, id);
        var invoice = view.Invoices[0].Invoice.Id;
        var pdf = await Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), invoice));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf[..8]));
        Assert.True(pdf.Length > 1000);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(other), invoice)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), long.MaxValue)));
        await using var db = Context();
        var delivery = await db.Deliveries.SingleAsync(x => x.InvoiceId == invoice && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.NotNull(Assert.Single(transport.Sent).Pdf);
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));

        Assert.Single(transport.Sent);
        var otherView = await View(app, board, other);
        var optedOut = await db.Deliveries.SingleAsync(x => x.UserId == other && x.Kind == MailKind.Invoice);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetEmailInvoices(Actor(other), otherView.AccountId, false, Guid.NewGuid()));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(optedOut.Id));

        Assert.Equal(DeliveryState.Cancelled, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == optedOut.Id)).State);
        var service = await Service(app, board);
        var waiting = await Admit(app, board, "stale@example.test");
        var waitingView = await View(app, board, waiting);
        var stale = await db.Deliveries.SingleAsync(x => x.UserId == waiting && x.Kind == MailKind.Invoice);
        // A queued revision must not send the old payable quote after an invoice edit.
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, waitingView.AccountId, service, 1, false, Guid.NewGuid()));
        await Run(app, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(stale.Id));

        Assert.Equal(DeliveryState.Cancelled, (await db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == stale.Id)).State);
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task Deployment_template_receives_structured_data_assets_and_association_local_issue_date()
    {
        Clock.Now = new DateTimeOffset(2026, 6, 1, 22, 30, 0, TimeSpan.Zero);
        var templates = Path.Combine(DirectoryPath, "trusted-invoice");
        Directory.CreateDirectory(templates);
        await File.WriteAllTextAsync(Path.Combine(templates, "heading.typ"), "Trusted deployment invoice");
        await File.WriteAllTextAsync(Path.Combine(templates, "lasku.typ"), """
            #let invoice = json(sys.inputs.at("invoice"))
            #assert(invoice.date == "2026-06-02")
            #assert(invoice.items.len() == 1 and invoice.remaining_cents == 1000)
            #include "heading.typ"
            #invoice.number
            """);
        Settings["Billing:TemplateDirectory"] = templates;
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var pdf = await Run(app, sp => sp.GetRequiredService<InvoicePdf>().ForMember(Actor(id), view.Invoices[0].Invoice.Id));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(pdf[..8]));
    }

}
