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
    public async Task Billing_history_labels_automatic_operations_as_System_and_preserves_actor_ids()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var view = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().SetEmailInvoices(board, view.AccountId, false, Guid.NewGuid()));
        await using var app = await StartProvider();
        using var administrator = Client();
        await Login(administrator, "board@example.test");

        var html = WebUtility.HtmlDecode(await administrator.GetStringAsync($"admin/members/{id}/billing"));

        Assert.Contains("admission · System · Initial membership request", html);
        Assert.Contains($"actor #{Nulysa.Accounts.Authority.Id(board)}", html);
        Assert.DoesNotContain("actor # ·", html);
    }

    [Fact]
    public async Task Statement_paging_hints_render_conditionally_instead_of_exposing_Razor_source()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        var view = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await using var app = await StartProvider();
        using var client = Client();
        await Login(client);
        var empty = await client.GetStringAsync("billing");
        Assert.Contains("Closing balance: 0.00 EUR", empty);
        Assert.DoesNotContain("@if", empty);
        Assert.DoesNotContain("showing a page", empty);

        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "association", "statement-display", 100, "EUR",
            DateOnly.FromDateTime(Clock.Now.DateTime), view.Invoices[0].Reference)));
        var paged = await client.GetStringAsync("billing?balance=allocated&pageSize=1");
        Assert.DoesNotContain("@if", paged);
        Assert.Contains("showing a page", paged);
        Assert.Contains("Balance at end of this page", paged);
        Assert.DoesNotContain(">Current balances</h2>", paged);

        using var administrator = Client();
        await Login(administrator, "board@example.test");
        var admin = await administrator.GetStringAsync($"admin/members/{id}/billing");
        Assert.Contains("Closing balance:", admin);
        Assert.DoesNotContain("@if", admin);
        Assert.DoesNotContain("showing a page", admin);
    }

    [Fact]
    public async Task Member_tasks_are_separate_and_bill_routes_do_not_expose_another_members_details()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var other = await Admit(commands, board, "other@example.test");
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<Nulysa.Accounts.AccountCommands>().SetPassword(id, token, Password));
        var otherView = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, other)))!;
        await using var app = await StartProvider();
        using var client = Client();
        await Login(client);

        var services = await client.GetStringAsync("services");
        Assert.Contains("<h1>Account overview</h1>", services);
        Assert.DoesNotContain("<h2>Issued bills</h2>", services);
        var bills = await client.GetStringAsync("billing");
        Assert.Contains("<h1>Billing details</h1>", bills);
        Assert.DoesNotContain("<h2>Billing intervals</h2>", bills);
        Assert.Contains(">Current balances</h2>", services);
        Assert.DoesNotContain(">Current balances</h2>", bills);
        Assert.DoesNotContain("<h3>Allocated to unfinished bills</h3>", bills);
        Assert.Contains("class=\"balance-note\"", services);
        Assert.DoesNotContain("Available purchase balance", bills);
        Assert.Contains(">Transaction history</h2>", bills);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("balances")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("billing/history")).StatusCode);
        var forbidden = await client.GetAsync($"billing/invoices/{otherView.Invoices[0].Invoice.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain(otherView.Invoices[0].Reference, await forbidden.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"billing/invoices/{long.MaxValue}")).StatusCode);
    }

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

        var html = await member.GetStringAsync("services");

        Assert.Contains("<h3>Retired door</h3>", html);
        Assert.Contains("Service Retired door", html);
        Assert.Contains("Stop renewal", html);
        Assert.DoesNotContain($"subscribe-{service}", html);
        Assert.DoesNotContain("Remove immediately", html);
        using var administrator = Client();
        await Login(administrator, "board@example.test");
        var adminHtml = await administrator.GetStringAsync($"admin/members/{id}");

        Assert.Contains("Remove immediately", adminHtml);
        var antiforgery = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>");
        var response = await member.PostAsync("services", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = $"stop-{service}",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(antiforgery.Groups[1].Value),
            ["Action.Key"] = Guid.NewGuid().ToString(),
            ["Action.Id"] = enrollment!.Value.ToString(),
            ["Action.Name"] = "stop"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await db.ServiceEnrollments.AsNoTracking().SingleAsync(x => x.Id == enrollment)).RenewalStoppedAt);
        var review = await response.Content.ReadAsStringAsync();
        var reviewToken = Regex.Match(review, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>").Groups[1].Value;
        var key = Regex.Match(review, "<input(?=[^>]*name=\"Action.Key\")(?=[^>]*value=\"([^\"]+)\")[^>]*>").Groups[1].Value;
        response = await member.PostAsync("services", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "confirm-action",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(reviewToken),
            ["Action.Key"] = key,
            ["Action.Id"] = enrollment!.Value.ToString(),
            ["Action.Name"] = "stop",
            ["Action.Confirmed"] = "true"
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
    public async Task Billing_configuration_prefills_every_service_field()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var service = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, null,
            new("door", "Door", true, 21, 5, 7, "space")));
        await using var app = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var page = await client.GetAsync("admin/settings/services/" + service);
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.IsSuccessStatusCode, html.Split("HEADERS")[0]);
        string Value(string id) => Regex.Match(html, $"<input(?=[^>]*id=\"{id}\")(?=[^>]*value=\"([^\"]*)\")[^>]*>").Groups[1].Value;

        Assert.DoesNotContain("service-id", html);
        Assert.Equal("door", Value("service-code"));
        Assert.Equal("Door", Value("service-name"));
        Assert.Equal("space", Value("category"));
        Assert.Equal("21", Value("free-days"));
        Assert.Equal("5", Value("grace-days"));
        Assert.Equal("7", Value("priority"));
        Assert.Matches("<input(?=[^>]*id=\"service-code\")(?=[^>]*readonly)[^>]*>", html);
        Assert.Matches("<input(?=[^>]*id=\"self-subscribe\")(?=[^>]*checked)[^>]*>", html);
        Assert.Matches("<option[^>]*(value=\"active\"[^>]*selected|selected[^>]*value=\"active\")", html);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("admin/settings/services/abc")).StatusCode);
        var create = await client.GetStringAsync("admin/settings/services/new");
        Assert.DoesNotContain("View this service's fee prices", WebUtility.HtmlDecode(create));
    }

    [Fact]
    public async Task Billing_configuration_edits_a_service_without_duplicating_it()
    {
        var board = await Board();
        await using var commands = CommandsApp();
        var service = await Run(commands, sp => sp.GetRequiredService<BillingCommands>().ConfigureService(board, null,
            new("door", "Door", true, 21, 5, 7, "space")));
        await using var app = await StartProvider();
        using var client = Client();
        await Login(client, "board@example.test");
        var page = await client.GetAsync("admin/settings/services/" + service);
        var html = await page.Content.ReadAsStringAsync();
        Assert.True(page.IsSuccessStatusCode, html.Split("HEADERS")[0]);
        var antiforgery = Regex.Match(html, "<input(?=[^>]*name=\"__RequestVerificationToken\")(?=[^>]*value=\"([^\"]+)\")[^>]*>");
        var response = await client.PostAsync("admin/settings/services/" + service, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "service-definition",
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(antiforgery.Groups[1].Value),
            ["Service.Id"] = service.ToString(),
            ["Service.Code"] = "door",
            ["Service.Name"] = "Door renamed",
            ["Service.Category"] = "space",
            ["Service.SelfSubscribe"] = "true",
            ["Service.FreeDays"] = "21",
            ["Service.GraceDays"] = "5",
            ["Service.Priority"] = "7",
            ["Service.Retirement"] = "retired"
        }));
        await using var db = Context();
        var edited = await db.ServiceDefinitions.SingleAsync(x => x.Id == service);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Door renamed", edited.Name);
        Assert.True(edited.Retired);
        Assert.True((await db.Fees.SingleAsync(x => x.ServiceDefinitionId == service)).Retired);
        Assert.Equal(1, await db.ServiceDefinitions.CountAsync());
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
        Assert.Contains("Outstanding bills", await client.GetStringAsync("billing"));
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("admin/payments")).Headers.Location!.AbsolutePath);
        Assert.Equal("/Account/AccessDenied", (await client.GetAsync("admin/settings/services")).Headers.Location!.AbsolutePath);
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
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("Check your email to verify your application");
        await using var db = Context();
        var application = await db.Applications.AsNoTracking().SingleAsync();
        var verification = await db.Deliveries.Where(x => x.ApplicationId == application.Id).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(verification));
        var verifyUrl = Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value;
        await memberPage.GotoAsync(verifyUrl);

        Assert.Null((await db.Applications.AsNoTracking().SingleAsync()).VerifiedAt); // Mail-scanner GET is not confirmation.
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Verify my email address" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("Email verified");

        async Task LoginPage(IPage page, string email)
        {
            await page.GotoAsync(root + "Account/Login");
            await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        }
        await LoginPage(boardPage, "board@example.test");
        await boardPage.GotoAsync(root + "admin/applications");
        await Assertions.Expect(boardPage.GetByRole(AriaRole.Img, new() { Name = "Applicant is underage" })).ToBeVisibleAsync();
        await boardPage.GetByRole(AriaRole.Link, new() { Name = "Browser Member — " + Email }).ClickAsync();
        await boardPage.GetByRole(AriaRole.Button, new() { Name = "Approve membership", Exact = true }).ClickAsync();
        await boardPage.GetByRole(AriaRole.Button, new() { Name = "Confirm membership", Exact = true }).ClickAsync();
        await Assertions.Expect(boardPage.GetByRole(AriaRole.Heading, new() { Name = "Account overview", Exact = true })).ToBeVisibleAsync();
        var id = (await db.Members.SingleAsync()).UserId;
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(application.Id));
        await memberPage.GotoAsync(Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value);
        await memberPage.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByLabel("Confirm password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Set password", Exact = true }).ClickAsync();
        await LoginPage(memberPage, Email);
        await memberPage.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        await Assertions.Expect(memberPage).ToHaveTitleAsync("Member details — Synthetic Membership Association");
        await memberPage.GetByLabel("Name", new() { Exact = true }).FillAsync("Unsaved draft name");
        foreach (var (label, section) in new[] { ("Personal details", "personal"), ("Email", "email"), ("Communication preferences", "communications"), ("Account and privacy", "account") })
        {
            await memberPage.GetByRole(AriaRole.Navigation, new() { Name = "On this page", Exact = true })
                .GetByRole(AriaRole.Link, new() { Name = label, Exact = true }).ClickAsync();
            await Assertions.Expect(memberPage).ToHaveURLAsync(root + "profile#" + section);
            await Assertions.Expect(memberPage.Locator("section#" + section)).ToBeInViewportAsync();
            await Assertions.Expect(memberPage.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Unsaved draft name");
        }
        await memberPage.GetByLabel("Name", new() { Exact = true }).FillAsync("Changed Browser Name");
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Save details" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("Changed Browser Name");
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToHaveTextAsync("Personal details saved.");
        await memberPage.GetByLabel("Send invoices via email").UncheckAsync();
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Save communication preferences" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("Email preferences saved");
        await Assertions.Expect(memberPage.GetByLabel("Send invoices via email")).Not.ToBeCheckedAsync();
        await memberPage.SetViewportSizeAsync(375, 812);
        Assert.True(await memberPage.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await memberPage.SetViewportSizeAsync(1280, 800);
        await memberPage.GetByLabel("New email", new() { Exact = true }).FillAsync("browser-changed@example.test");
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Send verification" }).ClickAsync();
        await Assertions.Expect(memberPage.GetByText("Awaiting verification: browser-changed@example.test")).ToBeVisibleAsync();
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("Check your new email for a verification link");
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
        await Assertions.Expect(memberPage.GetByRole(AriaRole.Status)).ToContainTextAsync("password reset link");
        var reset = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.PasswordReset).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(reset));
        await memberPage.GotoAsync(Regex.Match(transport.Sent[^1].Text, @"https?://\S+").Value);
        await memberPage.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByLabel("Confirm password", new() { Exact = true }).FillAsync(Password);
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Set password", Exact = true }).ClickAsync();
        await LoginPage(memberPage, "browser-changed@example.test");
        await memberPage.GetByRole(AriaRole.Link, new() { Name = "Settings", Exact = true }).ClickAsync();
        await memberPage.GetByRole(AriaRole.Link, new() { Name = "Resign and request account deletion", Exact = true }).ClickAsync();
        await memberPage.GetByLabel("I understand and want to resign now.").CheckAsync();
        await memberPage.GetByRole(AriaRole.Button, new() { Name = "Resign and begin account deletion" }).ClickAsync();
        await Assertions.Expect(memberPage).ToHaveURLAsync(root + "Account/Login");
        var warning = await db.Deliveries.Where(x => x.UserId == id && x.Kind == MailKind.DeletionWarning).Select(x => x.Id).SingleAsync();
        await Run(commands, sp => sp.GetRequiredService<MailDispatcher>().Dispatch(warning));

        Assert.Contains("14 days", transport.Sent[^1].Text);
        await memberPage.GotoAsync(root);
        await Assertions.Expect(memberPage).ToHaveURLAsync(new Regex("/Account/Login"));
        await boardPage.GotoAsync(root + "admin/members/" + id + "/details");
        await boardPage.GetByText("Restore account", new() { Exact = true }).ClickAsync();
        await boardPage.GetByLabel("Reason / verified restoration request").FillAsync("Verified request");
        await boardPage.GetByRole(AriaRole.Button, new() { Name = "Cancel pending deletion" }).ClickAsync();
        await Assertions.Expect(boardPage.GetByRole(AriaRole.Heading, new() { Name = "Member registry", Exact = true })).ToBeVisibleAsync();

        Assert.Null((await db.Users.AsNoTracking().SingleAsync(x => x.Id == id)).PendingDeletionAt);
        Assert.Equal(5, transport.Sent.Count); // Verification, welcome, email change, reset, warning.
    }
}
