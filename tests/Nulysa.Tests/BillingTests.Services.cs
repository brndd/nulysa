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
    public async Task Typed_service_end_reasons_round_trip_the_existing_database_and_audit_codes()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var service = await Service(app, board);
        await using var db = Context();
        var reasons = new[]
        {
            (Nulysa.Services.ServiceEndReason.Nonpayment, "nonpayment"),
            (Nulysa.Services.ServiceEndReason.ServiceRetired, "service-retired"),
            (Nulysa.Services.ServiceEndReason.Stopped, "stopped"),
            (Nulysa.Services.ServiceEndReason.AdministratorRemoval, "administrator-removal"),
            (Nulysa.Services.ServiceEndReason.MembershipEnded, "membership-ended")
        };
        foreach (var (reason, code) in reasons)
        {
            var enrollment = new Nulysa.Services.ServiceEnrollment
            {
                UserId = id,
                ServiceDefinitionId = service,
                StartedAt = Clock.Now,
                EndedAt = Clock.Now,
                EndReason = reason
            };
            db.ServiceEnrollments.Add(enrollment);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.Equal(code, await db.Database.SqlQuery<string>($"""SELECT "EndReason" AS "Value" FROM "ServiceEnrollments" WHERE "Id" = {enrollment.Id}""").SingleAsync());
            Assert.Equal(reason, (await db.ServiceEnrollments.SingleAsync(row => row.Id == enrollment.Id)).EndReason);
            Assert.Equal(code, Nulysa.Services.ServiceEndReasons.Code(reason));
        }
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Retired_services_are_hidden_and_cannot_be_subscribed()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var service = await Service(app, board);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 7, 10, "door", Retired: true)));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 7, 10, "door")));
        var services = await Run(app, sp => sp.GetRequiredService<BillingQueries>().Services(Actor(id)));

        Assert.DoesNotContain(services, x => x.Id == service);
        await Assert.ThrowsAsync<ValidationException>(() => Run(app, sp => sp.GetRequiredService<BillingCommands>()
            .Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid())));
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Retiring_a_service_stops_renewals_and_keeps_paid_coverage()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, view.AccountId, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        view = await View(app, board, id);
        Assert.Contains(view.Forecasts, x => x.Description == "Door");
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Door", false, 0, 0, 10, "door", Retired: true)));
        view = await View(app, board, id);
        await using (var db = Context())
        {
            var serviceFee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service);
            Assert.DoesNotContain(view.Forecasts, x => x.Description == "Door");
            await Assert.ThrowsAsync<ValidationException>(() =>
                Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, serviceFee.Id, new(2026, 6, 1), 40)));
        }
        On(6, 20);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.DoesNotContain(view.Invoices.SelectMany(x => x.Lines),
            x => x.Line.Benefit == Benefit.Service && x.Line.Purpose is PurchasePurpose.Renewal or PurchasePurpose.Alignment);
        Assert.Contains(view.Coverage, x => x.Benefit == Benefit.Service && x.EndExclusive == new DateOnly(2026, 7, 1));
        await using (var db = Context())
        {
            Assert.True((await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service)).Retired);
            Assert.False((await db.Fees.SingleAsync(x => x.Id == 1)).Retired);
        }
        On(7, 2);
        await Scan(app, view.AccountId);
        view = await View(app, board, id);

        Assert.Empty(view.Subscriptions);
        await using var after = Context();
        Assert.Contains(await after.ServiceChanges.ToListAsync(), x => x.Action == "service-retired");
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Free_days_are_once_only_and_independent_from_payment_window_and_stopping_preserves_coverage()
    {
        On(3, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        await Pay(app, view.Invoices[0].Reference, 10);
        var service = await Service(app, board, free: 20);
        On(3, 10);
        var key = Guid.NewGuid();
        var enrollment = await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, key));

        Assert.Equal(enrollment, await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, key)));
        view = await View(app, board, id);
        var grant = Assert.Single(view.Coverage, x => x.Source == CoverageSource.InitialFree);

        Assert.Equal(new DateOnly(2026, 3, 30), grant.EndExclusive);
        var serviceLine = view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Benefit == Benefit.Service).Line;

        Assert.Equal(new DateOnly(2026, 3, 30), serviceLine.StartsOn);
        Assert.Equal(new DateOnly(2026, 5, 1), serviceLine.EndExclusive);
        Assert.Equal(new DateOnly(2026, 3, 25), serviceLine.PaymentCutoffExclusive);
        On(3, 25);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Equal(LineState.Expired, view.Invoices.SelectMany(x => x.Lines).Single(x => x.Line.Id == serviceLine.Id).Line.State);
        On(3, 30);
        await Scan(app, account);

        Assert.Empty((await View(app, board, id)).Subscriptions);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, false, Guid.NewGuid()));
        view = await View(app, board, id);

        Assert.Single(view.Coverage, x => x.Source == CoverageSource.InitialFree);
        Assert.False(Assert.Single(view.Subscriptions).Standing.Active);
        var current = view.Subscriptions[0].Enrollment.Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(board, account, current, true, Guid.NewGuid(), "Explicit removal"));
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 1, true, Guid.NewGuid()));
        view = await View(app, board, id);

        Assert.Single(view.Coverage, x => x.Source == CoverageSource.AdministratorGrant);
        current = view.Subscriptions[0].Enrollment.Id;
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().StopService(Actor(id), account, current, false, Guid.NewGuid(), "Stop renewal"));
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.All(view.Invoices.SelectMany(x => x.Lines).Where(x => x.Line.Benefit == Benefit.Service), x => Assert.NotEqual(LineState.Open, x.Line.State));
    }

    [Fact]
    [Trait("Acceptance", "SVC")]
    public async Task Membership_lapse_does_not_freeze_paid_services_and_service_expiry_works_without_a_renewal_invoice()
    {
        On(6, 1);
        var board = await Board();
        await using var app = CommandsApp();
        var id = await Admit(app, board);
        var view = await View(app, board, id);
        var account = view.AccountId;
        var membershipReference = view.Invoices[0].Reference;
        await Pay(app, membershipReference, 10);
        var service = await Service(app, board, grace: 0);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().Subscribe(board, account, service, 3, false, Guid.NewGuid()));
        view = await View(app, board, id);
        var serviceBill = view.Invoices.Single(x => x.Lines.Any(l => l.Line.Benefit == Benefit.Service));
        await Pay(app, serviceBill.Reference, serviceBill.Remaining);
        On(6, 20);
        await Scan(app, account);
        On(7, 9);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.False(view.Membership.Active);
        Assert.False(Assert.Single(view.Subscriptions).Standing.Active);
        Assert.Equal(new DateOnly(2026, 9, 1), view.Coverage.Where(x => x.Benefit == Benefit.Service).Max(x => x.EndExclusive));
        On(8, 15);
        await Pay(app, membershipReference, 10);
        view = await View(app, board, id);

        Assert.True(Assert.Single(view.Subscriptions).Standing.Active);
        // No intervening scan issues a renewal; dated eligibility still expires both benefits.
        On(9, 9);
        await Scan(app, account);
        view = await View(app, board, id);

        Assert.Empty(view.Subscriptions);
        Assert.Equal(new DateOnly(2026, 9, 1), view.Coverage.Where(x => x.Benefit == Benefit.Service).Max(x => x.EndExclusive));
    }

}
