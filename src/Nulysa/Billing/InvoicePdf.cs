using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Billing;

public sealed class InvoicePdf(AppDbContext db, IOptions<BillingOptions> options, IConfiguration configuration, AssociationClock clock)
{
    public const string TypstVersion = "0.15.0";

    public static void VerifyRenderer(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start Typst.");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("The invoice renderer did not report its version in time.");
        }
        if (process.ExitCode != 0 || !output.Result.StartsWith("typst " + TypstVersion + " ", StringComparison.Ordinal))
            throw new InvalidOperationException("The invoice renderer must be Typst " + TypstVersion + ".");
    }

    public async Task<byte[]> ForMember(ClaimsPrincipal actor, long invoiceId, CancellationToken ct = default)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId, ct)
            ?? throw new KeyNotFoundException("Invoice not found.");
        var account = await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.Id == invoice.BillingAccountId, ct);
        await Authority.RequireOwnerOrAdministrator(db, actor, account.UserId ?? 0, ct);
        return await Render(invoiceId, null, ct) ?? throw new InvalidOperationException("Invoice changed during rendering; reload and try again.");
    }

    internal async Task<byte[]?> Render(long invoiceId, int? requiredRevision, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId, ct);
        if (requiredRevision is not null && invoice.Revision != requiredRevision)
            return null;
        var facts = await BillingFacts.Load(db, invoice.BillingAccountId, ct);
        var view = BillingQueries.View(facts, invoice);
        var settings = options.Value;
        if (!BillingOptions.ValidIban(settings.Iban))
            throw new InvalidOperationException("Billing:Iban must be a checksum-valid payment destination.");
        var data = new
        {
            number = invoice.Id.ToString(),
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(invoice.IssuedAt, clock.Zone).DateTime).ToString("yyyy-MM-dd"),
            due_date = (invoice.DueOn ?? view.Lines.Min(x => x.Line.ChargeDueOn)).ToString("yyyy-MM-dd"),
            language = facts.User?.Language ?? "en",
            reference = view.Reference,
            sender = new { name = configuration["Association:Name"] ?? "Nulysa", address = settings.Address, business_id = settings.BusinessId, email = settings.ContactEmail, iban = settings.Iban, bic = settings.Bic, bank = settings.Bank },
            recipient = new { name = facts.User?.Name ?? "Deleted member", address = Array.Empty<string>() },
            items = view.Lines.Where(x => x.Line.State is LineState.Open or LineState.Fulfilled).Select(x => new
            {
                description = x.Line.Description,
                period = PeriodLabel(x.Line, facts.User?.Language == "fi"),
                state = x.Line.State.ToString(),
                amount_cents = checked((long)(x.Line.Amount * 100))
            }),
            paid_cents = checked((long)(view.Paid * 100)),
            held_cents = checked((long)(view.Held * 100)),
            remaining_cents = checked((long)(view.Remaining * 100))
        };
        var root = Path.Combine(Path.GetTempPath(), "nulysa-invoice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var templates = settings.TemplateDirectory ?? Path.Combine(AppContext.BaseDirectory, "Billing", "Templates");
            foreach (var file in Directory.EnumerateFiles(templates, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Invoice assets must be ordinary private files, not symlinks.");
                var target = Path.Combine(root, Path.GetRelativePath(templates, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "invoice.json"), JsonSerializer.Serialize(data), ct);
            Directory.CreateDirectory(Path.Combine(root, "packages"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using (var version = Process.Start(new ProcessStartInfo(settings.TypstPath) { ArgumentList = { "--version" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!)
            {
                var output = version.StandardOutput.ReadToEndAsync(deadline.Token);
                try
                {
                    await version.WaitForExitAsync(deadline.Token);
                }
                catch
                {
                    if (!version.HasExited)
                        version.Kill(entireProcessTree: true);
                    throw;
                }
                if (version.ExitCode != 0 || !(await output).StartsWith("typst " + TypstVersion + " ", StringComparison.Ordinal))
                    throw new InvalidOperationException("The invoice renderer must be Typst " + TypstVersion + ".");
            }
            var info = new ProcessStartInfo(settings.TypstPath) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "compile", "--root", root, "--ignore-system-fonts", "--package-path", Path.Combine(root, "packages"), "--package-cache-path", Path.Combine(root, "packages"), "--input", "invoice=invoice.json", "lasku.typ", "invoice.pdf" })
                info.ArgumentList.Add(argument);
            // Trusted templates are self-contained. No network packages are permitted.
            info.Environment["http_proxy"] = "http://127.0.0.1:9";
            info.Environment["https_proxy"] = "http://127.0.0.1:9";
            info.Environment["ALL_PROXY"] = "http://127.0.0.1:9";
            info.Environment["NO_PROXY"] = "";
            info.Environment["no_proxy"] = "";
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Typst.");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Invoice rendering failed; verify the trusted template and payment configuration.");
            if (!await db.Invoices.AsNoTracking().AnyAsync(x => x.Id == invoiceId && x.Revision == invoice.Revision, ct))
                return null;
            return await File.ReadAllBytesAsync(Path.Combine(root, "invoice.pdf"), ct);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private static string PeriodLabel(InvoiceLine line, bool finnish)
    {
        if (line.Benefit == Benefit.OneOff)
            return $"{(finnish ? "Maksa viimeistään" : "Pay by")} {line.ChargeDueOn:yyyy-MM-dd}";

        var through = line.EndExclusive.AddDays(-1);
        return finnish ? $"{line.StartsOn:yyyy-MM-dd} – {through:yyyy-MM-dd}"
            : $"{line.StartsOn:yyyy-MM-dd} through {through:yyyy-MM-dd}";
    }
}
