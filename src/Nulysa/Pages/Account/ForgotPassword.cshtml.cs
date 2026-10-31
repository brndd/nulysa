using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Nulysa.Accounts;

namespace Nulysa.Pages.Account;

[AllowAnonymous, EnableRateLimiting("verification")]
public sealed class ForgotPasswordModel(AccountCommands commands) : PageModel
{
    [BindProperty, Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    public bool Submitted { get; private set; }
    public async Task OnPostAsync()
    {
        if (!ModelState.IsValid)
            return;
        await commands.RequestPasswordReset(Email, HttpContext.RequestAborted);
        Submitted = true;
    }
}
