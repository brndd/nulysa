using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Operations;

namespace Nulysa.Tests;

[Trait("Group", "Protocol")]
public sealed class SystemConsoleTests : OidcTest
{
    [Fact]
    public async Task Account_creation_and_superuser_grant_share_the_outer_transaction()
    {
        await using var scope = Maintenance.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>();
        var user = new User { Email = Email, UserName = Email, EmailConfirmed = true, Admitted = true };
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await Superusers.Set(db, users, roles, user.Id, true);
            Assert.True(await users.IsInRoleAsync(user, "Superuser"));
            await using var other = Context();
            Assert.False(await other.Users.AnyAsync(x => x.Id == user.Id));
            Assert.False(await other.UserRoles.AnyAsync(x => x.UserId == user.Id));
            await tx.RollbackAsync();
        }
        await using var check = Context();
        Assert.False(await check.Users.AnyAsync(x => x.Id == user.Id));
        Assert.False(await check.UserRoles.AnyAsync(x => x.UserId == user.Id));
    }

    [Fact]
    public async Task Console_and_query_require_live_superuser_authority_not_board_role_or_stale_claims()
    {
        var user = await CreateUser();
        await using var app = await StartProvider();
        using var client = Client();
        var anonymous = await client.GetAsync("system-console");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/Account/Login", anonymous.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        Assert.DoesNotContain("System Console", await client.GetStringAsync("/"));
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("system-console")).Headers.Location!.AbsolutePath);
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var diagnostics = scope.ServiceProvider.GetRequiredService<SchedulerDiagnostics>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.Read(new ClaimsPrincipal()));
        Assert.True((await roles.CreateAsync(new IdentityRole<long>("Administrator"))).Succeeded);
        Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(user.Id.ToString()))!, "Administrator")).Succeeded);
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client)).StatusCode);
        Assert.DoesNotContain("System Console", await client.GetStringAsync("/"));
        var denied = await client.GetAsync("system-console");
        Assert.Equal("/Account/AccessDenied", denied.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(denied.Headers.Location)).StatusCode);
        // Even a stale/forged Superuser claim cannot satisfy the underlying query's live check.
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, "Superuser")], "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.Read(principal));
        await Superusers.Set(db, users, roles, user.Id, true);
        await Superusers.Set(db, users, roles, user.Id, true); // Idempotent provisioning.
        Assert.Contains("System Console", await client.GetStringAsync("/"));
        var consoleResponse = await client.GetAsync("system-console");
        Assert.True(consoleResponse.Headers.CacheControl?.NoStore);
        Assert.True(consoleResponse.Headers.CacheControl?.NoCache);
        Assert.Contains(consoleResponse.Headers.Pragma, value => value.Name == "no-cache");
        var missing = await consoleResponse.Content.ReadAsStringAsync();
        Assert.Contains("missing_schedule", missing);
        Assert.DoesNotContain("\"type\":\"server\"", missing);
        await Schedules.Install(Scheduler, Clock, TimeZoneInfo.Utc);
        foreach (var name in Schedules.Names) await Observations.Record(db, name, Clock.Now, CancellationToken.None);
        var healthy = await diagnostics.Read(principal);
        Assert.True(healthy.Work.Healthy);
        Assert.All(healthy.Schedules, x => Assert.NotNull(x.NextFire));
        db.FailureObservations.Add(new() { Job = "work.synthetic-failure", LastFailedAt = Clock.Now });
        await db.SaveChangesAsync();
        var html = await client.GetStringAsync("system-console");
        Assert.Contains("Attention required", html);
        Assert.Contains("work.synthetic-failure", html);
        Assert.Contains("authentication-cleanup", html);
        await (await app.Services.GetRequiredService<Quartz.ISchedulerFactory>().GetScheduler()).Shutdown();
        Assert.Contains("Scheduler diagnostics unavailable", await client.GetStringAsync("system-console"));
        await Superusers.Set(db, users, roles, user.Id, false);
        await Superusers.Set(db, users, roles, user.Id, false);
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("system-console")).Headers.Location!.AbsolutePath);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.Read(principal));
        Assert.True(await users.IsInRoleAsync((await users.FindByIdAsync(user.Id.ToString()))!, "Administrator"));
        await Superusers.Set(db, users, roles, user.Id, true);
        await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.PendingDeletionAt, Clock.Now));
        Assert.False((await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, "Superuser")).Succeeded);
        Assert.Equal("/Account/Login", (await client.GetAsync("system-console")).Headers.Location!.AbsolutePath);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Operator_commands_grant_revoke_and_fail_cleanly_for_invalid_users()
    {
        var user = await CreateUser();
        async Task<int> Run(string command, string id)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Nulysa/bin", configuration, "net10.0/Nulysa.dll")));
            info.ArgumentList.Add(command);
            info.ArgumentList.Add(id);
            info.Environment["DOTNET_ENVIRONMENT"] = "Development";
            foreach (var (name, value) in Settings) info.Environment[name.Replace(":", "__")] = value;
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
        Assert.Equal(0, await Run("grant-superuser", user.Id.ToString()));
        await using var db = Context();
        Assert.True(await db.UserRoles.AnyAsync(x => x.UserId == user.Id));
        Assert.Equal(0, await Run("revoke-superuser", user.Id.ToString()));
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == user.Id));
        Assert.Equal(1, await Run("grant-superuser", "invalid-id"));
        Assert.Equal(1, await Run("grant-superuser", long.MaxValue.ToString()));
    }
}
