using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Nulysa.Billing;
using Nulysa.Integrations;
using static Nulysa.Localization.UiText;

namespace Nulysa.Components.Pages;

public partial class ReceiptReview
{
    public sealed class ReviewInput
    {
        public Guid Key { get; set; } = Guid.NewGuid();
        public long Target { get; set; }
        [Required, StringLength(1000)] public string Reason { get; set; } = "";
        public bool Confirmed { get; set; }
    }
    [Parameter] public long ReceiptId { get; set; }
    [SupplyParameterFromQuery(Name = "purpose")] public string? RequestedPurpose { get; set; }
    [SupplyParameterFromQuery(Name = "account")] public string? RequestedAccount { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? Search { get; set; }
    [SupplyParameterFromForm] public ReviewInput? Input { get; set; }
    [CascadingParameter] private HttpContext HttpContext { get; set; } = null!;
    private string Purpose => Purposes.ContainsKey(RequestedPurpose ?? "") ? RequestedPurpose! : "";
    private long? MemberAccount => long.TryParse(RequestedAccount, out var id) ? id : null;
    private static readonly Dictionary<string, string> Purposes = new() { ["ignore"] = "Read / ignore without allocation", ["billing"] = "Member billing credit", ["group"] = "Recurring group", ["invoice"] = "One-off invoice", ["deposit"] = "Purchase balance deposit" };
    private ClaimsPrincipal actor = null!;
    private Receipt? receipt;
    private PaymentMember? member;
    private List<PaymentMember> members = [];
    private Dictionary<long, string> destinations = [];
    private string? error;
    private string? effect;
    private bool reviewing;
    protected override async Task OnInitializedAsync()
    {
        actor = (await Authentication.GetAuthenticationStateAsync()).User;
        receipt = await Administration.Receipt(actor, ReceiptId);
        if (receipt is null)
        {
            HttpContext.Response.StatusCode = 404;
            return;
        }
        Input ??= new();
        reviewing = Input.Confirmed;
        members = await Administration.Members(actor, Search, MemberAccount);
        member = MemberAccount is not null ? members.SingleOrDefault() : null;
        if (member is not null && Purpose is "group" or "invoice")
        {
            var view = await Queries.Account(actor, member.AccountId);
            destinations = Purpose == "group"
                ? view.Groups.ToDictionary(group => group.Id, group => T("{0}-month group · {1} · reference {2}", group.IntervalMonths, string.Join(", ", view.Items.Where(item => item.BillingGroupId == group.Id).Select(item => view.FeeNames[item.FeeDefinitionId])), group.PaymentReference))
                : view.Invoices.Where(invoice => invoice.Invoice.BillingGroupId is null && invoice.Invoice.ClosedAt is null && invoice.Remaining > 0)
                    .ToDictionary(invoice => invoice.Invoice.Id, invoice => T("{0} · bill #{1} · {2:0.00} EUR remaining · pay by {3:d MMM yyyy}", string.Join(", ", invoice.Lines.Select(row => row.Line.Description)), invoice.Invoice.Id, invoice.Remaining, invoice.Invoice.DueOn));
        }
    }
    private async Task Submit()
    {
        try
        {
            if (Purpose != "ignore" && (receipt!.Amount < 0 || receipt.Currency != "EUR"))
                throw new ValidationException("Only incoming EUR transactions can be assigned. Review the source facts and choose read / ignore where appropriate.");
            if (!Purposes.ContainsKey(Purpose) || (Purpose != "ignore" && member is null))
                throw new ValidationException("Choose a member and payment purpose.");
            if (Purpose is "group" or "invoice" && !destinations.ContainsKey(Input!.Target))
                throw new ValidationException("Choose an available destination belonging to this member.");
            effect = T("{0:0.00} {1} → {2}", receipt!.Amount, receipt.Currency, T(Purposes[Purpose])) + (member is null ? T(". No member funds will be allocated.") : T(" for {0} (#{1}).", member.Name, member.UserId)) + (destinations.TryGetValue(Input!.Target, out var destination) ? " " + destination : "");
            if (!Input.Confirmed)
            {
                reviewing = true;
                return;
            }
            if (Purpose == "ignore")
                await Commands.IgnoreReceipt(actor, ReceiptId, Input.Reason);
            else
            {
                var allocation = ReceiptDestination.FromSelection(
                    groupId: Purpose == "group" ? Input.Target : null,
                    purchaseDeposit: Purpose == "deposit",
                    invoiceId: Purpose == "invoice" ? Input.Target : null);
                await Commands.AssignReceipt(actor, ReceiptId, member!.AccountId, allocation, Input.Key, Input.Reason);
            }
            Feedback.Save(HttpContext, "Transaction disposition saved. Source facts were retained.");
            Navigation.NavigateTo("/admin/payments");
        }
        catch (ValidationException ex)
        {
            error = ex.Message;
            reviewing = false;
        }
    }

}
