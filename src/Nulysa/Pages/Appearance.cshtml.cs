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

        var options = new CookieOptions { HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromDays(365), IsEssential = true };
        if (Theme == "association")
            Response.Cookies.Delete(Appearance.ThemeCookie, options);
        else
            Response.Cookies.Append(Appearance.ThemeCookie, Theme, options);
        Response.Cookies.Append(Appearance.ModeCookie, Mode, options);
        return RedirectToPage(new { saved = true });
    }
}
