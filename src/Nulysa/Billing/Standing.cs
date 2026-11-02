namespace Nulysa.Billing;

public enum StandingState
{
    Unavailable,
    Covered,
    MembershipInitialGrace,
    MembershipRenewalGrace,
    MembershipInactive,
    NotSubscribed,
    ServiceBlockedByMembership,
    CoveredWithRenewalStopped,
    ServiceRenewalGrace,
    AwaitingPayment
}

public sealed record Standing(StandingState State, DateOnly? Until, DateOnly? InactiveSince = null, bool DueToday = false)
{
    public bool Active => State is StandingState.Covered or StandingState.MembershipInitialGrace
        or StandingState.MembershipRenewalGrace or StandingState.CoveredWithRenewalStopped or StandingState.ServiceRenewalGrace;

    // Existing attention/notice behavior includes the initial membership payment window.
    public bool IsMembershipGrace => State is StandingState.MembershipInitialGrace or StandingState.MembershipRenewalGrace;

    public string Description => State switch
    {
        StandingState.Unavailable => "Pending deletion or unavailable",
        StandingState.Covered => "Covered",
        StandingState.MembershipInitialGrace or StandingState.MembershipRenewalGrace => DueToday ? "Due today" : "Active during grace",
        StandingState.MembershipInactive => "Inactive — membership continues; billing paused",
        StandingState.NotSubscribed => "Not subscribed",
        StandingState.ServiceBlockedByMembership => "Membership inactive",
        StandingState.CoveredWithRenewalStopped => "Renewal stopped — covered",
        StandingState.ServiceRenewalGrace => DueToday ? "Due today" : "Renewal grace",
        StandingState.AwaitingPayment => "Awaiting payment — no current coverage",
        _ => throw new ArgumentOutOfRangeException(nameof(State))
    };
}
