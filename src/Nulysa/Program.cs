using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Operations;
using OpenIddict.Abstractions;
using Quartz;
using static OpenIddict.Abstractions.OpenIddictConstants;

var mode = args.FirstOrDefault() ?? "web";
if (mode == "worker")
{
    using var worker = FoundationHost.BuildWorker(args);
    await worker.RunAsync();
    return;
}
WebApplication app;
try { app = FoundationHost.Build(args); }
catch (Exception) when (mode == "status")
{
    Console.WriteLine("{\"healthy\":false,\"issues\":[\"configuration_invalid\"]}");
    Environment.ExitCode = 1;
    return;
}
if (mode == "web")
{
    await app.RunAsync();
    return;
}

await using var scope = app.Services.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
try
{
    if (mode == "migrate")
    {
        await db.Database.MigrateAsync();
        await using var upgrade = await db.Database.BeginTransactionAsync();
        await QuartzSchema.Upgrade(db);
        await upgrade.CommitAsync();
    }
    var scheduler = await scope.ServiceProvider.GetRequiredService<ISchedulerFactory>().GetScheduler();
    switch (mode)
    {
        case "migrate": break;
        case "install-schedules":
            await using (var tx = await db.Database.BeginTransactionAsync())
            using (scheduler.EnlistTransaction(tx.GetDbTransaction()))
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72134501)");
                await Schedules.Install(scheduler, app.Services.GetRequiredService<TimeProvider>(),
                    TimeZoneInfo.FindSystemTimeZoneById(app.Configuration["Association:TimeZone"] ?? "Europe/Helsinki"));
                await tx.CommitAsync();
            }
            break;
        case "status":
            var report = await WorkStatus.Read(db, scheduler, app.Services.GetRequiredService<TimeProvider>());
            Console.WriteLine(JsonSerializer.Serialize(report, JsonSerializerOptions.Web));
            Environment.ExitCode = report.Healthy ? 0 : 1;
            break;
        case "grant-superuser":
        case "revoke-superuser":
            if (args.Length != 2 || !long.TryParse(args[1], out var userId) || userId <= 0)
                throw new InvalidOperationException("Supply one existing numeric user ID.");
            await Superusers.Set(db, scope.ServiceProvider.GetRequiredService<UserManager<User>>(),
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>(), userId, mode == "grant-superuser");
            Console.WriteLine("Superuser assignment updated.");
            break;
        case "register-client":
            var clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var id = app.Configuration["Oidc:ClientId"] ?? throw new InvalidOperationException("Oidc:ClientId required.");
            if (await clients.FindByClientIdAsync(id) is not null) throw new InvalidOperationException("Client exists; use an explicit reviewed update.");
            await clients.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = id,
                ClientSecret = app.Configuration["Oidc:ClientSecret"] ?? throw new InvalidOperationException("Client secret required."),
                ClientType = ClientTypes.Confidential,
                ConsentType = ConsentTypes.Implicit,
                RedirectUris = { new Uri(app.Configuration["Oidc:RedirectUri"] ?? throw new InvalidOperationException("Redirect URI required.")) },
                Permissions = { Permissions.Endpoints.Authorization, Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code, Permissions.Scopes.Email, Permissions.Scopes.Profile },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            });
            break;
        case "create-development-user" when app.Environment.IsDevelopment():
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var email = app.Configuration["DevelopmentUser:Email"] ?? throw new InvalidOperationException("DevelopmentUser:Email required.");
                var password = app.Configuration["DevelopmentUser:Password"];
                if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("A non-empty development password is required.");
                // Only this Development-only command's scoped manager accepts weak passwords.
                users.PasswordValidators.Clear();
                await using var creation = await db.Database.BeginTransactionAsync();
                var user = new User { Email = email, UserName = email, EmailConfirmed = true, Admitted = true, Name = "Synthetic Member" };
                var result = await users.CreateAsync(user, password);
                if (!result.Succeeded) throw new InvalidOperationException("Development user creation failed.");
                await Superusers.Set(db, users, scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<long>>>(), user.Id, true);
                await creation.CommitAsync();
                break;
            }
        default: throw new InvalidOperationException("Use web, worker, migrate, install-schedules, status, grant-superuser <user-id>, revoke-superuser <user-id>, register-client or create-development-user (Development only).");
    }
    await scheduler.Shutdown();
}
catch (Exception) when (mode == "status")
{
    Console.WriteLine("{\"healthy\":false,\"issues\":[\"database_unavailable\"]}");
    Environment.ExitCode = 1;
}
catch (Exception) when (mode is "grant-superuser" or "revoke-superuser")
{
    Console.Error.WriteLine("Superuser assignment failed. Verify the numeric user ID and database access.");
    Environment.ExitCode = 1;
}
catch (Exception) when (mode == "create-development-user")
{
    Console.Error.WriteLine("Development user creation failed. Check the email, non-empty password and whether the account already exists.");
    Environment.ExitCode = 1;
}
await app.DisposeAsync();

public partial class Program;
