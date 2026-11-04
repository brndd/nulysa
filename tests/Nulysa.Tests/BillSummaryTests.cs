using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nulysa.Billing;
using Nulysa.Components;

namespace Nulysa.Tests;

public sealed class BillSummaryTests
{
    [Theory]
    [InlineData(null, true, false, false)]
    [InlineData(LineState.Open, true, false, true)]
    [InlineData(LineState.Fulfilled, true, false, false)]
    [InlineData(LineState.Expired, true, true, false)]
    [InlineData(LineState.Open, false, false, false)]
    public async Task Membership_paid_note_only_explains_an_open_bill_with_unpaid_other_items(
        LineState? serviceState, bool membershipPaid, bool closed, bool showNote)
    {
        var invoice = new InvoiceView(new Invoice { Id = 1, ClosedAt = closed ? DateTimeOffset.UtcNow : null }, "123", [
            new(new InvoiceLine { Benefit = Benefit.Membership, State = membershipPaid ? LineState.Fulfilled : LineState.Open }, 0, membershipPaid ? 0 : 10)
        ]);
        if (serviceState is { } state)
            invoice.Lines.Add(new(new InvoiceLine { Benefit = Benefit.Service, State = state }, 0, state == LineState.Fulfilled ? 0 : 10));
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<BillSummary>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Invoice"] = invoice }));
            return component.ToHtmlString();
        });
        Assert.Equal(showNote, html.Contains("Membership fee paid.", StringComparison.Ordinal));
    }
}
