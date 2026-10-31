namespace Nulysa.Services;

public sealed class ServiceDefinition
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool SelfSubscriptionAllowed { get; set; }
    public int InitialFreeDays { get; set; } = 14;
    public int RenewalGraceDays { get; set; } = 7;
    public int Priority { get; set; } = 100;
    public bool Retired { get; set; }
}

public sealed class ServiceEnrollment
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ServiceDefinitionId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? RenewalStoppedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public DateOnly? FirstPaidOn { get; set; }
}

public sealed class ServiceChange
{
    public long Id { get; set; }
    public long? ServiceEnrollmentId { get; set; }
    public Guid OperationId { get; set; }
    public string Action { get; set; } = "";
}
