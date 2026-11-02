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
    [Trait("Acceptance", "ADJ")]
    public async Task Discount_grant_and_honorary_coverage_release_only_unfinished_funds_without_fictitious_money()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10);
        On(6, 20);
        await Scan(app, account);
        await Pay(app, reference, 5);
        var grantKey = Guid.NewGuid();
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().GrantMembershipMonths(board, account, 1, grantKey, "Volunteer compensation"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().GrantMembershipMonths(board, account, 1, grantKey, "Volunteer compensation"));
        view = await View(app, board, id);

        Assert.Equal(5m, view.Balances.BillingCredit);
        Assert.Single(view.Coverage, x => x.Source == CoverageSource.AdministratorGrant);
        Assert.DoesNotContain(view.Invoices.SelectMany(x => x.Lines), x => x.Line.State == LineState.Open);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 8, 1), new(3026, 8, 1), DiscountKind.Percentage, 100, Guid.NewGuid(), "Honorary membership"));
        On(7, 20);
        await Scan(app, account);
        view = await View(app, board, id);
        var honorary = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 8, 1)).Line;

        Assert.Equal(LineState.Fulfilled, honorary.State);
        Assert.Equal(0, honorary.Amount);
        Assert.Null(honorary.EffectiveOn);
        Assert.Equal(5m, view.Balances.BillingCredit);
        await using var db = Context();

        Assert.Equal(2, await db.Receipts.CountAsync());
        Assert.DoesNotContain(await db.MoneyMovements.ToListAsync(), x => x.Amount <= 0);
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Completed_future_discount_is_a_reviewed_credit_preserving_original_purchase_and_coverage()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var reference = view.Invoices[0].Reference;
        await Pay(app, reference, 10);
        On(6, 20);
        await Scan(app, account);
        await Pay(app, reference, 10);
        view = await View(app, board, id);
        var line = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.StartsOn == new DateOnly(2026, 7, 1)).Line;
        var discount = await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 7, 1), new(2026, 8, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Future discount"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().CreditFutureDiscount(board, account, line.Id, discount!.Value, 5, Guid.NewGuid(), "Reviewed July purchase"));
        view = await View(app, board, id);

        Assert.Equal(5m, view.Balances.BillingCredit);
        Assert.Equal(10m, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == line.Id).Line.Amount);
        Assert.Equal(2, view.Coverage.Count);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>().CreditFutureDiscount(board, account, line.Id, discount!.Value, 5, Guid.NewGuid(), "Duplicate correction")));
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Future_discount_credits_compensate_distinct_dates_and_only_uncredited_parts_of_replacement_discounts()
    {
        Settings["Billing:DefaultIntervalMonths"] = "12";
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var initial = await View(app, board, id);
        var account = initial.AccountId;
        await Pay(app, initial.Invoices[0].Reference, 120);
        On(6, 20);
        var line = Assert.Single((await View(app, board, id)).Invoices.SelectMany(x => x.Lines)).Line;
        async Task<long> Discount(DateOnly start, DateOnly end, decimal percent) => (await Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .AddDiscount(board, account, 1, start, end, DiscountKind.Percentage, percent, Guid.NewGuid(), "Future discount")))!.Value;
        Task Credit(long discount, decimal amount) => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .CreditFutureDiscount(board, account, line.Id, discount, amount, Guid.NewGuid(), "Reviewed future purchase"));
        Task Withdraw(long discount) => Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, discount, Guid.NewGuid(), "Replacement discount"));
        var july = await Discount(new(2026, 7, 1), new(2026, 8, 1), 50);
        var august = await Discount(new(2026, 8, 1), new(2026, 9, 1), 50);
        await Credit(july, 5.10m);
        await Credit(august, 5.10m);

        Assert.Equal(10.20m, (await View(app, board, id)).Balances.BillingCredit);
        await Assert.ThrowsAsync<ValidationException>(() => Credit(july, 5.10m));
        await Withdraw(july);
        await Withdraw(august);
        var replacement = await Discount(new(2026, 7, 1), new(2026, 9, 1), 75);
        await Credit(replacement, 5.09m);

        await Assert.ThrowsAsync<ValidationException>(() => Credit(replacement, 5.09m));
        await Withdraw(replacement);
        var julyFree = await Discount(new(2026, 7, 1), new(2026, 8, 1), 100);
        await Credit(julyFree, 2.55m);
        var view = await View(app, board, id);

        Assert.Equal(17.84m, view.Balances.BillingCredit);
        Assert.Single(view.Coverage);
        Assert.Equal(120m, Assert.Single(view.Invoices.SelectMany(x => x.Lines)).Line.Amount);
        Assert.Equal(0m, (await Run(app, sp => sp.GetRequiredService<BillingQueries>().Reconcile(board))).Difference);
    }

    [Fact]
    [Trait("Acceptance", "ADJ")]
    public async Task Zero_price_catchup_survives_missed_scans_and_price_changes_do_not_rewrite_issued_quotes()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, 1, new(2026, 6, 1), 20));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 6, 1), new(3026, 6, 1), DiscountKind.Percentage, 50, Guid.NewGuid(), "Discount original quote"));

        Assert.Equal(5m, (await View(app, board, id)).Invoices[0].Total);
        var firstDiscount = (await View(app, board, id)).Discounts.Single().Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().WithdrawDiscount(board, account, firstDiscount, Guid.NewGuid(), "Honorary instead"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().AddDiscount(board, account, 1, new(2026, 6, 1), new(3026, 6, 1), DiscountKind.Percentage, 100, Guid.NewGuid(), "Honorary"));
        On(9, 10);
        await Scan(app, account);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(view.Membership.Active);
        Assert.Equal(4, view.Coverage.Count);
        Assert.Empty(view.Movements);
        await using var db = Context();

        Assert.Empty(await db.Receipts.ToListAsync());
    }

}
