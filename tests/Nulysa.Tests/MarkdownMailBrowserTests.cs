using Microsoft.Playwright;

namespace Nulysa.Tests;

[Trait("Group", "Browser")]
public sealed class MarkdownMailBrowserTests : BoardTest
{
    [Fact]
    public async Task Administrator_can_preview_save_and_reopen_markdown_drafts_and_templates()
    {
        await Board();
        await using var web = await StartProvider();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var root = Settings["Oidc:Issuer"]!;
        await context.AddCookiesAsync([new Cookie { Name = "Nulysa.Language", Value = "en", Url = root }]);
        var page = await context.NewPageAsync();
        await page.GotoAsync(root + "Account/Login");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync("board@example.test");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();

        await page.GotoAsync(root + "admin/communications/drafts");
        var start = page.GetByRole(AriaRole.Button, new() { Name = "New draft", Exact = true });
        await Assertions.Expect(start).ToBeEnabledAsync();
        await start.ClickAsync();
        await page.GetByLabel("Draft title", new() { Exact = true }).FillAsync("Workshop");
        var english = page.Locator("fieldset").Filter(new() { HasText = "English" });
        var subject = english.GetByLabel("Subject", new() { Exact = true });
        var write = english.GetByRole(AriaRole.Tab, new() { Name = "Write", Exact = true });
        var preview = english.GetByRole(AriaRole.Tab, new() { Name = "Preview", Exact = true });
        var previewPanel = english.GetByRole(AriaRole.Tabpanel, new() { Name = "Preview" });
        var body = english.GetByLabel("Message body (Markdown)", new() { Exact = true });
        var toolbar = english.GetByRole(AriaRole.Toolbar, new() { Name = "Markdown formatting" });
        var bold = toolbar.GetByRole(AriaRole.Button, new() { Name = "Bold", Exact = true });
        await Assertions.Expect(toolbar).ToBeVisibleAsync();
        await toolbar.FocusAsync();
        var heading = toolbar.GetByRole(AriaRole.Button, new() { Name = "Heading", Exact = true });
        await Assertions.Expect(heading).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(bold).ToBeFocusedAsync();

        await preview.ClickAsync();
        await Assertions.Expect(previewPanel.Locator("p").First).ToHaveTextAsync("Subject:");
        await Assertions.Expect(previewPanel.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        await subject.FillAsync("Open workshop");
        await Assertions.Expect(previewPanel).ToContainTextAsync("Open workshop");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await write.ClickAsync();

        await body.FillAsync("Bring tools");
        await body.EvaluateAsync("el => el.setSelectionRange(8, 8)");
        await heading.ClickAsync();
        await Assertions.Expect(body).ToHaveValueAsync("## Bring tools");
        await heading.ClickAsync();
        await Assertions.Expect(body).ToHaveValueAsync("Bring tools");
        await body.SelectTextAsync();
        await bold.ClickAsync();
        await Assertions.Expect(body).ToHaveValueAsync("**Bring tools**");
        await bold.ClickAsync();
        await Assertions.Expect(body).ToHaveValueAsync("Bring tools");
        await bold.ClickAsync();
        await body.PressAsync("ControlOrMeta+z");
        await Assertions.Expect(body).ToHaveValueAsync("Bring tools");

        await body.FillAsync("Bring tools\nInvite a friend");
        await body.SelectTextAsync();
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "Bullet list", Exact = true }).ClickAsync();
        await Assertions.Expect(body).ToHaveValueAsync("- Bring tools\n- Invite a friend");
        await subject.FillAsync("");
        await write.PressAsync("ArrowRight");
        await Assertions.Expect(preview).ToBeFocusedAsync();
        await Assertions.Expect(toolbar).ToBeHiddenAsync();
        await Assertions.Expect(previewPanel.Locator("p").First).ToHaveTextAsync("Subject:");
        await Assertions.Expect(previewPanel.Locator("li")).ToHaveTextAsync(new[] { "Bring tools", "Invite a friend" });
        await page.Locator("html").EvaluateAsync("el => el.dataset.mode = 'dark'");
        Assert.True(await previewPanel.Locator(".markdown-preview").EvaluateAsync<bool>("""
            el => {
                const pane = getComputedStyle(el);
                const panel = getComputedStyle(el.closest(".markdown-panel"));
                return pane.color === panel.color && pane.color !== "rgb(0, 0, 0)"
                    && pane.backgroundColor !== "rgb(255, 255, 255)";
            }
            """));
        await page.Locator("html").EvaluateAsync("el => el.dataset.mode = 'light'");
        await subject.FillAsync("Open workshop");
        await preview.PressAsync("ArrowLeft");
        await Assertions.Expect(write).ToBeFocusedAsync();
        await Assertions.Expect(body).ToHaveValueAsync("- Bring tools\n- Invite a friend");
        Assert.Equal("- Bring tools\n- Invite a friend",
            await body.EvaluateAsync<string>("el => el.value.slice(el.selectionStart, el.selectionEnd)"));

