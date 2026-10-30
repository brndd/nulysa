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
        if (context.User.Identity?.IsAuthenticated != true) return;
        // Role claims in an existing cookie/circuit may predate revocation.
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.GetUserAsync(context.User);
        if (user is { MayLogIn: true } && await users.IsInRoleAsync(user, "Superuser")) context.Succeed(requirement);
    }
}

public static class Superusers
{
    // Operator-only command; never exposed by member/board editing endpoints.
    public static async Task Set(AppDbContext db, UserManager<User> users, RoleManager<IdentityRole<long>> roles, long id, bool enabled)
    {
        // Join development account creation's transaction when present.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync() : null;
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72134502)");
        var user = await users.FindByIdAsync(id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ?? throw new InvalidOperationException("User not found.");
        if (enabled && !await roles.RoleExistsAsync("Superuser") &&
            !(await roles.CreateAsync(new IdentityRole<long>("Superuser"))).Succeeded)
            throw new InvalidOperationException("Superuser role creation failed.");
        if (await users.IsInRoleAsync(user, "Superuser") != enabled)
        {
            var result = enabled ? await users.AddToRoleAsync(user, "Superuser") : await users.RemoveFromRoleAsync(user, "Superuser");
            if (!result.Succeeded) throw new InvalidOperationException("Superuser assignment failed.");
        }
        if (tx is not null) await tx.CommitAsync();
    }
}
