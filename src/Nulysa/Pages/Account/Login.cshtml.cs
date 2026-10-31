using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nulysa.Accounts;

namespace Nulysa.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(UserManager<User> users, SignInManager<User> signIn) : PageModel
{
    [BindProperty, Required, EmailAddress] public string Email { get; set; } = "";
    [BindProperty, Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();
        var user = await users.FindByEmailAsync(Email);
        if (user is not null && user.MayLogIn &&
            (await signIn.PasswordSignInAsync(user, Password, isPersistent: false, lockoutOnFailure: false)).Succeeded)
            return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");

        ModelState.AddModelError("", "Unable to log in with these credentials.");
        return Page();
    }
}
