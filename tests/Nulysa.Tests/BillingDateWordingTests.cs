using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;

namespace Nulysa.Tests;

public sealed class BillingDateWordingTests : MembershipTest
{
    [Theory]
    [InlineData("en")]
    [InlineData("fi")]
    public async Task Portal_and_grace_mail_show_the_last_included_day_without_moving_payment_deadlines(string language)
    {
        Clock.Now = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var board = await Board();
        var transport = new RecordingMail();
        await using var commands = CommandsApp(transport);
        var id = await Admit(commands, board);
        var profile = Input().Profile;
        profile.Language = language;
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().UpdateProfile(Actor(id), id, profile));
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        var initial = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "bank", "paid", 10, "EUR", new(2026, 6, 1), initial.Invoices[0].Reference)));
        var service = await Run(commands, sp => sp.GetRequiredService<BillingCommands>()
            .ConfigureService(board, null, new("intro", "Introductory service", true, 14, 7, 10, "service")));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(fee => fee.ServiceDefinitionId == service);
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2026, 6, 1), 30));
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Subscribe(Actor(id), initial.AccountId, service, 1, false, Guid.NewGuid()));
        await using var web = await StartProvider();
        using var client = Client(language: null);
        await Login(client, Email);

        var membershipThrough = language == "en" ? "Valid through 30 Jun 2026" : "Voimassa 30.6.2026 asti";
        Assert.Contains(membershipThrough, Text(await client.GetStringAsync("/")));
        var services = Text(await client.GetStringAsync("/services"));
        Assert.Contains(membershipThrough, services);
        Assert.Contains(language == "en" ? "Valid through 14 Jun 2026" : "Voimassa 14.6.2026 asti", services);

        Clock.Now = new(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Scan(initial.AccountId));
        Clock.Now = new(2026, 7, 2, 10, 0, 0, TimeSpan.Zero);
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Scan(initial.AccountId));
        var renewed = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var invoice = renewed.Invoices.Single(invoice => invoice.Invoice.ClosedAt is null);
        await Login(client, Email);

        Assert.Equal(new DateOnly(2026, 7, 9), renewed.Membership.ActiveUntilExclusive);
        Assert.Contains(language == "en" ? "Available through 8 Jul 2026" : "Käytettävissä 8.7.2026 asti", Text(await client.GetStringAsync("/services")));
        var payBy = language == "en" ? "Pay by 1 Jul 2026" : "Maksa viimeistään 1.7.2026";
        Assert.Contains(payBy, Text(await client.GetStringAsync("/billing")));
        Assert.Contains(payBy, Text(await client.GetStringAsync($"/billing/invoices/{invoice.Invoice.Id}")));

        var delivery = await db.Deliveries.SingleAsync(delivery => delivery.UserId == id && delivery.Kind == MailKind.MembershipGrace);
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(delivery.Id));
        var message = Assert.Single(transport.Sent).Text;

        Assert.Contains(language == "en" ? "active through 2026-07-08" : "2026-07-08 asti", message);
        Assert.DoesNotContain("2026-07-09", message);
    }

    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ")), @"\s+", " ");
}
