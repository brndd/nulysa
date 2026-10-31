using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;

namespace Nulysa.Accounts;

public static class Authority
{
    public static long Id(ClaimsPrincipal actor) => long.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        && actor.Identity?.IsAuthenticated == true ? id : throw new UnauthorizedAccessException();

    public static async Task<bool> IsAdministrator(AppDbContext db, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        if (!await AccountGate.Allows(db, actor, ct)) return false;
        var id = Id(actor);
        return await (from assignment in db.UserRoles
                      join role in db.Roles on assignment.RoleId equals role.Id
                      where assignment.UserId == id && role.NormalizedName == "ADMINISTRATOR"
                      select assignment).AnyAsync(ct);
    }

    public static async Task RequireAdministrator(AppDbContext db, ClaimsPrincipal actor, CancellationToken ct)
    {
        if (!await IsAdministrator(db, actor, ct)) throw new UnauthorizedAccessException();
    }

    public static async Task RequireOwnerOrAdministrator(AppDbContext db, ClaimsPrincipal actor, long userId, CancellationToken ct)
    {
        if (!await AccountGate.Allows(db, actor, ct) || (Id(actor) != userId && !await IsAdministrator(db, actor, ct)))
            throw new UnauthorizedAccessException();
    }
}

public sealed class AdministratorRequirement : IAuthorizationRequirement;

public sealed class AdministratorAuthorizationHandler(IServiceScopeFactory scopes) : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorRequirement requirement)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (await Authority.IsAdministrator(scope.ServiceProvider.GetRequiredService<AppDbContext>(), context.User)) context.Succeed(requirement);
    }
}
