using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nulysa.Data;
using Quartz;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Nulysa.Tests;

public abstract class DatabaseTest : IAsyncLifetime
{
    private readonly string database = "nulysa_s0_" + Guid.NewGuid().ToString("N");
    private string admin = "";
    protected string DirectoryPath { get; } = Path.Combine("/tmp/opencode", "nulysa-s0-" + Guid.NewGuid().ToString("N"));
    protected Dictionary<string, string?> Settings { get; } = [];
    protected WebApplication Maintenance { get; private set; } = null!;
    protected IScheduler Scheduler { get; private set; } = null!;
    protected List<WorkerProcess> Workers { get; } = [];

    public async Task InitializeAsync()
    {
        admin = Environment.GetEnvironmentVariable("NULYSA_TEST_POSTGRES") ??
            "Host=127.0.0.1;Port=55432;Database=postgres;Username=nulysa;Password=synthetic-local-password";
        var parsed = new NpgsqlConnectionStringBuilder(admin);
        if (parsed.Host is not ("127.0.0.1" or "localhost") || parsed.Database is not ("postgres" or "nulysa_test"))
            throw new InvalidOperationException("Tests require isolated loopback PostgreSQL, with admin database postgres or nulysa_test.");
        await using (var connection = new NpgsqlConnection(admin))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            await command.ExecuteNonQueryAsync();
        }
        parsed.Database = database;
        Directory.CreateDirectory(DirectoryPath);
        foreach (var name in new[] { "signing", "encryption" })
        {
            using var rsa = RSA.Create(2048);
            var path = Path.Combine(DirectoryPath, name + ".pem");
            await File.WriteAllTextAsync(path, rsa.ExportRSAPrivateKeyPem());
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Settings["ConnectionStrings:Database"] = parsed.ConnectionString;
        Settings["DataProtection:Path"] = Path.Combine(DirectoryPath, "data-protection");
        Settings["Billing:Iban"] = "FI2112345600000785";
        Settings["Oidc:Enabled"] = "true";
        Settings["Oidc:Issuer"] = "http://127.0.0.1:" + FreePort() + "/";
        Settings["Oidc:SigningKeyPath"] = Path.Combine(DirectoryPath, "signing.pem");
        Settings["Oidc:EncryptionKeyPath"] = Path.Combine(DirectoryPath, "encryption.pem");
        Settings["Worker:CheckinSeconds"] = "1";
        Settings["Worker:FailureDetectionSeconds"] = "2";
        Maintenance = Build("migrate");
        await using var scope = Maintenance.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        db.FeePrices.Add(new Nulysa.Billing.FeePrice { FeeDefinitionId = 1, EffectiveOn = new DateOnly(2000, 1, 1), MonthlyAmount = 10m });
        await db.SaveChangesAsync();
        Scheduler = await Maintenance.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE probe_operations (id text PRIMARY KEY, input integer NOT NULL,
              completed boolean NOT NULL DEFAULT false, released boolean NOT NULL DEFAULT false, attempts integer NOT NULL DEFAULT 0);
            CREATE TABLE probe_effects (id text PRIMARY KEY REFERENCES probe_operations(id));
            """);
    }

    protected WebApplication Build(string mode, Action<WebApplicationBuilder>? configure = null) => FoundationHost.Build([mode], builder =>
    {
        builder.Environment.EnvironmentName = "Development";
        builder.Configuration.AddInMemoryCollection(Settings);
        builder.Logging.ClearProviders();
        configure?.Invoke(builder);
    });

    protected async Task<WebApplication> StartWeb()
    {
        var app = Build("web");
        app.Urls.Add(Settings["Oidc:Issuer"]!);
        await app.StartAsync();
        return app;
    }

    protected AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(Settings["ConnectionStrings:Database"]).UseOpenIddict().Options);

    protected WorkerProcess StartWorker()
    {
        var worker = new WorkerProcess(Settings);
        Workers.Add(worker);
        return worker;
    }

    protected async Task<int> Attempts(string key)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<int>($"SELECT attempts AS \"Value\" FROM probe_operations WHERE id = {key}").SingleAsync();
    }

    protected async Task<int> Effects(string key)
    {
        await using var db = Context();
        return await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM probe_effects WHERE id = {key}").SingleAsync();
    }

    protected async Task Release(string key)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE probe_operations SET released = true WHERE id = {key}");
    }

    public static async Task Until(Func<Task<bool>> condition, int seconds = 30)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition())
            await Task.Delay(100, deadline.Token);
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task DisposeAsync()
    {
        foreach (var worker in Workers)
            await worker.DisposeAsync();
        if (Scheduler is not null)
            await Scheduler.Shutdown();
        if (Maintenance is not null)
            await Maintenance.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stdout;
    private readonly Task<string> stderr;
    public bool HasExited => process.HasExited;
    public WorkerProcess(Dictionary<string, string?> settings)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Nulysa.WorkerHarness"));
        var info = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true };
        info.ArgumentList.Add(Path.Combine(project, "bin", configuration, "net10.0", "Nulysa.WorkerHarness.dll"));
        info.Environment["DOTNET_ENVIRONMENT"] = "Development";
        foreach (var (key, value) in settings)
            info.Environment[key.Replace(":", "__")] = value;
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        process = Process.Start(info)!;
        stdout = process.StandardOutput.ReadToEndAsync();
        stderr = process.StandardError.ReadToEndAsync();
    }

    public async Task Signal(string signal)
    {
        using var command = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-" + signal, process.Id.ToString() } })!;
        await command.WaitForExitAsync();

        Assert.Equal(0, command.ExitCode);
    }

    public async Task Stop()
    {
        if (HasExited)
            return;
        await Signal("TERM");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await process.WaitForExitAsync(deadline.Token);

        Assert.Equal(0, process.ExitCode);
    }

    public async Task<string> Output() => (await stdout) + (await stderr);

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        await Task.WhenAll(stdout, stderr);
        process.Dispose();
    }
}
