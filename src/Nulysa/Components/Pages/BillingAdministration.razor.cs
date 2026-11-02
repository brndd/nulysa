using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Nulysa.Billing;
using Nulysa.Integrations;

namespace Nulysa.Components.Pages;

public partial class BillingAdministration
{
    public sealed class OneOffInput
    {
        public Guid Key { get; set; } = Guid.NewGuid();
        [Required, StringLength(300)] public string Description { get; set; } = "";
        [Required, StringLength(80)] public string Category { get; set; } = "";
        public decimal Amount { get; set; }
        public DateOnly DueOn { get; set; }
        public bool Confirmed { get; set; }
    }
    public sealed class AdjustmentInput
    {
        public Guid Key { get; set; } = Guid.NewGuid();

        public long FeeId { get; set; } = 1;
        public DiscountKind Kind { get; set; }
        public decimal Value { get; set; }
        public DateOnly Start { get; set; }
        public DateOnly Through { get; set; }
        public long DiscountId { get; set; }
        public long LineId { get; set; }
        public long InvoiceId { get; set; }
        public long ReceiptId { get; set; }
        public int Months { get; set; } = 1;
        public bool Purchase { get; set; }
        [Required, StringLength(1000)] public string Reason { get; set; } = "";
        public bool Confirmed { get; set; }
    }
    [Parameter] public long AccountId { get; set; }
    [Parameter] public long? UserId { get; set; }
    [SupplyParameterFromQuery(Name = "returnUrl")] public string? ReturnUrl { get; set; }
    [SupplyParameterFromQuery(Name = "invoice")] public long? SelectedInvoice { get; set; }
    [SupplyParameterFromQuery(Name = "backTo")] public string? BackTo { get; set; }
    [SupplyParameterFromQuery] public int Page { get; set; } = 1;
    [SupplyParameterFromQuery] public int PageSize { get; set; } = 25;
    [SupplyParameterFromForm(FormName = "one-off")] public OneOffInput? OneOff { get; set; }
    [SupplyParameterFromForm(FormName = "adjustment")] public AdjustmentInput? Adjustment { get; set; }
    private ClaimsPrincipal actor = null!;
    private BillingView? view;
    private List<FeeDefinition> fees = [];
    private string? error;
    [Parameter] public string? RequestedTask { get; set; }
    [CascadingParameter] private HttpContext HttpContext { get; set; } = null!;
    private Dictionary<string, string> OneOffValues => new() { ["OneOff.Description"] = OneOff!.Description, ["OneOff.Category"] = OneOff.Category, ["OneOff.Amount"] = Number(OneOff.Amount), ["OneOff.DueOn"] = OneOff.DueOn.ToString("yyyy-MM-dd") };
    private Dictionary<string, string> AdjustmentValues => new()
    {
        ["Adjustment.FeeId"] = Adjustment!.FeeId.ToString(),
        ["Adjustment.Kind"] = Adjustment.Kind.ToString(),
        ["Adjustment.Value"] = Number(Adjustment.Value),
        ["Adjustment.Start"] = Adjustment.Start.ToString("yyyy-MM-dd"),
        ["Adjustment.Through"] = Adjustment.Through.ToString("yyyy-MM-dd"),
        ["Adjustment.DiscountId"] = Adjustment.DiscountId.ToString(),
        ["Adjustment.LineId"] = Adjustment.LineId.ToString(),
        ["Adjustment.InvoiceId"] = Adjustment.InvoiceId.ToString(),
        ["Adjustment.ReceiptId"] = Adjustment.ReceiptId.ToString(),
        ["Adjustment.Months"] = Adjustment.Months.ToString(),
        ["Adjustment.Purchase"] = Adjustment.Purchase.ToString().ToLowerInvariant(),
        ["Adjustment.Reason"] = Adjustment.Reason
    };
    private static string Number(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private bool AvailableTask => Actions.ContainsKey(TaskName) && (view?.UserId is not null || TaskName is "refund" or "unassign");
    private string TaskName => RequestedTask ?? "";
    private string Title => TaskName.Length > 0 ? Actions.GetValueOrDefault(TaskName, "Financial action") : UserId is not null ? "Financial actions" : $"Retained balance #{AccountId}";
    private string DirectoryUrl => UserId is { } id ? RegistryNavigation.MemberUrl(id, ReturnUrl, MemberSection.Actions) : $"/admin/payments/retained-balances/{AccountId}";
    private string CancelUrl => UserId is { } id ? RegistryNavigation.BackTo(id, BackTo, ReturnUrl) : DirectoryUrl;
    private string ActionUrl(string task) => UserId is { } id ? RegistryNavigation.ActionUrl(id, task, ReturnUrl, BackTo) : DirectoryUrl + "/" + task;
    private static readonly (string Label, string[] Tasks)[] TaskGroups =
    [
        ("Discounts", ["discount", "withdraw", "credit"]),
        ("Membership grants", ["grant"]),
        ("Billing corrections", ["one-off", "cancel", "refund", "unassign"])
    ];
    private bool reviewing;
    private string? effect;
    private string? notice;
    private Nulysa.Membership.RegistryEntry? subject;
    private List<Receipt> receipts = [];
    private static readonly Dictionary<string, string> Actions = new() { ["discount"] = "Add discount", ["withdraw"] = "Withdraw discount", ["credit"] = "Apply discount to already paid time", ["grant"] = "Grant membership months", ["one-off"] = "Issue one-off request", ["cancel"] = "Cancel unfinished bill", ["refund"] = "Record completed refund", ["unassign"] = "Reverse receipt allocation" };
    private string Explanation => TaskName switch
    {
        "discount" => "Changes future pricing and may complete already funded requests. Completed purchases keep their original facts.",
        "withdraw" => "Withdraw this discount and reprice unfinished requests. Completed purchases are preserved.",
        "credit" => "Calculate the remaining credit for a completed purchase's unused future discounted time. Credit can immediately fund valid recurring bills.",
        "grant" => "Extend current paid or granted membership coverage by whole months, releasing holds for newly covered days.",
        "cancel" => "Cancel unfinished lines and release their held funds. Completed purchases remain unchanged; cancellation may affect standing.",
        "refund" => "Record money already sent through the bank. This form does not send money. Only available funds can be refunded.",
        _ => "Return an eligible unspent receipt to payment review for reassignment. Source facts remain unchanged; purchased coverage cannot be clawed back."
    };
    private string DiscountLabel(Discount discount) => $"{view!.FeeNames.GetValueOrDefault(discount.FeeDefinitionId)} · {discount.Kind} {discount.Value:0.##} · {BillingDateText.Period(discount.StartsOn, discount.EndExclusive)} · #{discount.Id}";
    protected override async Task OnInitializedAsync()
    {
        actor = (await Authentication.GetAuthenticationStateAsync()).User;
        Page = Math.Clamp(Page, 1, 1000000);
        PageSize = PageSize <= 0 ? 25 : Math.Min(PageSize, 100);
        notice = Feedback.Read(HttpContext);
        if (UserId is { } memberId)
        {
            subject = await Members.Subject(actor, memberId);
            if (subject is not null)
                view = await Queries.Member(actor, memberId, skip: (Page - 1) * PageSize, take: PageSize);
            if (view is not null)
                AccountId = view.AccountId;
        }
        else if (await Administration.AccountExists(actor, AccountId))
        {
            view = await Queries.Account(actor, AccountId, skip: (Page - 1) * PageSize, take: PageSize);
            if (view.UserId is not null)
                view = null;
        }
        if (view is null || (TaskName.Length > 0 && !AvailableTask))
        {
            HttpContext.Response.StatusCode = 404;
            view = null;
            return;
        }
        fees = await Queries.Fees(actor);
        if (TaskName == "unassign")
            receipts = await Administration.PostedReceipts(actor, AccountId);
        OneOff ??= new() { DueOn = Clock.Today.AddDays(14) };
        Adjustment ??= new() { Start = Clock.Today, Through = Clock.Today.AddMonths(1).AddDays(-1), InvoiceId = SelectedInvoice ?? 0 };
        reviewing = TaskName == "one-off" ? OneOff.Confirmed : Adjustment.Confirmed;
    }
    private Task CreateOneOff() => Handle(async () =>
    {
        if (TaskName != "one-off" || view!.UserId is null)
            throw new ValidationException("Choose the one-off request action.");
        var isValidAmount = OneOff!.Amount > 0 && Pricing.Round(OneOff.Amount) == OneOff.Amount;
        if (!isValidAmount || OneOff.DueOn <= Clock.Today)
            throw new ValidationException("Use a positive cent amount and a future deadline.");
        if (!OneOff.Confirmed)
        {
            reviewing = true;
            return;
        }
        await Commands.OneOff(actor, AccountId, OneOff.Key, OneOff.Description, OneOff.Category, OneOff.Amount, OneOff.DueOn);
        Saved();
    });
    private void Saved()
    {
        Feedback.Save(HttpContext, "Financial action saved. Review updated balances and coverage.");
        Navigation.NavigateTo(CancelUrl);
    }
    private Task Adjust() => Handle(async () =>
    {
        var a = Adjustment!;
        if (!AvailableTask || TaskName == "one-off")
            throw new ValidationException("Choose a financial action.");
        effect = Explanation;
        switch (TaskName)
        {
            case "discount":
                var isAvailableFee = fees.Any(fee => fee.Id == a.FeeId && !fee.Retired);
                var areValidDates = a.Start >= Clock.Today && a.Through >= a.Start && a.Through < DateOnly.MaxValue;
                var isValidValue = a.Value >= 0 && Pricing.Round(a.Value) == a.Value
                    && (a.Kind != DiscountKind.Percentage || a.Value <= 100);
                if (!isAvailableFee || !areValidDates || !isValidValue)
                    throw new ValidationException("Choose a fee, future dates and a valid discount value.");
                effect += $" {fees.Single(fee => fee.Id == a.FeeId).Name}: {a.Kind} {a.Value}, {a.Start:d MMM yyyy} through {a.Through:d MMM yyyy}.";
                break;
            case "withdraw":
            case "credit":
                var discount = view!.Discounts.SingleOrDefault(discount => discount.Id == a.DiscountId && discount.WithdrawnAt == null) ?? throw new ValidationException("Choose an available discount in this account.");
                effect += " " + DiscountLabel(discount);
                if (TaskName == "credit")
                {
                    var credit = await Queries.DiscountCreditPreview(actor, AccountId, a.LineId, a.DiscountId);
                    if (credit <= 0 || (a.Confirmed && credit != a.Value))
                        throw new ValidationException("The remaining credit changed or is zero. Review the selection again.");
                    a.Value = credit;
                    effect += $" Credit {credit:0.00} EUR for purchase #{a.LineId}.";
                }
                break;
            case "grant":
                var end = await Queries.MembershipGrantEndExclusive(actor, AccountId, a.Months);
                if (a.Confirmed && a.Through != end.AddDays(-1))
                    throw new ValidationException("Coverage changed. Review the grant again.");
                a.Through = end.AddDays(-1);
                effect += $" {a.Months} months; covered through {a.Through:d MMM yyyy}.";
                break;
            case "cancel":
                var bill = view!.Invoices.SingleOrDefault(invoice => invoice.Invoice.Id == a.InvoiceId && invoice.Lines.Any(row => row.Line.State == LineState.Open)) ?? throw new ValidationException("Choose a bill with unfinished lines in this account.");
                effect += $" Bill #{bill.Invoice.Id}, {bill.Remaining:0.00} EUR remaining; {bill.Held:0.00} EUR held will be released.";
                break;
            case "refund":
                var available = a.Purchase ? view!.Balances.PurchaseBalance : view!.Balances.BillingCredit;
                if (a.Value <= 0 || Pricing.Round(a.Value) != a.Value || a.Value > available)
                    throw new ValidationException("Use a positive cent amount within the available balance.");
                effect += $" {a.Value:0.00} EUR from {(a.Purchase ? "purchase balance" : "billing credit")}, currently {available:0.00} EUR available.";
                break;
            case "unassign":
                var receipt = receipts.SingleOrDefault(receipt => receipt.Id == a.ReceiptId) ?? throw new ValidationException("Choose a posted receipt in this account.");
                effect += $" {receipt.ReceivedOn}: {receipt.Amount:0.00} {receipt.Currency}, {receipt.Counterparty}, {receipt.Reference}.";
                break;
        }
        if (!a.Confirmed)
        {
            reviewing = true;
            return;
        }
        switch (TaskName)
        {
            case "discount":
                await Commands.AddDiscount(actor, AccountId, a.FeeId, a.Start, a.Through.AddDays(1), a.Kind, a.Value, a.Key, a.Reason);
                break;
            case "withdraw":
                await Commands.WithdrawDiscount(actor, AccountId, a.DiscountId, a.Key, a.Reason);
                break;
            case "credit":
                await Commands.CreditFutureDiscount(actor, AccountId, a.LineId, a.DiscountId, a.Value, a.Key, a.Reason);
                break;
            case "grant":
                await Commands.GrantMembershipMonths(actor, AccountId, a.Months, a.Key, a.Reason);
                break;
            case "cancel":
                await Commands.CancelInvoice(actor, AccountId, a.InvoiceId, a.Key, a.Reason);
                break;
            case "refund":
                await Commands.Refund(actor, AccountId, a.Key, a.Value, a.Purchase, a.Reason);
                break;
            case "unassign":
                await Commands.UnassignReceipt(actor, AccountId, a.ReceiptId, a.Key, a.Reason);
                break;
            default:
                throw new ValidationException("Choose an adjustment.");
        }
        Saved();
    });
    private async Task Handle(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ValidationException ex)
        {
            error = ex.Message;
            reviewing = false;
        }
    }
    private static string StatementTitle(BalanceKind balance) => balance switch
    {
        BalanceKind.BillingCredit => "Billing credit statement",
        BalanceKind.PurchaseBalance => "Purchase balance statement",
        _ => "Held/funding movements"
    };

}
