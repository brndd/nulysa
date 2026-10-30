using Microsoft.AspNetCore.Identity;

namespace Nulysa.Accounts;

public sealed class User : IdentityUser<long>
{
    public string Name { get; set; } = "";
    public string OidcSubject { get; init; } = Guid.NewGuid().ToString("N");
    public bool Admitted { get; set; }
    public DateTimeOffset? PendingDeletionAt { get; set; }
    public bool MayLogIn => Admitted && PendingDeletionAt is null;
}
