using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Nulysa.Accounts;

namespace Nulysa.Pages.Account;

[AllowAnonymous, EnableRateLimiting("verification")]
public sealed class SetPasswordModel(AccountCommands commands) : PageModel
{
    [BindProperty(SupportsGet = true)] public long Id { get; set; }
    [BindProperty(SupportsGet = true), Required] public string Token { get; set; } = "";
    [BindProperty, Required, StringLength(256, MinimumLength = 6), DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        try
        {
            await commands.SetPassword(Id, Token, Password, HttpContext.RequestAborted);
            return RedirectToPage("Login");
        }
        catch (ValidationException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return Page();
        }
    }
}
