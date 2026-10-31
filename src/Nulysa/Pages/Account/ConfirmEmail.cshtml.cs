using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;

namespace Nulysa.Pages.Account;

[AllowAnonymous, EnableRateLimiting("verification")]
public sealed class ConfirmEmailModel(AccountCommands commands) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty(SupportsGet = true)] public Guid Version { get; set; }
    [BindProperty(SupportsGet = true), Required] public string Token { get; set; } = "";
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        try
        { await commands.ConfirmEmailChange(Id, Version, Token, HttpContext.RequestAborted); return RedirectToPage("Login"); }
        catch (ValidationException ex) { ModelState.AddModelError("", ex.Message); return Page(); }
        catch (DbUpdateException) { ModelState.AddModelError("", "That address is unavailable. Request a new change from your profile."); return Page(); }
    }
}
