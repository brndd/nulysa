using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nulysa.Accounts;
using Nulysa.Components;
using Nulysa.Data;
using Nulysa.Operations;
using Quartz;

namespace Nulysa;

public static class FoundationHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? customize = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(FoundationHost).Assembly.GetName().Name });
        customize?.Invoke(builder);
        if (builder.Environment.IsDevelopment()) builder.WebHost.UseStaticWebAssets();
        var mode = args.FirstOrDefault() ?? "web";
        if (mode == "status") builder.Logging.ClearProviders();
        ConfigureServices(builder, mode);
        var development = builder.Environment.IsDevelopment();
        var app = builder.Build();
        app.UseForwardedHeaders();
        if (!development)
        {
            app.UseExceptionHandler(error => error.Run(context =>
            {
                context.Response.StatusCode = 500;
                return context.Response.WriteAsync("The request could not be completed.");
            }));
            app.UseHsts();
            app.UseHttpsRedirection();
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorPages();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        if (builder.Configuration.GetValue<bool>("Oidc:Enabled")) app.MapControllers();
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        return app;
    }

    public static IHost BuildWorker(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureServices(builder, "worker");
        return builder.Build();
    }

    private static void ConfigureServices(IHostApplicationBuilder builder, string mode)
    {
        var config = builder.Configuration;
        var development = builder.Environment.IsDevelopment();
        _ = TimeZoneInfo.FindSystemTimeZoneById(config["Association:TimeZone"] ?? "Europe/Helsinki");
        var concurrency = config.GetValue("Worker:Concurrency", 4);
        if (concurrency is < 1 or > 32) throw new InvalidOperationException("Worker:Concurrency must be between 1 and 32.");
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            if (config["ReverseProxy:KnownProxy"] is { Length: > 0 } proxy) options.KnownProxies.Add(IPAddress.Parse(proxy));
            else options.ForwardedHeaders = ForwardedHeaders.None;
        });
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        var connection = config.GetConnectionString("Database") ?? throw new InvalidOperationException("ConnectionStrings:Database is required.");
        builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connection, pg => pg.CommandTimeout(15)).UseOpenIddict());
        builder.Services.AddIdentity<User, IdentityRole<long>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
        }).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = "/Account/Login";
            options.Cookie.Name = "Nulysa.Account";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Events.OnValidatePrincipal = AccountGate.ValidateCookie;
        });
        var keys = config["DataProtection:Path"] ?? throw new InvalidOperationException("DataProtection:Path is required.");
        if (!Path.IsPathFullyQualified(keys)) throw new InvalidOperationException("DataProtection:Path must be absolute.");
        var protection = builder.Services.AddDataProtection().SetApplicationName("Nulysa").PersistKeysToFileSystem(new DirectoryInfo(keys));
        if (!development)
        {
            var certificate = config["DataProtection:CertificatePath"] ?? throw new InvalidOperationException("Production requires a Data Protection encryption certificate.");
            var wrappingKey = X509CertificateLoader.LoadPkcs12FromFile(certificate, config["DataProtection:CertificatePassword"]);
            if (!wrappingKey.HasPrivateKey) throw new InvalidOperationException("The Data Protection certificate needs its private key.");
            protection.ProtectKeysWithCertificate(wrappingKey);
        }
        if (mode != "worker")
        {
            builder.Services.AddAuthorizationBuilder().AddPolicy("Administrator", policy => policy.RequireRole("Administrator"))
                .AddPolicy("Superuser", policy => policy.RequireAuthenticatedUser().AddRequirements(new SuperuserRequirement()));
            builder.Services.AddScoped<IAuthorizationHandler, SuperuserAuthorizationHandler>();
            builder.Services.AddScoped<SchedulerDiagnostics>();
            builder.Services.AddRazorPages();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddScoped<AuthenticationStateProvider, AccountAuthenticationStateProvider>();
            builder.Services.AddControllers();
        }
        var oidc = builder.Services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppDbContext>());
        if (mode != "worker" && config.GetValue<bool>("Oidc:Enabled")) oidc.AddServer(options => Oidc.Configure(options, config, development));

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.AddQuartz(q =>
        {
            q.ConfigureScheduler(options =>
            {
                options.InstanceName = "Nulysa";
                options.GenerateInstanceId = true;
                options.ShutdownJobInterruption = ShutdownJobInterruption.WhenWaitingForJobs;
            });
            q.UseDefaultThreadPool(options => options.MaxConcurrency = concurrency);
            q.UseDefaultRetryPolicy(RetryPolicy.Fixed(2, TimeSpan.FromSeconds(2)));
            q.AddJobMiddleware<RedactJobFailures>();
            q.AddJobListener<FailureRecorder>(Matchers.AllJobs());
            q.UsePersistentStore(store =>
            {
                store.UsePostgres(connection);
                store.UseClustering(options =>
                {
                    options.CheckinInterval = TimeSpan.FromSeconds(config.GetValue("Worker:CheckinSeconds", 5));
                    options.CheckinMisfireThreshold = TimeSpan.FromSeconds(config.GetValue("Worker:FailureDetectionSeconds", 15));
                });
                store.ConfigureStore(options =>
                {
                    options.AcceptEnlistedTransactions = true;
                    options.StoreJobDataAsStrings = true;
                    options.RecoverFiringsCancelledByShutdown = true;
                    options.CommandTimeout = TimeSpan.FromSeconds(15);
                    options.MisfireThreshold = TimeSpan.FromSeconds(5);
                });
                store.UseSystemTextJsonSerializer();
                store.UseExecutionHistory();
                if (mode == "migrate") store.ProvisionSchema();
            });
        });
        builder.Services.AddQuartzExecutionHistory(options =>
        {
            options.Retention = TimeSpan.FromDays(7);
            options.MaxEntriesPerScheduler = 2000;
            options.RecordInput = false;
        });
        if (mode is "web" or "worker") builder.Services.AddHostedService<ValidateSchema>();
        if (mode == "worker") builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    }
}
