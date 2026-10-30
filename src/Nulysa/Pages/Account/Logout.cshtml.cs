using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nulysa.Accounts;

namespace Nulysa.Pages.Account;

[Authorize]
public sealed class LogoutModel(SignInManager<User> signIn) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return RedirectToPage("/Account/Login");
    }
}
