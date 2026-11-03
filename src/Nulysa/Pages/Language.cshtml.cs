using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nulysa.Localization;
using static Nulysa.Localization.UiText;

namespace Nulysa.Pages;

[AllowAnonymous]
public sealed class LanguageModel : PageModel
{
    [BindProperty] public string Language { get; set; } = "automatic";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) && !ReturnUrl!.StartsWith("/Language", StringComparison.OrdinalIgnoreCase) ? ReturnUrl : "/";
    public bool Saved { get; private set; }

    public void OnGet(bool saved = false)
    {
        Language = UiLanguages.Find(Request.Cookies[UiLanguages.Cookie])?.Code ?? "automatic";
        Saved = saved;
    }

    public IActionResult OnPost() => Save(returnToCaller: false);
    public IActionResult OnPostSelect() => Save(returnToCaller: true);

    private IActionResult Save(bool returnToCaller)
    {
        if (Language != "automatic" && UiLanguages.Find(Language)?.Code != Language)
            ModelState.AddModelError(nameof(Language), T("Choose a listed language."));
        if (!ModelState.IsValid)
            return Page();

        var options = new CookieOptions { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true };
        if (Language == "automatic")
            Response.Cookies.Delete(UiLanguages.Cookie, options);
        else
            Response.Cookies.Append(UiLanguages.Cookie, Language, options);
        return returnToCaller ? LocalRedirect(SafeReturnUrl) : RedirectToPage(new { saved = true, ReturnUrl = SafeReturnUrl });
    }
}
