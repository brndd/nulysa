namespace Nulysa.Membership;

public sealed class MembershipOptions
{
    public int UnverifiedRetentionHours { get; set; } = 24;
    public int DeletionWaitingDays { get; set; } = 14;
    public int RejectionRetentionDays { get; set; } = 7;
    public string DefaultLanguage { get; set; } = "en";
}

public sealed class AssociationClock(TimeProvider clock, IConfiguration config)
{
    public DateTimeOffset Now => clock.GetUtcNow();
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(config["Association:TimeZone"] ?? "Europe/Helsinki");
    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, Zone).DateTime);

    public DateTimeOffset AfterCalendarDays(int days)
    {
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(Now, Zone).DateTime.AddDays(days), DateTimeKind.Unspecified);
        // Never move erasure earlier to resolve a missing or ambiguous local wall time.
        while (Zone.IsInvalidTime(local))
            local = local.AddMinutes(1);
        var offset = Zone.IsAmbiguousTime(local) ? Zone.GetAmbiguousTimeOffsets(local).Min() : Zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
