using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Nulysa.Communications;

namespace Nulysa.Pages.Communications;

// The signed token authorizes the change without a session, including for mail clients.
[AllowAnonymous, EnableRateLimiting("newsletter-unsubscribe"), IgnoreAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NewsletterUnsubscribeModel(NewsletterPreferences preferences) : PageModel
{
    public string? Token { get; private set; }
    public bool Complete { get; private set; }

    public IActionResult OnGet([FromQuery] string? token)
    {
        if (!preferences.IsValidUnsubscribeToken(token))
            Response.StatusCode = StatusCodes.Status400BadRequest;
        else
            Token = token;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] string? token, CancellationToken ct) =>
        await preferences.Unsubscribe(token, ct) ? new NoContentResult() : BadRequest();

    public async Task<IActionResult> OnPostConfirmAsync([FromForm] string? token, CancellationToken ct)
    {
        Complete = await preferences.Unsubscribe(token, ct);
        if (!Complete)
            Response.StatusCode = StatusCodes.Status400BadRequest;
        return Page();
    }
}
