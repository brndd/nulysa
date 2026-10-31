using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Billing;
using Nulysa.Communications;
using Nulysa.Membership;

namespace Nulysa.Tests;

[Trait("Group", "Membership")]
public sealed class MembershipFormTests : MembershipTest
{
    [Fact]
    public async Task Retired_subscriptions_keep_member_stop_and_administrator_removal_controls()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var view = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        var service = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, null,
            new("door", "Retired door", true, 0, 7, 10, "door")));
        await using var db = Context();
        var fee = await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service);
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().SetPrice(board, fee.Id, new(2000, 1, 1), 30));
        var enrollment = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Subscribe(Actor(id), view.AccountId, service, 1, false, Guid.NewGuid()));
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "association", "paid", 40, "EUR",
            DateOnly.FromDateTime(Clock.Now.DateTime), view.Invoices[0].Reference)));
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, service,
            new("door", "Retired door", true, 0, 7, 10, "door", Retired: true)));
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        await using var app = await StartProvider();
        using var member = Client();
        await Login(member);

        var html = await member.GetStringAsync("billing");

        Assert.Contains("<h3>Retired door</h3>", html);
        Assert.Contains("Stop renewal; keep remaining covered time", html);
        Assert.DoesNotContain($"subscribe-{service}", html);
        Assert.DoesNotContain("Remove immediately", html);
        using var administrator = Client();
        await Login(administrator, "board@example.test");
        var adminHtml = await administrator.GetStringAsync($"admin/members/{id}/billing");

        Assert.Contains("Remove immediately", adminHtml);
        var antiforgery = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>");
        var response = await member.PostAsync("billing", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = $"stop-{service}",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(antiforgery.Groups[1].Value),
            ["Action.Key"] = Guid.NewGuid().ToString(),
            ["Action.Id"] = enrollment!.Value.ToString(),
            ["Action.Name"] = "stop"
        }));
        var stopped = await db.ServiceEnrollments.AsNoTracking().SingleAsync(x => x.Id == enrollment);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(stopped.FirstPaidOn);
        Assert.NotNull(stopped.RenewalStoppedAt);
        Assert.Null(stopped.EndedAt);
        Assert.Contains((await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!.Coverage,
            x => x.ServiceDefinitionId == service && x.Source == CoverageSource.Purchase);
    }

    [Fact]
    public async Task Public_and_member_mutations_require_antiforgery_and_private_admin_routes_are_not_a_directory()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        await using var app = await StartProvider();
        using var client = Client();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("apply", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("apply/verify?id=" + Guid.NewGuid(), new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("Account/ForgotPassword", new FormUrlEncodedContent([]))).StatusCode);
        await Login(client);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("profile", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("resign", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("admin/members")).Headers.Location!.AbsolutePath);
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("admin/applications")).Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("admin/members/" + Nulysa.Accounts.Authority.Id(board))).StatusCode);
        var html = await client.GetStringAsync("profile");

        Assert.Contains("Input.DateOfBirth", html);
        Assert.DoesNotContain("PasswordHash", html);
        Assert.True((await client.GetAsync("profile")).Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("billing", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Contains("Issued payment requests", await client.GetStringAsync("billing"));
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("admin/payments")).Headers.Location!.AbsolutePath);
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("admin/billing-configuration")).Headers.Location!.AbsolutePath);
    }
}

[Trait("Group", "Browser")]
public sealed class MembershipBrowserTests : MembershipTest
{
    [Fact]
    public async Task Static_forms_cover_application_review_password_profile_email_reset_resignation_and_admin_restoration()
    {
        var board = await Board();
        Settings["Mail:PublicUrl"] = Settings["Oidc:Issuer"];
        Settings["Association:Name"] = "Synthetic Membership Association";
        var transport = new RecordingMail();
        await using var commands = CommandsApp(transport);
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var memberContext = await browser.NewContextAsync();
        await using var boardContext = await browser.NewContextAsync();
        var memberPage = await memberContext.NewPageAsync();
        var boardPage = await boardContext.NewPageAsync();
        var root = Settings["Oidc:Issuer"]!;
        await memberPage.GotoAsync(root + "apply");
        await memberPage.GetByLabel("Name", new() { Exact = true }).FillAsync("Browser Member");
        await memberPage.GetByLabel("Municipality").FillAsync("Helsinki");
        await memberPage.GetByLabel("Date of birth").FillAsync("2010-06-12");
        await memberPage.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await memberPage.GetByLabel("Message to administrators").FillAsync("Synthetic browser application");
        await memberPage.GetByLabel("I accept the association's").CheckAsync();
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Submit application" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("verification email has been queued");
        await using var db = Context();
        var application = await db.Applications.AsNoTracking().SingleAsync();
        var verification = await db.Deliveries.Where(x => x.ApplicationId == application.Id).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(verification));
        var verifyUrl = Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value;
        await memberPage.GotoAsync(verifyUrl);

