using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Membership;
using Nulysa.Operations;

namespace Nulysa.Tests;

[Trait("Group", "Protocol")]
public sealed class ConfigurationTests : DatabaseTest
{
    [Theory]
    [InlineData("FI21 1234 5600 0007 85", true)]
    [InlineData("DE89370400440532013000", true)]
    [InlineData("FI2112345600000786", false)]
    [InlineData("FI21not-an-account", false)]
    [InlineData("", false)]
    public async Task Billing_rejects_invalid_payment_destinations(string iban, bool valid)
    {

        Assert.Equal(valid, BillingOptions.ValidIban(iban));
        Settings["Billing:Iban"] = iban;
        await using var app = Build("commands");
        if (valid || iban.Length == 0)

            Assert.Equal(iban, app.Services.GetRequiredService<IOptions<BillingOptions>>().Value.Iban);
        else

            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<BillingOptions>>().Value);
    }

    [Fact]
    public async Task Billing_rejects_a_missing_template_directory_or_renderer()
    {
        Settings["Billing:TemplateDirectory"] = Path.Combine(DirectoryPath, "missing-invoice-templates");
        await using (var app = Build("commands"))
            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<BillingOptions>>().Value);
        Settings.Remove("Billing:TemplateDirectory");
        Settings["Billing:TypstPath"] = Path.Combine(DirectoryPath, "missing-typst");
        await using (var app = Build("commands"))
            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<BillingOptions>>().Value);
    }

    [Fact]
    public async Task Invoice_renderer_must_report_the_pinned_version()
    {
        var path = Path.Combine(DirectoryPath, "wrong-typst");
        await File.WriteAllTextAsync(path, "#!/bin/sh\necho 'typst 0.14.0'\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Throws<InvalidOperationException>(() => InvoicePdf.VerifyRenderer(path));
    }

    [Fact]
    public async Task Membership_and_mail_options_reject_invalid_retention_fallback_URL_and_missing_sender()
    {
        Settings["Membership:DefaultLanguage"] = "sv";
        await using (var app = Build("commands"))

            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<MembershipOptions>>().Value);
        Settings.Remove("Membership:DefaultLanguage");
        Settings["Membership:DeletionWaitingDays"] = "-1";
        await using (var app = Build("commands"))

            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<MembershipOptions>>().Value);
        Settings.Remove("Membership:DeletionWaitingDays");
        Settings["Mail:PublicUrl"] = "https://user:password@example.test/";
        await using (var app = Build("commands"))

            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<MailOptions>>().Value);
        Settings["Mail:PublicUrl"] = "https://registry.example.test/";
        Settings["Mail:Transport"] = "Smtp";
        Settings["Mail:Host"] = "smtp.example.test";
        await using (var app = Build("commands"))

            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<MailOptions>>().Value);
        Settings["Mail:FromEmail"] = "from@example.test";
        Settings["Mail:Username"] = "smtp-user";
        await using (var app = Build("commands"))

            Assert.Throws<OptionsValidationException>(() => app.Services.GetRequiredService<IOptions<MailOptions>>().Value);
        Settings.Remove("Mail:Username");
        await using (var app = Build("commands"))

            Assert.Equal("Smtp", app.Services.GetRequiredService<IOptions<MailOptions>>().Value.Transport);
    }
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Development_user_command_grants_both_admin_roles_and_prints_ID_without_relaxing_normal_validation()
    {
        async Task<(int Exit, string Output)> Create(string email, string password)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Nulysa/bin", configuration, "net10.0/Nulysa.dll")));
            info.ArgumentList.Add("create-development-user");
            info.Environment["DOTNET_ENVIRONMENT"] = "Development";
            foreach (var (name, value) in Settings)
                info.Environment[name.Replace(":", "__")] = value;
            info.Environment["DevelopmentUser__Email"] = email;
            info.Environment["DevelopmentUser__Password"] = password;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
            return (process.ExitCode, await stdout);
        }
        var created = await Create("weak@example.test", "x");

        Assert.Equal(0, created.Exit);
        var duplicate = await Create("weak@example.test", "x");

        Assert.Equal(1, duplicate.Exit); // Email uniqueness remains enforced.
        Assert.DoesNotContain("Created development user with ID", duplicate.Output);
        Assert.Equal(1, (await Create("empty@example.test", "")).Exit);
        Assert.Equal(1, (await Create("blank@example.test", "   ")).Exit);
        await using var scope = Maintenance.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync("weak@example.test"))!;

        Assert.True(await users.CheckPasswordAsync(user, "x"));
        Assert.NotEqual("x", user.PasswordHash);
        Assert.True(await users.IsInRoleAsync(user, "Superuser"));
        Assert.True(await users.IsInRoleAsync(user, "Administrator"));
        Assert.Contains($"Created development user with ID {user.Id}.", created.Output);
        var normal = await users.CreateAsync(new User { Email = "normal@example.test", UserName = "normal@example.test" }, "x");

        Assert.False(normal.Succeeded);
        Assert.Contains(normal.Errors, error => error.Code == "PasswordTooShort");
        Assert.Null(await users.FindByEmailAsync("empty@example.test"));
        Assert.Null(await users.FindByEmailAsync("blank@example.test"));
    }

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
            foreach (var (name, value) in settings)
                info.Environment[name.Replace(":", "__")] = value;
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
            foreach (var name in Schedules.Names)
                await Observations.Record(db, name, DateTimeOffset.UtcNow, CancellationToken.None);
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
