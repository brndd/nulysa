using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Nulysa.Accounts;
using Nulysa.Data;

namespace Nulysa.Localization;

public sealed record UiLanguage(string Code, string Culture, string Name);

public static class UiLanguages
{
    public const string Cookie = "Nulysa.Language";
    // Add a resource translation and a catalogue entry to support another UI language.
    // Member preferences and campaign variants use this catalogue. Automatic mail has its own translated details and fallback.
    public static IReadOnlyList<UiLanguage> Supported { get; } =
    [new("en", "en-GB", "English"), new("fi", "fi-FI", "Suomi")];

    public static UiLanguage? Find(string? code) => Supported.FirstOrDefault(language =>
        string.Equals(language.Code, code, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(language.Culture, code, StringComparison.OrdinalIgnoreCase));

    public static string Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
}

public sealed class MemberCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;
        var id = Authority.Id(context.User);
        var language = await context.RequestServices.GetRequiredService<AppDbContext>().Users.AsNoTracking()
            .Where(user => user.Id == id).Select(user => user.Language).SingleOrDefaultAsync(context.RequestAborted);
        return UiLanguages.Find(language) is { } selected ? new(selected.Culture, selected.Code) : null;
    }
}

public sealed class BrowserCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        var language = UiLanguages.Find(context.Request.Cookies[UiLanguages.Cookie]);
        return Task.FromResult(language is null ? null : new ProviderCultureResult(language.Culture, language.Code));
    }
}

public sealed class HeaderCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext context)
    {
        var choices = context.Request.GetTypedHeaders().AcceptLanguage;
        var language = choices?.Where(choice => choice.Quality is null or > 0)
            .OrderByDescending(choice => choice.Quality ?? 1).Take(10)
            .Select(choice => UiLanguages.Find(choice.Value.Value) ?? UiLanguages.Find(choice.Value.Value?.Split('-')[0]))
            .FirstOrDefault(choice => choice is not null);
        return Task.FromResult(language is null ? null : new ProviderCultureResult(language.Culture, language.Code));
    }
}
