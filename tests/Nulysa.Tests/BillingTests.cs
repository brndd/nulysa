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

[Trait("Group", "Billing")]
public sealed partial class BillingTests : MembershipTest
{
    private void On(int month, int day) => Clock.Now = new DateTimeOffset(2026, month, day, 10, 0, 0, TimeSpan.Zero);
    private Task<BillingView> View(WebApplication app, ClaimsPrincipal actor, long id) => Run(app, async sp => (await sp.GetRequiredService<BillingQueries>().Member(actor, id))!);
    private Task<long> Pay(WebApplication app, string reference, decimal amount, DateOnly? received = null, string? sourceId = null)
        => Run(app, sp => sp.GetRequiredService<BillingCommands>().Import(new("test-bank", "association", sourceId ?? Guid.NewGuid().ToString(), amount, "EUR",
            received ?? DateOnly.FromDateTime(Clock.Now.DateTime), reference, "Payment", "Synthetic payer")));
    private Task Scan(WebApplication app, long account) => Run(app, sp => sp.GetRequiredService<BillingCommands>().Scan(account));
    private async Task<long> Service(WebApplication app, ClaimsPrincipal board, int free = 0, int grace = 7, bool self = false, decimal price = 30)
    {
        var id = await Run(app, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, null, new("door", "Door", self, free, grace, 10, "door")));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == id);
        await Run(app, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2000, 1, 1), price));
        return id;
    }

}
