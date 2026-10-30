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
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var email = app.Configuration["DevelopmentUser:Email"] ?? throw new InvalidOperationException("DevelopmentUser:Email required.");
            var result = await users.CreateAsync(new User { Email = email, UserName = email, EmailConfirmed = true, Admitted = true, Name = "Synthetic Member" },
                app.Configuration["DevelopmentUser:Password"] ?? throw new InvalidOperationException("DevelopmentUser:Password required."));
            if (!result.Succeeded) throw new InvalidOperationException("Development user creation failed.");
            break;
        default: throw new InvalidOperationException("Use web, worker, migrate, install-schedules, status, register-client or create-development-user (Development only).");
    }
    await scheduler.Shutdown();
}
catch (Exception) when (mode == "status")
{
    Console.WriteLine("{\"healthy\":false,\"issues\":[\"database_unavailable\"]}");
    Environment.ExitCode = 1;
}
await app.DisposeAsync();

public partial class Program;
