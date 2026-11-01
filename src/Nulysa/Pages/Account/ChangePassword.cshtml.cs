using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nulysa.Accounts;
using Nulysa.Components;

namespace Nulysa.Pages.Account;

[Authorize]
public sealed class ChangePasswordModel(AccountCommands commands, SignInManager<User> signIn, UiFeedback feedback) : PageModel
{
    [BindProperty, Required, StringLength(256)] public string CurrentPassword { get; set; } = "";
    [BindProperty, Required, StringLength(256)] public string NewPassword { get; set; } = "";
    [BindProperty, Required, Compare(nameof(NewPassword), ErrorMessage = "The new passwords do not match.")]
    public string ConfirmPassword { get; set; } = "";
    [BindProperty] public Guid Operation { get; set; }

    public IActionResult OnGet() => LocalRedirect("/profile#account");

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            feedback.SaveError(HttpContext, string.Join(" ", ModelState.Values.SelectMany(value => value.Errors).Select(error => error.ErrorMessage)));
            return LocalRedirect("/profile#account");
        }

        try
        {
            var user = await commands.ChangePassword(User, CurrentPassword, NewPassword, Operation, HttpContext.RequestAborted);
            await signIn.RefreshSignInAsync(user);
            feedback.Save(HttpContext, "Password changed. A notification email has been queued.");
        }
        catch (ValidationException ex)
        {
            feedback.SaveError(HttpContext, ex.Message);
        }
        return LocalRedirect("/profile#account");
    }
}
