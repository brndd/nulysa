using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Components;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class UiValidationBrowserTests : BoardTest
{
    [Fact]
    public async Task Finnish_member_and_board_pages_reflow_in_all_built_in_theme_modes_with_named_controls_and_contrast()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        Settings["Association:Name"] = "Tampereen pitkä suomalainen hakkeritilayhdistys";
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        await Verified(commands, "long-finnish-application@example.test");
        var account = (await Run(commands, sp => sp.GetRequiredService<BillingQueries>().Member(board, id)))!;
        await Run(commands, sp => sp.GetRequiredService<BillingCommands>().Import(new("test", "bank", "unmatched-ui4", 12.34m, "EUR", DateOnly.FromDateTime(Clock.Now.DateTime), "unknown")));
        var passwordToken = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, passwordToken, Password));
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var root = Settings["Oidc:Issuer"]!;
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var circuits = new List<string>();
        page.WebSocket += (_, socket) => circuits.Add(socket.Url);
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.GotoAsync(root + "Language");
        await page.GetByLabel("Portal language").SelectOptionAsync("fi");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save language" }).ClickAsync();
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "fi");
        var routes = new[] { "admin", "admin/members", "admin/applications", $"admin/members/{id}/details", $"admin/members/{id}/billing", "admin/payments", "admin/payments/import", "admin/settings/fees/1", "admin/mail-attention" };
        foreach (var theme in new[] { "default", "tampere-hacklab" })
            foreach (var mode in new[] { "light", "dark" })
            {
                await context.AddCookiesAsync([
                    new Cookie { Name = Appearance.ThemeCookie, Value = theme, Url = root },
                new Cookie { Name = Appearance.ModeCookie, Value = mode, Url = root }
                ]);
                foreach (var width in new[] { 320, 375, 768, 1440 })
                {
                    await page.SetViewportSizeAsync(width, 900);
                    foreach (var route in routes)
                    {
                        var response = await page.GotoAsync(root + route);
                        Assert.True(response!.Ok, route);
                        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveCountAsync(1);
                        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), $"{route}: {theme}/{mode} at {width}");
                        if (route == "admin/members")
                        {
                            Assert.True(await page.EvaluateAsync<bool>("""
                            (() => {
                                const rows = new Map();
                                for (const field of document.querySelectorAll('.filter-fields > div')) {
                                    const row = field.getBoundingClientRect().top;
                                    const bottom = field.querySelector('input, select').getBoundingClientRect().bottom;
                                    if (!rows.has(row)) rows.set(row, []);
                                    rows.get(row).push(bottom);
                                }
                                return [...rows.values()].every(bottoms => Math.max(...bottoms) - Math.min(...bottoms) <= 1);
                            })()
                            """), $"Registry filters misaligned: {theme}/{mode} at {width}");
                            var evidence = Environment.GetEnvironmentVariable("NULYSA_UI_EVIDENCE");
                            if (!string.IsNullOrWhiteSpace(evidence) && width == 1440)
                            {
                                Directory.CreateDirectory(evidence);
                                await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"registry-{theme}-{mode}.png"), FullPage = true });
                            }
                        }
                        Assert.Empty(await page.EvaluateAsync<string[]>("""
                        [...document.querySelectorAll('main input:not([type=hidden]):not([type=submit]), main select, main textarea')]
                          .filter(el => !el.labels?.length && !el.getAttribute('aria-label') && !el.getAttribute('aria-labelledby'))
                          .map(el => el.name || el.id)
                        """));
                    }
                    if (width is 375 or 1440)
                    {
                        await page.GotoAsync(root + $"admin/members/{id}/details");
                        var evidence = Environment.GetEnvironmentVariable("NULYSA_UI_EVIDENCE");
                        if (!string.IsNullOrWhiteSpace(evidence))
                        {
                            Directory.CreateDirectory(evidence);
                            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"{theme}-{mode}-{width}.png"), FullPage = true });
                        }
                    }
                }
                var failures = await page.EvaluateAsync<string[]>("""
                (() => {
                    const css = getComputedStyle(document.documentElement);
                    const rgb = name => {
                        const el = document.createElement('span');
                        el.style.color = css.getPropertyValue('--nulysa-' + name);
                        document.body.append(el);
                        const channels = getComputedStyle(el).color.match(/[\d.]+/g).slice(0, 3).map(Number);
                        el.remove();
                        return channels.map(v => { v /= 255; return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4; });
                    };
                    const luminance = name => { const [r,g,b] = rgb(name); return .2126*r + .7152*g + .0722*b; };
                    const ratio = (a,b) => { const x = luminance(a), y = luminance(b); return (Math.max(x,y)+.05)/(Math.min(x,y)+.05); };
                    const pairs = [
                        ['text','page',4.5], ['text','surface',4.5], ['text','muted',4.5],
                        ['secondary','page',4.5], ['secondary','surface',4.5], ['secondary','muted',4.5],
                        ['link','page',4.5], ['link','surface',4.5], ['danger','surface',4.5],
                        ['action-text','action-bg',4.5], ['focus','surface',3], ['focus','page',3], ['border','surface',3]
                    ];
                    return pairs.filter(([a,b,min]) => ratio(a,b) < min).map(([a,b]) => a + '/' + b + ': ' + ratio(a,b));
                })()
                """);
                Assert.Empty(failures);
            }
        await context.ClearCookiesAsync();
        await context.AddCookiesAsync([new Cookie { Name = "Nulysa.Language", Value = "fi", Url = root }]);
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Sähköposti", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Salasana", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Kirjaudu sisään", Exact = true }).ClickAsync();
        foreach (var theme in new[] { "default", "tampere-hacklab" })
            foreach (var mode in new[] { "light", "dark" })
            {
                await context.AddCookiesAsync([
                    new Cookie { Name = Appearance.ThemeCookie, Value = theme, Url = root },
                new Cookie { Name = Appearance.ModeCookie, Value = mode, Url = root }
                ]);
                foreach (var width in new[] { 320, 375, 768, 1440 })
                {
                    await page.SetViewportSizeAsync(width, 900);
                    foreach (var route in new[] { "", "services", "billing", "profile", "resign", $"billing/invoices/{account.Invoices[0].Invoice.Id}" })
                    {
                        var response = await page.GotoAsync(root + route);
                        Assert.True(response!.Ok, route);
                        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"), $"{route}: {theme}/{mode} at {width}");
                        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveCountAsync(1);
                    }
                }
            }
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(root + "services");
        await page.EvaluateAsync("document.documentElement.style.fontSize = '200%'");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        Assert.Empty(errors);
        Assert.Empty(circuits);
    }

    [Fact]
    public async Task Finnish_member_forms_restore_dialog_focus_and_focus_server_errors_without_a_circuit()
    {
        Settings["Billing:PurchaseBalanceEnabled"] = "true";
        var board = await Board();
        await using var commands = CommandsApp();
        var id = await Admit(commands, board);
        var token = await PasswordToken(commands, id);
        await Run(commands, sp => sp.GetRequiredService<AccountCommands>().SetPassword(id, token, Password));
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 320, Height = 812 }, ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync();
        var root = Settings["Oidc:Issuer"]!;
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(Email);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.GotoAsync(root + "services");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Jäsenyyden yhteenveto", Exact = true })).ToBeVisibleAsync();
        var menu = page.GetByRole(AriaRole.Button, new() { Name = "Valikko", Exact = true });
        await menu.FocusAsync();
        await menu.PressAsync("Enter");
        await Assertions.Expect(menu).ToHaveAttributeAsync("aria-expanded", "true");
        await menu.PressAsync("Escape");
        await Assertions.Expect(menu).ToBeFocusedAsync();
        var trigger = page.GetByText("Siirrä varoja saldojen välillä", new() { Exact = true });
        await trigger.FocusAsync();
        await trigger.PressAsync("Enter");
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await dialog.PressAsync("Escape");
        await Assertions.Expect(trigger).ToBeFocusedAsync();
        await trigger.ClickAsync();
        await dialog.GetByLabel("Summa (EUR)").FillAsync("1.25");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Siirrä käytettävissä olevia varoja" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeFocusedAsync();
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GotoAsync(root + "profile");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.GotoAsync(root + "billing");
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
    }
}