        var header = english.Locator(".markdown-header");
        Assert.True(await header.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
        var tabs = english.GetByRole(AriaRole.Tablist);
        Assert.True((await toolbar.BoundingBoxAsync())!.Y < (await tabs.BoundingBoxAsync())!.Y + 8);
        await page.SetViewportSizeAsync(375, 812);
        await page.Locator("html").EvaluateAsync("el => el.dataset.mode = 'dark'");
        Assert.True(await header.EvaluateAsync<bool>("el => el.scrollWidth <= el.clientWidth + 1"));
        Assert.True((await toolbar.BoundingBoxAsync())!.Y > (await tabs.BoundingBoxAsync())!.Y);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        var draftRoute = new System.Text.RegularExpressions.Regex("/admin/communications/drafts/[0-9a-f-]+$");
        await Assertions.Expect(page).ToHaveURLAsync(draftRoute);
        await Assertions.Expect(page.GetByText("Draft saved.", new() { Exact = true })).ToBeVisibleAsync();
        var firstDraft = page.Url;
        await page.ReloadAsync();
        await Assertions.Expect(body).ToHaveValueAsync("- Bring tools\n- Invite a friend");
        await Assertions.Expect(page.GetByText("Draft saved.", new() { Exact = true })).ToHaveCountAsync(0);

        await page.GetByRole(AriaRole.Button, new() { Name = "New draft", Exact = true }).ClickAsync();
        await Assertions.Expect(body).ToHaveValueAsync("");
        await page.GetByLabel("Draft title", new() { Exact = true }).FillAsync("Second workshop");
        await subject.FillAsync("Second workshop");
        await body.FillAsync("Bring a friend.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(draftRoute);
        await Assertions.Expect(page).Not.ToHaveURLAsync(firstDraft);
        await Assertions.Expect(page.GetByText("Draft saved.", new() { Exact = true })).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(body).ToHaveValueAsync("Bring a friend.");

        await page.GotoAsync(root + "admin/communications/templates");
        await page.GetByRole(AriaRole.Link, new() { Name = "Application verification", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(root + "admin/communications/templates/Verification");
        await Assertions.Expect(english.GetByRole(AriaRole.Link, new() { Name = "Liquid documentation", Exact = true }))
            .ToHaveAttributeAsync("href", "https://shopify.github.io/liquid/");
        await Assertions.Expect(body).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("verification_url"));
        await body.FillAsync(@"Thanks for applying. {% raw %}{{example}}{% endraw %}. Reply by {{verification_expires_at}}. Verify now");
        await body.EvaluateAsync("el => el.setSelectionRange(el.value.length - 10, el.value.length)");
        await english.GetByLabel("Template field", new() { Exact = true }).SelectOptionAsync("verification_url");
        await english.GetByRole(AriaRole.Button, new() { Name = "Insert in body", Exact = true }).ClickAsync();
        const string edited = @"Thanks for applying. {% raw %}{{example}}{% endraw %}. Reply by {{verification_expires_at}}. [Verify now]({{verification_url}})";
        await Assertions.Expect(body).ToHaveValueAsync(edited);
        await preview.ClickAsync();
        await Assertions.Expect(previewPanel).ToContainTextAsync("Thanks for applying. {{example}}.");
        await Assertions.Expect(previewPanel.GetByRole(AriaRole.Link, new() { Name = "Verify now" }))
            .ToHaveAttributeAsync("href", "https://example.org/apply/verify?id=example");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save template", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByText("Template saved.", new() { Exact = true })).ToBeVisibleAsync();
        await page.ReloadAsync();
        await Assertions.Expect(body).ToHaveValueAsync(edited);
        var languages = page.GetByRole(AriaRole.Navigation, new() { Name = "Template language" }).GetByRole(AriaRole.List);
        await Assertions.Expect(languages.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(2);
        await Assertions.Expect(languages.Locator("span[aria-current=page]")).ToHaveTextAsync("English");
        await Assertions.Expect(languages.GetByRole(AriaRole.Link)).ToHaveCountAsync(1);
        await languages.GetByRole(AriaRole.Link, new() { Name = "Suomi", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("#markdown-fi-body")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("Vahvista sähköpostiosoitteesi"));
        await Assertions.Expect(languages.Locator("span[aria-current=page]")).ToHaveTextAsync("Suomi");
        await languages.GetByRole(AriaRole.Link, new() { Name = "English", Exact = true }).ClickAsync();
        var reset = page.GetByRole(AriaRole.Button, new() { Name = "Restore default for this language", Exact = true });
        await reset.ClickAsync();
        await Assertions.Expect(page.GetByText("Default template restored.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(body).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("Verify your email"));
    }
}
