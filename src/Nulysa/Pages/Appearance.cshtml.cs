using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nulysa.Components;

namespace Nulysa.Pages;

[AllowAnonymous]
public sealed class AppearanceModel : PageModel
{
    [BindProperty] public string Theme { get; set; } = "association";
    [BindProperty] public string Mode { get; set; } = "system";
    [BindProperty] public string? ReturnUrl { get; set; }
    public bool Saved { get; private set; }

    public void OnGet(bool saved = false)
    {
        var theme = Request.Cookies[Appearance.ThemeCookie];
        var mode = Request.Cookies[Appearance.ModeCookie];
        Theme = Appearance.IsTheme(theme) ? theme! : "association";
        Mode = Appearance.IsMode(mode) ? mode! : "system";
        Saved = saved;
    }

    public IActionResult OnPost()
    {
        if (Theme != "association" && !Appearance.IsTheme(Theme))
            ModelState.AddModelError(nameof(Theme), "Choose a listed theme.");
        if (!Appearance.IsMode(Mode))
            ModelState.AddModelError(nameof(Mode), "Choose a listed colour mode.");
        if (!ModelState.IsValid)
            return Page();

        var options = CookieOptions();
        if (Theme == "association")
            Response.Cookies.Delete(Appearance.ThemeCookie, options);
        else
            Response.Cookies.Append(Appearance.ThemeCookie, Theme, options);
        Response.Cookies.Append(Appearance.ModeCookie, Mode, options);
        return RedirectToPage(new { saved = true });
    }

    public IActionResult OnPostTheme()
    {
        ModelState.Remove(nameof(Mode));
        Mode = Appearance.IsMode(Request.Cookies[Appearance.ModeCookie]) ? Request.Cookies[Appearance.ModeCookie]! : "system";
        if (Theme != "association" && !Appearance.IsTheme(Theme))
            ModelState.AddModelError(nameof(Theme), "Choose a listed theme.");
        if (!ModelState.IsValid)
            return Page();
        if (Theme == "association")
            Response.Cookies.Delete(Appearance.ThemeCookie, CookieOptions());
        else
            Response.Cookies.Append(Appearance.ThemeCookie, Theme, CookieOptions());
        return ReturnToCaller();
    }

    public IActionResult OnPostMode()
    {
        ModelState.Remove(nameof(Theme));
        Theme = Appearance.IsTheme(Request.Cookies[Appearance.ThemeCookie]) ? Request.Cookies[Appearance.ThemeCookie]! : "association";
        if (!Appearance.IsMode(Mode))
            ModelState.AddModelError(nameof(Mode), "Choose a listed colour mode.");
        if (!ModelState.IsValid)
            return Page();
        Response.Cookies.Append(Appearance.ModeCookie, Mode, CookieOptions());
        return ReturnToCaller();
    }

    private IActionResult ReturnToCaller() => LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    private CookieOptions CookieOptions() => new() { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true };

}
