using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Nulysa.Billing;
using Nulysa.Integrations;

namespace Nulysa.Components.Pages;

public partial class Payments
{
    [CascadingParameter] private HttpContext HttpContext { get; set; } = null!;
    private string Destination => AdminNavigation.Resolve("/" + Navigation.ToBaseRelativePath(Navigation.Uri))?.Key ?? "payments";
    private string Area => Destination switch { "payments" => "review", "retained" => "claims", _ => Destination };
    private string Title => AdminNavigation.Sections.SelectMany(section => section.Pages).Single(page => page.Key == Destination).Label;
    private string? notice;
    public sealed class ImportInput
    {
        [Required, StringLength(40)] public string Source { get; set; } = "bank";
        [Required, StringLength(200)] public string SourceAccount { get; set; } = "";
        [Required, StringLength(200)] public string ExternalId { get; set; } = "";
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "EUR";
        public DateOnly ReceivedOn { get; set; }
        public string Reference { get; set; } = "";
        public string Message { get; set; } = "";
        public string Counterparty { get; set; } = "";
    }
    [SupplyParameterFromForm(FormName = "manual-receipt")] public ImportInput? Import { get; set; }
    private ClaimsPrincipal actor = null!;
    private List<Receipt> receipts = [];
    private List<BankSyncState> statuses = [];
    private List<FinancialClaim> claims = [];
    private Reconciliation? reconciliation;
    private string? error;
    protected override async Task OnInitializedAsync()
    {
        Import ??= new() { ReceivedOn = Clock.Today };
        notice = Feedback.Read(HttpContext);
        actor = (await Authentication.GetAuthenticationStateAsync()).User;
        if (Area == "review")
        {
            receipts = await Queries.Review(actor);
            statuses = await Queries.BankStatus(actor);
        }
        if (Area == "claims")
            claims = await Queries.Claims(actor);
        if (Area == "accounting")
            reconciliation = await Queries.Reconcile(actor);
    }
    private Task ImportReceipt() => Handle(async () => await Commands.ImportManual(actor, new(Import!.Source, Import.SourceAccount, Import.ExternalId, Import.Amount, Import.Currency, Import.ReceivedOn, Import.Reference, Import.Message, Import.Counterparty)));
    private async Task Handle(Func<Task> action)
    {
        try
        {
            await action();
            Feedback.Save(HttpContext, "Bank transaction imported. Review its matching result in Payment review.");
            Navigation.NavigateTo(Navigation.Uri);
        }
        catch (ValidationException ex)
        {
            error = ex.Message;
        }
    }

}
