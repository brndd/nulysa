using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Nulysa.Components;
using Nulysa.Operations;
using Quartz;

namespace Nulysa.Integrations;

internal static class BankConnectionEndpoints
{
    internal const string PagePath = "/admin/payments/bank";
    internal const string CallbackPath = PagePath + "/callback";

    internal static void MapBankConnections(this WebApplication app)
    {
        var routes = app.MapGroup(PagePath).RequireAuthorization("Administrator");
        routes.MapPost("/start", (HttpContext context, BankConnections banks, UiFeedback feedback,
            [FromForm] string? bank, [FromForm] DateOnly? importFrom, [FromForm] Guid? renewalId, CancellationToken ct) =>
            Handle(context, feedback, async () => Results.Redirect(
                (await banks.Start(context.User, bank, importFrom, renewalId, ct)).AbsoluteUri), ct));

        routes.MapGet("/callback", (HttpContext context, BankConnections banks, UiFeedback feedback,
            string? state, string? code, string? error, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Handle(context, feedback, async () =>
            {
                if (!Guid.TryParse(state, out var id))
                    throw new ValidationException("Bank authorization expired or was already used. Start again.");
                var choose = await banks.Complete(context.User, id, error is null ? code : null, ct);
                if (choose)
                    return Results.Redirect($"{PagePath}?selection={id:D}");
                feedback.Save(context, "Bank consent renewed.");
                return Results.Redirect(PagePath);
            }, ct);
        });

        routes.MapPost("/connect", (HttpContext context, BankConnections banks, UiFeedback feedback,
            [FromForm] Guid selection, [FromForm] Guid account, CancellationToken ct) =>
            Handle(context, feedback, async () =>
            {
                await banks.Connect(context.User, selection, account, ct);
                feedback.Save(context, "Bank account connected.");
                return Results.Redirect(PagePath);
            }, ct));

        routes.MapPost("/sync", async (HttpContext context, BankConnections banks, UiFeedback feedback, ISchedulerFactory schedulers,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            if (!banks.Enabled)
                return Results.BadRequest();
            var scheduler = await schedulers.GetScheduler(ct);
            await scheduler.TriggerJob(new JobKey("bank-sync", Schedules.Group), cancellationToken: ct);
            feedback.Save(context, "Bank import queued successfully. Refresh this page in a moment to check the result.");
            return Results.Redirect(PagePath);
        });
    }

    private static async Task<IResult> Handle(HttpContext context, UiFeedback feedback, Func<Task<IResult>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (ValidationException ex)
        {
            feedback.SaveError(context, ex.Message);
        }
        catch (Exception ex) when (IsProviderError(ex, ct))
        {
            feedback.SaveError(context, "Enable Banking could not complete the request. Try again.");
        }
        return Results.Redirect(PagePath);
    }

    internal static bool IsProviderError(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is HttpRequestException or JsonException or CryptographicException or
            IOException or ArgumentException or KeyNotFoundException or FormatException or OperationCanceledException;
}
