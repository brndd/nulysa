namespace Nulysa.Communications;

public enum CampaignKind { Newsletter, OfficialNotice }

public sealed class MailCampaign
{
    public Guid Id { get; set; }
    public Guid Revision { get; set; }
    public string Title { get; set; } = "";
    public CampaignKind Kind { get; set; }
    public string Content { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? QueuedAt { get; set; }
    public long? QueuedBy { get; set; }
    public int RecipientCount { get; set; }
    public int AcceptedRecipients { get; set; }
}

public sealed class OperationalMailTemplate
{
    public MailKind Kind { get; set; }
    public string Content { get; set; } = "[]";
    public string FallbackLanguage { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public long UpdatedBy { get; set; }
}

public sealed class MailDeliveryRetry
{
    public Guid DeliveryId { get; set; }
    public int QueueVersion { get; set; }
    public long ActorId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
}

public sealed record CampaignSummary(Guid Id, Guid Revision, string Title, CampaignKind Kind, DateTimeOffset UpdatedAt, DateTimeOffset? QueuedAt, int RecipientCount, int AcceptedRecipients);
public sealed record CampaignHistoryEntry(CampaignSummary Campaign, bool Processing);
public sealed record CampaignHistoryPage(IReadOnlyList<CampaignHistoryEntry> Entries, int Page, int PageCount);
public sealed record CampaignDraft(CampaignSummary Campaign, IReadOnlyList<MailVariant> Variants);
public sealed record CampaignLanguageGroup(string PreferredLanguage, int Recipients, string? OutgoingLanguage, MailContent? Content);
public sealed record CampaignPreview(CampaignSummary Campaign, IReadOnlyList<CampaignLanguageGroup> Groups, string? Confirmation);
public sealed record CampaignRecipient(Guid Id, long? UserId, string? PreferredLanguage, string? OutgoingLanguage,
    DeliveryState State, int Attempts, DateTimeOffset? AcceptedAt, string? ProviderId, bool Test);
public sealed record CampaignProgress(CampaignSummary Campaign, IReadOnlyDictionary<DeliveryState, int> Counts, int NoLongerRetained,
    IReadOnlyList<CampaignRecipient> Recipients, int TotalRows, int Page);
public sealed record OperationalTemplateView(MailKind Kind, IReadOnlyList<MailVariant> Variants, string FallbackLanguage,
    IReadOnlyList<string> BuiltInLanguages, DateTimeOffset? UpdatedAt);
