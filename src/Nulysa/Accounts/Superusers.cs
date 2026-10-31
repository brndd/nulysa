using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nulysa.Data;

namespace Nulysa.Accounts;

public sealed class SuperuserRequirement : IAuthorizationRequirement;

public sealed class SuperuserAuthorizationHandler(IServiceScopeFactory scopes) : AuthorizationHandler<SuperuserRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SuperuserRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;
        // Role claims in an existing cookie/circuit may predate revocation.
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.GetUserAsync(context.User);
        if (user is { MayLogIn: true } && await users.IsInRoleAsync(user, "Superuser"))
            context.Succeed(requirement);
    }
}

public static class Superusers
{
    // Operator-only command; never exposed by member/board editing endpoints.
    public static async Task Set(AppDbContext db, UserManager<User> users, RoleManager<IdentityRole<long>> roles, long id, bool enabled, string role = "Superuser")
    {
        if (role is not ("Superuser" or "Administrator"))
            throw new ArgumentOutOfRangeException(nameof(role));
        // Join development account creation's transaction when present.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72134502)");
        var user = await users.FindByIdAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("User not found.");
        if (enabled && !await roles.RoleExistsAsync(role) &&
            !(await roles.CreateAsync(new IdentityRole<long>(role))).Succeeded)
            throw new InvalidOperationException("Operator role creation failed.");
        if (await users.IsInRoleAsync(user, role) != enabled)
        {
            var result = enabled ? await users.AddToRoleAsync(user, role) : await users.RemoveFromRoleAsync(user, role);
            if (!result.Succeeded)
                throw new InvalidOperationException("Operator role assignment failed.");
        }
        if (tx is not null)
            await tx.CommitAsync();
    }
}
