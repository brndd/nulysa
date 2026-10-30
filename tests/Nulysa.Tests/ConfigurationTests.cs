using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Nulysa.Operations;

namespace Nulysa.Tests;

[Trait("Group", "Protocol")]
public sealed class ConfigurationTests : DatabaseTest
{
    [Fact]
    public async Task Disabled_provider_has_no_protocol_endpoints_and_production_configuration_fails_closed()
    {
        Settings["Oidc:Enabled"] = "false";
        await using (var app = await StartWeb())
        {
            using var client = new HttpClient { BaseAddress = new Uri(Settings["Oidc:Issuer"]!) };
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync(".well-known/openid-configuration")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync("connect/token")).StatusCode);
        }
        Assert.Throws<InvalidOperationException>(() => Build("web", b => b.Environment.EnvironmentName = "Production"));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Synthetic", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(DirectoryPath, "protection.pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx, "synthetic"));
        Settings["DataProtection:CertificatePath"] = path;
        Settings["DataProtection:CertificatePassword"] = "synthetic";
        Settings["Oidc:Enabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => Build("web", b => b.Environment.EnvironmentName = "Production")); // HTTP issuer.
        Settings["Oidc:Issuer"] = "https://registry.example.test/";
        Settings["Oidc:SigningKeyPath"] = Path.Combine(DirectoryPath, "missing.pem");
        Assert.Throws<FileNotFoundException>(() => Build("web", b => b.Environment.EnvironmentName = "Production"));
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Status_command_is_machine_readable_with_nonzero_exit_on_failure_and_never_prints_secrets()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Nulysa/bin", configuration, "net10.0/Nulysa.dll"));
        async Task<(int Exit, JsonElement Json)> Status(Dictionary<string, string?> settings)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(executable);
            info.ArgumentList.Add("status");
            info.Environment["DOTNET_ENVIRONMENT"] = "Development";
            foreach (var (name, value) in settings) info.Environment[name.Replace(":", "__")] = value;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            var text = await stdout;
            Assert.DoesNotContain("private-never-output", text + await stderr);
            using var document = JsonDocument.Parse(text);
            return (process.ExitCode, document.RootElement.Clone());
        }
        var missing = await Status(Settings);
        Assert.Equal(1, missing.Exit);
        Assert.False(missing.Json.GetProperty("healthy").GetBoolean());
        await Schedules.Install(Scheduler, TimeProvider.System, TimeZoneInfo.Utc);
        await using (var db = Context())
        {
            foreach (var name in Schedules.Names) await Observations.Record(db, name, DateTimeOffset.UtcNow, CancellationToken.None);
        }
        var healthy = await Status(Settings);
        Assert.Equal(0, healthy.Exit);
        Assert.True(healthy.Json.GetProperty("healthy").GetBoolean());
        var unavailable = new Dictionary<string, string?>(Settings)
        {
            ["ConnectionStrings:Database"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=private-never-output;Timeout=1"
        };
        var failure = await Status(unavailable);
        Assert.Equal(1, failure.Exit);
        Assert.Equal("database_unavailable", failure.Json.GetProperty("issues")[0].GetString());
    }
}
