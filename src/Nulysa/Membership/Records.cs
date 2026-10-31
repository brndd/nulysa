using System.ComponentModel.DataAnnotations;
using Nulysa.Accounts;

namespace Nulysa.Membership;

public sealed class ProfileInput
{
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [Required, StringLength(200)] public string Municipality { get; set; } = "";
    [Required] public DateOnly? DateOfBirth { get; set; }
    [Required, RegularExpression("^(en|fi)$")] public string Language { get; set; } = "en";
    [StringLength(50)] public string? PhoneNumber { get; set; }
    [StringLength(100)] public string? Nickname { get; set; }
    [StringLength(200)] public string? MatrixId { get; set; }

    public void Validate(DateOnly today)
    {
        Validator.ValidateObject(this, new ValidationContext(this), true);
        if (DateOfBirth > today)
            throw new ValidationException("Enter a valid date of birth.");
    }

    public void CopyTo(User user)
    {
        user.Name = Name.Trim(); user.Municipality = Municipality.Trim(); user.DateOfBirth = DateOfBirth;
        user.Language = Language; user.PhoneNumber = PhoneNumber?.Trim(); user.Nickname = Nickname?.Trim(); user.MatrixId = MatrixId?.Trim();
    }

}

public sealed class ApplicationInput
{
    [Required] public ProfileInput Profile { get; set; } = new();
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = "";
    [Required, StringLength(4000)] public string Message { get; set; } = "";
    [Range(typeof(bool), "true", "true", ErrorMessage = "Accept the membership terms.")] public bool AcceptTerms { get; set; }
}

public sealed class MembershipApplication
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string Name { get; set; } = "";
    public string Municipality { get; set; } = "";
    public DateOnly DateOfBirth { get; set; }
    public string Language { get; set; } = "en";
    public string? PhoneNumber { get; set; }
    public string? Nickname { get; set; }
    public string? MatrixId { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset TermsAcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset VerificationRequestedAt { get; set; }
    public Guid VerificationVersion { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public string? RejectionExplanation { get; set; }
}

public sealed class Member
{
    public long UserId { get; set; }
    public DateOnly JoinedOn { get; set; }
    public DateTimeOffset? DeletionEligibleAt { get; set; }
    public Guid? DeletionOperation { get; set; }
}

// Only historical IDs and the approval time survive erasure; Id is the admission retry key.
public sealed class Approval
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public long AdministratorId { get; set; }
    public DateTimeOffset ApprovedAt { get; set; }
}

public sealed class LifecycleAction
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public long ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}