        Assert.Null((await db.Applications.AsNoTracking().SingleAsync()).VerifiedAt); // Mail-scanner GET is not confirmation.
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Verify my email address" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("awaiting administrator review");

        async Task LoginPage(IPage page, string email)
        {
            await page.GotoAsync(root + "Account/Login");
            await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        }
        await LoginPage(boardPage, "board@example.test");
        await boardPage.GotoAsync(root + "admin/applications");
        await Assertions.Expect(boardPage.GetByText("Under 18 — check consent outside the portal")).ToBeVisibleAsync();
        await boardPage.GetByRole(AriaRole.Link, new() { Name = "Browser Member — " + Email }).ClickAsync();
        await boardPage.GetByRole(AriaRole.Button, new() { Name = "Approve membership", Exact = true }).ClickAsync();
        await Assertions.Expect(boardPage.GetByRole(AriaRole.Heading, new() { Name = "Member details", Exact = true })).ToBeVisibleAsync();
        var id = (await db.Members.SingleAsync()).UserId;
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(application.Id));
        await memberPage.GotoAsync(Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value);
        await memberPage.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByLabel("Confirm password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Set password", Exact = true }).ClickAsync();
        await LoginPage(memberPage, Email);
        await memberPage.GetByRole(AriaRole.Link, new() { Name = "My details and email" }).ClickAsync();
        await Assertions.Expect(memberPage).ToHaveTitleAsync("Member details — Synthetic Membership Association");
        await memberPage.GetByLabel("Name", new() { Exact = true }).FillAsync("Changed Browser Name");
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Save details" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Changed Browser Name");
        await memberPage.GetByLabel("New email", new() { Exact = true }).FillAsync("browser-changed@example.test");
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Send verification to new address" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByText("Awaiting verification: browser-changed@example.test")).ToBeVisibleAsync();
        var change = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.EmailChange).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(change));
        await memberPage.GotoAsync(Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value);
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Confirm new email", Exact = true }).ClickAsync();
        await LoginPage(memberPage, "browser-changed@example.test");
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Heading, new() { Name = "Member portal", Exact = true })).ToBeVisibleAsync();

        Clock.Now = Clock.Now.AddMinutes(2);
        await memberPage.GotoAsync(root + "Account/ForgotPassword");
        await memberPage.GetByLabel("Email", new() { Exact = true }).FillAsync("browser-changed@example.test");
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Send reset link" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("reset email has been queued");
        var reset = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.PasswordReset).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(reset));
        await memberPage.GotoAsync(Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value);
        await memberPage.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByLabel("Confirm password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Set password", Exact = true }).ClickAsync();
        await LoginPage(memberPage, "browser-changed@example.test");
        await memberPage.GetByRole(AriaRole.Link, new() { Name = "Resign", Exact = true }).ClickAsync();
        await memberPage.GetByLabel("I understand and want to resign now.").CheckAsync();
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Resign and begin account deletion" }).ClickAsync();
        await Assertions.Expect(memberPage).ToHaveURLAsync(root + "Account/Login");
        var warning = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.DeletionWarning).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(warning));

        Assert.Contains("14 days", transport.Sent[^1].Text);
        await memberPage.GotoAsync(root);
        await Assertions.Expect(memberPage).ToHaveURLAsync(new Regex("/Account/Login"));
        await boardPage.GotoAsync(root + "admin/members/" + id);
        await boardPage.GetByLabel("Reason / verified restoration request").FillAsync("Verified request");
        await boardPage.GetByLabel("I confirm this membership action and its consequences.").CheckAsync();
        await boardPage.GetByRole(AriaRole.Button, new() { Name = "Cancel pending deletion" }).ClickAsync();
        await Assertions.Expect(boardPage.GetByRole(AriaRole.Heading, new() { Name = "Member registry", Exact = true })).ToBeVisibleAsync();

        Assert.Null((await db.Users.AsNoTracking().SingleAsync(x => x.Id == id)).PendingDeletionAt);
        Assert.Equal(5, transport.Sent.Count); // Verification, welcome, email change, reset, warning.
    }
}
