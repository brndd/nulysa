using Microsoft.AspNetCore.Identity;
using static Nulysa.Localization.UiText;

namespace Nulysa.Localization;

public sealed class LocalizedIdentityErrors : IdentityErrorDescriber
{
    private static IdentityError Translate(IdentityError error) => new() { Code = error.Code, Description = T(error.Description) };
    public override IdentityError PasswordMismatch() => Translate(base.PasswordMismatch());
    public override IdentityError InvalidToken() => Translate(base.InvalidToken());
    public override IdentityError PasswordRequiresNonAlphanumeric() => Translate(base.PasswordRequiresNonAlphanumeric());
    public override IdentityError PasswordRequiresDigit() => Translate(base.PasswordRequiresDigit());
    public override IdentityError PasswordRequiresLower() => Translate(base.PasswordRequiresLower());
    public override IdentityError PasswordRequiresUpper() => Translate(base.PasswordRequiresUpper());
    public override IdentityError PasswordTooShort(int length) => new() { Code = nameof(PasswordTooShort), Description = T("Passwords must be at least {0} characters.", length) };
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => new() { Code = nameof(PasswordRequiresUniqueChars), Description = T("Passwords must use at least {0} different characters.", uniqueChars) };
}
