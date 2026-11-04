using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Nulysa.Billing;
using Nulysa.Integrations;
using Nulysa.Localization;
using static Nulysa.Localization.UiText;

namespace Nulysa.Components.Pages;

public partial class Payments
{
    [CascadingParameter] private HttpContext HttpContext { get; set; } = null!;
    private string Destination => AdminNavigation.Resolve("/" + Navigation.ToBaseRelativePath(Navigation.Uri))?.Key ?? "payments";
    private string Area => Destination switch { "payments" => "review", "retained" => "claims", _ => Destination };
    private string Title => T(AdminNavigation.Sections.SelectMany(section => section.Pages).Single(page => page.Key == Destination).Label);
    private string? notice;
    public sealed class ImportInput
    {
        [Required, StringLength(40)] public string Source { get; set; } = "bank";
        [Required, StringLength(200)] public string SourceAccount { get; set; } = "";
        [Required, StringLength(200)] public string ExternalId { get; set; } = "";
        public MoneyAmount Amount { get; set; }
        public string Currency { get; set; } = "EUR";
        public DateOnly ReceivedOn { get; set; }
        public string Reference { get; set; } = "";
        public string Message { get; set; } = "";
        public string Counterparty { get; set; } = "";
    }
    [SupplyParameterFromForm(FormName = "manual-receipt")] public ImportInput? Import { get; set; }
    private ClaimsPrincipal actor = null!;
    private static readonly (string Key, string Label)[] Columns =
    [
        ("date", "Date"), ("amount", "Amount"), ("reference", "Reference"),
        ("counterparty", "Counterparty"), ("message", "Message"), ("reason", "Review reason"), ("status", "Status")
    ];
    [SupplyParameterFromQuery(Name = "page")] public string? RequestedPage { get; set; }
    [SupplyParameterFromQuery(Name = "sort")] public string? RequestedSort { get; set; }
    [SupplyParameterFromQuery(Name = "dir")] public string? RequestedDirection { get; set; }
    [SupplyParameterFromQuery(Name = "scope")] public string? RequestedScope { get; set; }
    [SupplyParameterFromQuery(Name = "review")] public string? RequestedReview { get; set; }
    private string Sort => Columns.Any(column => column.Key == RequestedSort) ? RequestedSort! : "date";
    private bool Descending => RequestedDirection == "desc";
    private bool All => RequestedScope == "all";
    private long? ReviewId => long.TryParse(RequestedReview, out var id) ? id : null;
    private PaymentReviewPage? payments;

    private string QueueUrl(int page, string? sort = null, bool? descending = null, bool? all = null) =>
        QueryHelpers.AddQueryString("/admin/payments", new Dictionary<string, string?>
        {
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sort"] = sort ?? Sort,
            ["dir"] = (descending ?? Descending) ? "desc" : "asc",
            ["scope"] = (all ?? All) ? "all" : null
        });

    private static string RowAnchor(long id) => $"#payment-{id}";
    private static bool NeedsReview(Receipt row) => row.State is ReceiptState.New or ReceiptState.Review;
    private static string Status(Receipt row) => row.State switch
    {
        ReceiptState.Posted => "Assigned",
        ReceiptState.Ignored => "Ignored",
        _ => "Needs review"
    };
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
            var page = int.TryParse(RequestedPage, out var number) ? number : 1;
            payments = await Administration.Review(actor, All, Sort, Descending, page);
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
