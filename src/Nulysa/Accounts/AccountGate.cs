using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;

namespace Nulysa.Accounts;

public static class AccountGate
{
    public static async Task<bool> Allows(AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(id, out var key) &&
            await db.Users.AsNoTracking().AnyAsync(x => x.Id == key && x.Admitted && x.PendingDeletionAt == null, ct);
    }

    public static async Task ValidateCookie(CookieValidatePrincipalContext context)
    {
        // Identity's periodic stamp check does not block a newly disabled account immediately.
        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        if (!await Allows(db, context.Principal!, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
        else
        {
            await context.HttpContext.RequestServices.GetRequiredService<ISecurityStampValidator>().ValidateAsync(context);
        }
    }
}
