using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Components;
using Nulysa.Data;
using Nulysa.Integrations;
using Nulysa.Membership;
using Nulysa.Operations;
using Quartz;

namespace Nulysa;

public static class FoundationHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? customize = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(FoundationHost).Assembly.GetName().Name });
        customize?.Invoke(builder);
        if (builder.Environment.IsDevelopment())
            builder.WebHost.UseStaticWebAssets();
        var mode = args.FirstOrDefault() ?? "web";
        if (mode == "status")
            builder.Logging.ClearProviders();
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
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.User.Identity?.IsAuthenticated == true || context.Request.Path.StartsWithSegments("/Account") || context.Request.Path.StartsWithSegments("/apply"))
            {
                // Antiforgery warns unless both directives are present; keep this the canonical pair.
                context.Response.Headers.CacheControl = "no-cache, no-store";
                context.Response.Headers.Pragma = "no-cache";
            }
            await next(context);
        });
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapRazorPages();
        app.MapGet("/billing/invoices/{id:long}/pdf", async (long id, HttpContext context, InvoicePdf pdf) =>
        {
            try
            { return Results.File(await pdf.ForMember(context.User, id, context.RequestAborted), "application/pdf", $"invoice-{id}.pdf"); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).RequireAuthorization().RequireRateLimiting("invoice-pdf");
        app.MapGet("/admin/payments/export", async (DateOnly start, DateOnly end, HttpContext context, BillingQueries queries) =>
        {
            if (end <= start)
                return Results.BadRequest("Choose an end date after the start date.");
            return Results.File(System.Text.Encoding.UTF8.GetBytes(await queries.Export(context.User, start, end, context.RequestAborted)), "text/csv; charset=utf-8", "ledger.csv");
        }).RequireAuthorization("Administrator");
        app.MapGet("/admin/payments/receipts-export", async (DateOnly start, DateOnly end, HttpContext context, BillingQueries queries) =>
        {
            if (end <= start)
                return Results.BadRequest("Choose an end date after the start date.");
            return Results.File(System.Text.Encoding.UTF8.GetBytes(await queries.ExportReceipts(context.User, start, end, context.RequestAborted)), "text/csv; charset=utf-8", "bank-transactions.csv");
        }).RequireAuthorization("Administrator");
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        if (builder.Configuration.GetValue<bool>("Oidc:Enabled"))
            app.MapControllers();
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
        if (concurrency is < 1 or > 32)
            throw new InvalidOperationException("Worker:Concurrency must be between 1 and 32.");
        var idleWaitSeconds = config.GetValue("Worker:IdleWaitSeconds", development ? 1 : 5);
        if (idleWaitSeconds is < 1 or > 60)
            throw new InvalidOperationException("Worker:IdleWaitSeconds must be between 1 and 60.");
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            if (config["ReverseProxy:KnownProxy"] is { Length: > 0 } proxy)
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            else
                options.ForwardedHeaders = ForwardedHeaders.None;
        });
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddScoped<AssociationClock>();
        builder.Services.AddScoped<MembershipCommands>();
        builder.Services.AddScoped<MembershipQueries>();
        builder.Services.AddScoped<AccountCommands>();
        builder.Services.AddScoped<MembershipMaintenance>();
        builder.Services.AddScoped<BillingCommands>();
        builder.Services.AddScoped<BillingQueries>();
        builder.Services.AddScoped<InvoicePdf>();
        builder.Services.AddHttpClient<BankFeed>(client =>
        {
            client.BaseAddress = new Uri("https://bankaccountdata.gocardless.com/api/v2/");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddOptions<BankOptions>().Bind(config.GetSection("Bank"))
            .Validate(x => x.StaleHours is >= 24 and <= 168 && (!x.Enabled || (x.SecretId.Length > 0 && x.SecretKey.Length > 0 && x.Accounts.Length > 0 && x.Accounts.All(a => Guid.TryParse(a, out _)))),
                "Enabled bank import requires provider credentials and account IDs.").ValidateOnStart();
        builder.Services.AddOptions<BillingOptions>().Bind(config.GetSection("Billing"))
            .Validate(x => x.AllowedIntervals.Length > 0 && x.AllowedIntervals.All(i => i is >= 1 and <= 120) && x.AllowedIntervals.Contains(x.DefaultIntervalMonths) &&
                x.InvoiceLeadDays is >= 7 and <= 365 && x.InitialMembershipGraceDays is >= 1 and <= 365 &&
                x.MembershipRenewalGraceDays is >= 0 and <= 365 && x.ServicePaymentWindowDays is >= 1 and <= 365 &&
                (x.TemplateDirectory is null || Path.IsPathFullyQualified(x.TemplateDirectory)) &&
                BillingOptions.TemplatesExist(x.TemplateDirectory) && BillingOptions.TypstAvailable(x.TypstPath),
                "Invalid billing timing/template/Typst settings.")
            .Validate(x => (development && x.Iban.Length == 0) || BillingOptions.ValidIban(x.Iban), "Billing:Iban must be a checksum-valid payment destination.").ValidateOnStart();
        builder.Services.AddScoped<MailQueue>();
        builder.Services.AddScoped<MailRenderer>();
        builder.Services.AddScoped<MailDispatcher>();
        builder.Services.TryAddTransient<IMailTransport>(sp =>
        {
            var mail = sp.GetRequiredService<IOptions<MailOptions>>();
            var smtp = new SmtpMailTransport(mail, sp.GetRequiredService<IConfiguration>());
            return development
                ? new DevelopmentMailTransport(smtp, mail, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>())
                : smtp;
        });
        builder.Services.AddOptions<MembershipOptions>().Bind(config.GetSection("Membership"))
            .Validate(x => x.UnverifiedRetentionHours is >= 1 and <= 168 && x.DeletionWaitingDays is >= 1 and <= 365 &&
                x.RejectionRetentionDays is >= 1 and <= 30 && x.DefaultLanguage is "en" or "fi", "Invalid membership retention/language settings.").ValidateOnStart();
        builder.Services.AddOptions<MailOptions>().Bind(config.GetSection("Mail"))
            .Validate(x => Uri.TryCreate(x.PublicUrl, UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && x.PublicUrl.EndsWith('/') &&
                (uri.Scheme == "https" || (development && uri.Scheme == "http" && uri.IsLoopback)) &&
                x.MaxPerMinute is >= 1 and <= 1000 && (x.SpoolPath is null || Path.IsPathFullyQualified(x.SpoolPath)) &&
                x.Transport switch
                {
                    "Development" => development,
                    "Smtp" => !string.IsNullOrWhiteSpace(x.Host) && x.Port is >= 1 and <= 65535 &&
                        !string.IsNullOrWhiteSpace(x.FromEmail) && new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(x.FromEmail) &&
                        (string.IsNullOrEmpty(x.Username) == string.IsNullOrEmpty(x.Password)),
                    _ => false
                }, "Invalid mail URL/transport settings.").ValidateOnStart();
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
        if (!Path.IsPathFullyQualified(keys))
            throw new InvalidOperationException("DataProtection:Path must be absolute.");
        var protection = builder.Services.AddDataProtection().SetApplicationName("Nulysa").PersistKeysToFileSystem(new DirectoryInfo(keys));
        if (!development)
        {
            var certificate = config["DataProtection:CertificatePath"] ?? throw new InvalidOperationException("Production requires a Data Protection encryption certificate.");
            var wrappingKey = X509CertificateLoader.LoadPkcs12FromFile(certificate, config["DataProtection:CertificatePassword"]);
            if (!wrappingKey.HasPrivateKey)
                throw new InvalidOperationException("The Data Protection certificate needs its private key.");
            protection.ProtectKeysWithCertificate(wrappingKey);
        }
        if (mode != "worker")
        {
            builder.Services.AddAuthorizationBuilder().AddPolicy("Administrator", policy => policy.RequireAuthenticatedUser().AddRequirements(new AdministratorRequirement()))
                .AddPolicy("Superuser", policy => policy.RequireAuthenticatedUser().AddRequirements(new SuperuserRequirement()));
            builder.Services.AddScoped<IAuthorizationHandler, AdministratorAuthorizationHandler>();
            builder.Services.AddScoped<IAuthorizationHandler, SuperuserAuthorizationHandler>();
            builder.Services.AddScoped<SchedulerDiagnostics>();
            builder.Services.AddRazorPages();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddScoped<AuthenticationStateProvider, AccountAuthenticationStateProvider>();
            builder.Services.AddControllers();
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = 429;
                options.AddPolicy("verification", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 20, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
                options.AddPolicy("invoice-pdf", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
        }
        var oidc = builder.Services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppDbContext>());
        if (mode != "worker" && config.GetValue<bool>("Oidc:Enabled"))
            oidc.AddServer(options => Oidc.Configure(options, config, development));

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.AddQuartz(q =>
        {
            q.ConfigureScheduler(options =>
            {
                options.InstanceName = "Nulysa";
                options.GenerateInstanceId = true;
                options.IdleWaitTime = TimeSpan.FromSeconds(idleWaitSeconds);
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
                if (mode == "migrate")
                    store.ProvisionSchema();
            });
        });
        builder.Services.AddQuartzExecutionHistory(options =>
        {
            options.Retention = TimeSpan.FromDays(7);
            options.MaxEntriesPerScheduler = 2000;
            options.RecordInput = false;
        });
        if (mode is "web" or "worker")
            builder.Services.AddHostedService<ValidateSchema>();
        if (mode == "worker")
            builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
    }
}
