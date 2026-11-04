using Microsoft.AspNetCore.Identity;

namespace Nulysa.Accounts;

public sealed class User : IdentityUser<long>
{
    public string Name { get; set; } = "";
    public string Municipality { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string Language { get; set; } = "en";
    public string? Nickname { get; set; }
    public string? MatrixId { get; set; }
    public string? PendingEmail { get; set; }
    public Guid? PendingEmailVersion { get; set; }
    public DateTimeOffset? PendingEmailRequestedAt { get; set; }
    public string OidcSubject { get; init; } = Guid.NewGuid().ToString("N");
    public bool EmailNewsletters { get; set; } = true;
    public bool Admitted { get; set; }
    public DateTimeOffset? PendingDeletionAt { get; set; }
    public bool MayLogIn => Admitted && PendingDeletionAt is null;
}
