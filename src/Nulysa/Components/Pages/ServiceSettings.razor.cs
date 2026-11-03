using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Nulysa.Billing;
using Nulysa.Services;
using static Nulysa.Localization.UiText;

namespace Nulysa.Components.Pages;

public partial class ServiceSettings
{
    public sealed class ServiceForm
    {
        [Required, StringLength(80)] public string Code { get; set; } = "";
        [Required, StringLength(200)] public string Name { get; set; } = "";
        [Required, StringLength(80)] public string Category { get; set; } = "";
        public bool SelfSubscribe { get; set; }
        [Range(0, 365)] public int FreeDays { get; set; } = 14;
        [Range(0, 365)] public int GraceDays { get; set; } = 7;
        [Range(0, int.MaxValue)] public int Priority { get; set; } = 100;
        public string Retirement { get; set; } = "leave";
    }
    [Parameter] public long? Id { get; set; }
    [SupplyParameterFromForm(FormName = "service-definition")] public ServiceForm? Service { get; set; }
    [CascadingParameter] private HttpContext HttpContext { get; set; } = null!;
    private ClaimsPrincipal actor = null!;
    private List<ServiceDefinition> services = [];
    private List<FeeDefinition> fees = [];
    private string? error;
    private string? notice;
    private bool found;
    private bool IsEditor => Id is not null || AdminNavigation.Path(Navigation.Uri).EndsWith("/new", StringComparison.Ordinal);
    private string Title => T(!IsEditor ? "Services" : Id is null ? "Create service" : "Edit service");
    private IReadOnlyList<BreadcrumbItem> Trail => !IsEditor ? [] : [new(T("Services"), "/admin/settings/services"), new(Title)];

    protected override async Task OnInitializedAsync()
    {
        actor = (await Authentication.GetAuthenticationStateAsync()).User;
        notice = Feedback.Read(HttpContext);
        services = await Queries.AllServices(actor);
        fees = await Queries.Fees(actor);
        if (Id is { } id)
        {
            var existing = services.SingleOrDefault(service => service.Id == id);
            if (existing is null)
            {
                found = false;
                HttpContext.Response.StatusCode = 404;
                return;
            }
            Service ??= new()
            {
                Code = existing.Code,
                Name = existing.Name,
                Category = fees.Single(fee => fee.ServiceDefinitionId == id).Category,
                SelfSubscribe = existing.SelfSubscriptionAllowed,
                FreeDays = existing.InitialFreeDays,
                GraceDays = existing.RenewalGraceDays,
                Priority = existing.Priority,
                Retirement = existing.Retired ? "retired" : "active"
            };
        }
        Service ??= new();
        found = true;
    }

    private async Task SaveService()
    {
        if (!found || !IsEditor)
            return;
        try
        {
            var retired = Service!.Retirement switch { "retired" => (bool?)true, "active" => false, _ => null };
            await Commands.ConfigureService(actor, Id, new(Service.Code, Service.Name, Service.SelfSubscribe, Service.FreeDays,
                Service.GraceDays, Service.Priority, Service.Category, retired));
            Feedback.Save(HttpContext, "Service definition saved.");
            Navigation.NavigateTo("/admin/settings/services");
        }
        catch (ValidationException ex)
        {
            error = ex.Message;
        }
    }
}
