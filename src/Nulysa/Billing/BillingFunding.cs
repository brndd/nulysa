using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Nulysa.Billing;

public sealed partial class BillingCommands
{
    private async Task<decimal> Fund(BillingFacts facts, BillingOperation operation, InvoiceLine line, decimal available, DateOnly fundsAvailableOn, long? receiptId, CancellationToken ct)
    {
        // The journal keeps this event's date; fulfilment cannot precede earlier required funding.
        var effectiveFundingOn = line.LastFundingOn is { } previousFunding && previousFunding > fundsAvailableOn
            ? previousFunding : fundsAvailableOn;
        var canFund = line.State == LineState.Open
            && effectiveFundingOn < line.ExpiresOn
            && (line.ServiceEnrollmentId is not { } enrollmentId
                || facts.Enrollments.Any(x => x.Id == enrollmentId && x.EndedAt is null && x.RenewalStoppedAt is null));
        if (!canFund)
            return 0;

        var proposedQuote = await ResolveFundingQuote(facts, line, effectiveFundingOn, ct);
        var canAcceptQuote = proposedQuote is not null && facts.Held(line) + available >= proposedQuote.Quote.Amount;
        // A shorter receipt-date quote is accepted only with enough money; waiting alone buys no time.
        if (canAcceptQuote)
            Requote(facts, operation, line, proposedQuote!.Quote, proposedQuote.Period);

        var fundingAmount = Math.Min(available, Math.Max(0, line.Amount - facts.Held(line)));
        Move(facts, operation, receiptId is null ? MovementKind.BillingToLine : MovementKind.ReceiptToLine, fundingAmount, fundsAvailableOn, line.Id, receiptId);
        if (fundingAmount > 0)
            line.LastFundingOn = effectiveFundingOn;
        if (facts.Held(line) == line.Amount)
            Complete(facts, operation, line, line.Amount == 0 ? null : effectiveFundingOn);

        await db.SaveChangesAsync(ct);
        return fundingAmount;
    }

    private sealed record FundingQuote(Quote Quote, Period Period);

    // Resolving a quote reads history but never revises a line, reserves money or grants coverage.
    private async Task<FundingQuote?> ResolveFundingQuote(BillingFacts facts, InvoiceLine line, DateOnly effectiveFundingOn, CancellationToken ct)
    {
        var isPricedAtFunding = line.Purpose == PurchasePurpose.Resumption
            || (line.Purpose == PurchasePurpose.Initial && line.Benefit == Benefit.Service);
        if (!isPricedAtFunding)
            return null;

        var pricingJson = line.PricingJson;
        var predatesCurrentQuote = effectiveFundingOn < line.StartsOn && line.StartsOn > line.OriginalStartsOn;
        // A rollover or earlier funding event may have replaced the quote this receipt belonged to.
        if (predatesCurrentQuote)
        {
            var quoteDate = effectiveFundingOn > line.OriginalStartsOn ? effectiveFundingOn : line.OriginalStartsOn;
            pricingJson = await db.LineAdjustments
                .Where(x => x.InvoiceLineId == line.Id && x.OldStart <= quoteDate && quoteDate < x.OldEnd)
                .OrderByDescending(x => x.Id)
                .Select(x => x.PricingJson)
                .FirstOrDefaultAsync(ct)
                ?? line.OriginalPricingJson;
        }

        var segments = JsonSerializer.Deserialize<List<PriceSegment>>(pricingJson)!;
        var quote = Pricing.Slice(segments, effectiveFundingOn, facts.Periods(line.Benefit, line.ServiceDefinitionId));
        var needsRepricing = quote.Segments.Count == 0
            || (line.Purpose == PurchasePurpose.Resumption && effectiveFundingOn < line.OriginalStartsOn);
        if (needsRepricing)
            quote = QuoteUnquotedDays(facts, line, effectiveFundingOn, quote);

        return quote.Segments.Count == 0 ? null : new(quote,
            new(quote.Segments.Min(segment => segment.Start), quote.Segments.Max(segment => segment.End)));
    }

    private Quote QuoteUnquotedDays(BillingFacts facts, InvoiceLine line, DateOnly effectiveFundingOn, Quote quote)
    {
        // Keep already-quoted segments; only the missing prefix uses dated price/discount facts.
        var invoice = facts.Invoices.Single(candidate => candidate.Id == line.InvoiceId);
        var group = facts.Groups.Single(candidate => candidate.Id == invoice.BillingGroupId);
        var period = BillingCalendar.Bridge(group.Anchor, group.IntervalMonths, effectiveFundingOn, false);
        var unquotedEnd = quote.Segments.Count == 0 || period.End < quote.Segments[0].Start
            ? period.End
            : quote.Segments[0].Start;
        var discounts = facts.Discounts
            .Where(discount => discount.FeeDefinitionId == line.FeeDefinitionId)
            .Select(discount =>
            {
                var end = discount.WithdrawnAt is { } withdrawn
                    ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(withdrawn, clock.Zone).DateTime)
                    : discount.EndsOn;
                return new Discount
                {
                    Id = discount.Id,
                    StartsOn = discount.StartsOn,
                    EndsOn = end < discount.EndsOn ? end : discount.EndsOn,
                    Kind = discount.Kind,
                    Value = discount.Value
                };
            })
            .Where(discount => discount.StartsOn < discount.EndsOn)
            .ToList();
        var prices = facts.Prices.Where(x => x.FeeDefinitionId == line.FeeDefinitionId).ToList();
        var prefix = Pricing.Calculate(group.Anchor, group.IntervalMonths, new(effectiveFundingOn, unquotedEnd), prices,
            discounts, facts.Periods(line.Benefit, line.ServiceDefinitionId));
        return Pricing.FromSegments(prefix.Segments.Concat(quote.Segments
            .Where(x => x.Start < period.End)
            .Select(x => x with { End = x.End > period.End ? period.End : x.End })));
    }

    private async Task AllocateCredit(BillingFacts facts, BillingOperation operation, DateOnly appliedOn, CancellationToken ct)
    {
        foreach (var line in OrderedLines(facts, facts.Invoices.Where(x => x.BillingGroupId is not null)))
        {
            var available = facts.Balances.BillingCredit;
            if (available <= 0)
                break;

            await Fund(facts, operation, line, available, appliedOn, null, ct);
        }
    }
}
