using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Localization;
using Quartz;

namespace Nulysa.Communications;

public sealed class Campaigns(AppDbContext db, ISchedulerFactory schedulers, IDataProtectionProvider protection, TimeProvider clock)
{
    public const int PageSize = 50;
    private ITimeLimitedDataProtector Confirmations => protection.CreateProtector("MailCampaignConfirmation.v1").ToTimeLimitedDataProtector();
    private ITimeLimitedDataProtector TestMessages => protection.CreateProtector("MailCampaignTest.v1").ToTimeLimitedDataProtector();

    public async Task<IReadOnlyList<CampaignSummary>> List(ClaimsPrincipal actor, int page = 1, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        return (await db.MailCampaigns.AsNoTracking().OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Skip(Offset(page)).Take(PageSize).ToListAsync(ct)).Select(Summary).ToArray();
    }

    public async Task<CampaignDraft> Get(ClaimsPrincipal actor, Guid id, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var campaign = await Find(id, ct);
        return new(Summary(campaign), RichMail.Read(campaign.Content));
    }

    public async Task<CampaignSummary> Save(ClaimsPrincipal actor, Guid id, Guid? expectedRevision, string title, CampaignKind kind,
        IReadOnlyCollection<MailVariant> variants, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (id == Guid.Empty || !Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl))
            throw new ValidationException("Provide a valid campaign title and type.");
        var content = RichMail.Store(RichMail.Validate(variants));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // This also serializes competing requests to create a previously unused ID.
        await DatabaseLocks.AcquireForIdentity(db, $"mail-campaign:{id:D}", ct);
        var campaign = await db.MailCampaigns.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (campaign?.Revision != expectedRevision)
            throw new ValidationException("The draft has changed. Reload it before editing.");
        if (campaign is null)
            db.MailCampaigns.Add(campaign = new() { Id = id });
        RequireDraft(campaign);
        campaign.Revision = Guid.NewGuid();
        campaign.Title = title.Trim();
        campaign.Kind = kind;
        campaign.Content = content;
        campaign.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Summary(campaign);
    }

    public async Task<CampaignSummary> Copy(ClaimsPrincipal actor, Guid sourceId, Guid newId, string title, CancellationToken ct = default)
    {
        var source = await Get(actor, sourceId, ct);
        return await Save(actor, newId, null, title, source.Campaign.Kind, source.Variants, ct);
    }

    public async Task<CampaignPreview> Preview(ClaimsPrincipal actor, Guid id, IReadOnlyDictionary<string, string> fallbacks,
        CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var campaign = await Find(id, ct);
        RequireDraft(campaign);
        var plan = await Plan(campaign, fallbacks, ct);
        var token = plan.Recipients.Length > 0 && plan.Groups.All(x => x.OutgoingLanguage is not null)
            ? Confirmations.Protect(JsonSerializer.Serialize(new Confirmation(id, Authority.Id(actor), plan.Fingerprint)), TimeSpan.FromMinutes(20))
            : null;
        return new(Summary(campaign), plan.Groups, token);
    }

    public async Task<CampaignSummary> Send(ClaimsPrincipal actor, Guid id, IReadOnlyDictionary<string, string> fallbacks, string confirmation,
        CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var proof = ReadConfirmation(confirmation);
        if (proof is null || proof.CampaignId != id || proof.ActorId != Authority.Id(actor))
            throw new ValidationException("Confirm the message from a current preview.");

        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        await DatabaseLocks.AcquireForIdentity(db, $"mail-campaign:{id:D}", ct);
        var campaign = await db.MailCampaigns.SingleAsync(x => x.Id == id, ct);
        if (campaign.QueuedAt is not null)
            return Summary(campaign);

        var plan = await Plan(campaign, fallbacks, ct);
        if (plan.Recipients.Length == 0 || plan.Groups.Any(x => x.OutgoingLanguage is null) || plan.Fingerprint != proof.Fingerprint)
            throw new ValidationException("The draft, audience or fallbacks changed. Preview and confirm again.");

        var kind = campaign.Kind == CampaignKind.Newsletter ? MailKind.Newsletter : MailKind.OfficialNotice;
        var now = clock.GetUtcNow();
        var deliveries = plan.Recipients.Select(x => new Delivery
        {
            Id = Guid.NewGuid(), Kind = kind, CampaignId = id, UserId = x.UserId,
            PreferredLanguage = x.PreferredLanguage, OutgoingLanguage = x.OutgoingLanguage, CreatedAt = now
        }).ToArray();
        db.Deliveries.AddRange(deliveries);
        campaign.QueuedAt = campaign.UpdatedAt = now;
        campaign.QueuedBy = Authority.Id(actor);
        campaign.RecipientCount = deliveries.Length;
        await db.SaveChangesAsync(ct);
        await MailQueue.ScheduleBatch(scheduler, deliveries, ct);
        await tx.CommitAsync(ct);
        return Summary(campaign);
    }

    public async Task<Guid> SendTest(ClaimsPrincipal actor, Guid id, Guid expectedRevision, string language, string address,
        CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var campaign = await Find(id, ct);
        RequireDraft(campaign);
        if (campaign.Revision != expectedRevision)
            throw new ValidationException("The draft has changed. Reload it before testing.");
        if (address is not { Length: > 0 and <= 256 } || address.Any(char.IsControl) ||
            !MailAddress.TryCreate(address, out var mailbox) || mailbox.Address != address)
            throw new ValidationException("Enter one email address for the test.");
        var outgoing = UiLanguages.Find(language)?.Code;
        var variant = RichMail.Read(campaign.Content).SingleOrDefault(x => x.Language == outgoing)
            ?? throw new ValidationException("Choose a saved language for the test.");
        var content = Render(campaign.Kind, variant);
        var message = new OutgoingMail(address, "[TEST] " + content.Subject, content.Text, Html: content.Html);
        var delivery = new Delivery
        {
            Id = Guid.NewGuid(), Kind = MailKind.CampaignTest, CampaignId = id, OutgoingLanguage = outgoing,
            CreatedAt = clock.GetUtcNow(), ProtectedTestMessage = TestMessages.Protect(JsonSerializer.Serialize(message), TimeSpan.FromDays(1))
        };
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync(ct);
        await MailQueue.Schedule(scheduler, delivery, ct);
        await tx.CommitAsync(ct);
        return delivery.Id;
    }

    public async Task<CampaignProgress> Progress(ClaimsPrincipal actor, Guid id, int page = 1, DeliveryState? state = null,
        CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        var campaign = await Find(id, ct);
        var deliveries = db.Deliveries.AsNoTracking().Where(x => x.CampaignId == id);
        var counts = await deliveries.Where(x => x.Kind != MailKind.CampaignTest)
            .GroupBy(x => x.State).Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.State, x => x.Count, ct);
        if (state is not null)
            deliveries = deliveries.Where(x => x.State == state);
        var total = await deliveries.CountAsync(ct);
        var rows = await deliveries.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip(Offset(page)).Take(PageSize)
            .Select(x => new CampaignRecipient(x.Id, x.UserId, x.PreferredLanguage, x.OutgoingLanguage, x.State, x.Attempts,
                x.AcceptedAt, x.ProviderId, x.Kind == MailKind.CampaignTest)).ToArrayAsync(ct);
        return new(Summary(campaign), counts, Math.Max(0, campaign.RecipientCount - counts.Values.Sum()), rows, total, page);
    }

    public async Task<int> RetryFailed(ClaimsPrincipal actor, Guid id, IReadOnlyCollection<Guid> deliveryIds, string reason,
        bool confirmed, CancellationToken ct = default)
    {
        await Authority.RequireAdministrator(db, actor, ct);
        if (!confirmed || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000 || deliveryIds.Count is 0 or > 500)
            throw new ValidationException("Select up to 500 failed deliveries and confirm the reason for retrying.");
        var ids = deliveryIds.Distinct().ToArray();
        var scheduler = await schedulers.GetScheduler(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        var campaign = await Find(id, ct);
        if (campaign.QueuedAt is null)
            throw new ValidationException("The campaign has not been queued.");
        var deliveries = await db.Deliveries.FromSqlInterpolated($"""
            SELECT * FROM "Deliveries"
            WHERE "CampaignId" = {id} AND "Id" = ANY({ids}) AND "State" = 'Failed'
                AND "UserId" IS NOT NULL AND "Kind" IN ('Newsletter', 'OfficialNotice')
            ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
        foreach (var delivery in deliveries)
        {
            delivery.QueueVersion++;
            delivery.State = DeliveryState.Pending;
            delivery.StartedAt = null;
            delivery.AttemptLimit = delivery.Attempts + MailQueue.AttemptsPerRound;
            db.MailDeliveryRetries.Add(new()
            {
                DeliveryId = delivery.Id, QueueVersion = delivery.QueueVersion, ActorId = Authority.Id(actor),
                Reason = reason.Trim(), RequestedAt = clock.GetUtcNow()
            });
        }
        await db.SaveChangesAsync(ct);
        await MailQueue.ScheduleBatch(scheduler, deliveries, ct);
        await tx.CommitAsync(ct);
        return deliveries.Count;
    }

    internal async Task<OutgoingMail?> RenderDelivery(Delivery delivery, User? user, NewsletterPreferences preferences, CancellationToken ct)
    {
        if (delivery.Kind == MailKind.CampaignTest)
        {
            return delivery.ProtectedTestMessage is { } payload && delivery.CreatedAt.AddDays(1) > clock.GetUtcNow()
                ? JsonSerializer.Deserialize<OutgoingMail>(TestMessages.Unprotect(payload)) : null;
        }
        if (user?.Email is null || !user.MayLogIn || !user.EmailConfirmed ||
            (delivery.Kind == MailKind.Newsletter && !user.EmailNewsletters) ||
            !await db.Members.AnyAsync(x => x.UserId == user.Id, ct))
            return null;
        var campaign = await Find(delivery.CampaignId!.Value, ct);
        var variant = RichMail.Read(campaign.Content).Single(x => x.Language == delivery.OutgoingLanguage);
        var unsubscribe = campaign.Kind == CampaignKind.Newsletter ? preferences.UnsubscribeLink(user) : null;
        var content = Render(campaign.Kind, variant, unsubscribe);
        return new(user.Email, content.Subject, content.Text, Html: content.Html, UnsubscribeUrl: unsubscribe);
    }

    private async Task<SendPlan> Plan(MailCampaign campaign, IReadOnlyDictionary<string, string> fallbacks, CancellationToken ct)
    {
        var variants = RichMail.Read(campaign.Content).ToDictionary(x => x.Language, StringComparer.Ordinal);
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var choice in fallbacks)
        {
            var preferred = UiLanguages.Find(choice.Key)?.Code ?? choice.Key;
            var outgoing = UiLanguages.Find(choice.Value)?.Code;
            if (outgoing is null || !variants.ContainsKey(outgoing) || variants.ContainsKey(preferred) || !choices.TryAdd(preferred, outgoing))
                throw new ValidationException("Choose one saved variant per missing language.");
        }

        var audience = await (from member in db.Members.AsNoTracking()
                              join user in db.Users.AsNoTracking() on member.UserId equals user.Id
                              where user.Admitted && user.EmailConfirmed && user.Email != null && user.PendingDeletionAt == null
                                  && (campaign.Kind == CampaignKind.OfficialNotice || user.EmailNewsletters)
                              orderby user.Id
                              select new { user.Id, user.Language }).ToListAsync(ct);
        var recipients = audience.Select(user =>
        {
            var preferred = UiLanguages.Find(user.Language)?.Code ?? user.Language;
            return new Recipient(user.Id, preferred, variants.ContainsKey(preferred) ? preferred : choices.GetValueOrDefault(preferred));
        }).ToArray();
        var groups = recipients.GroupBy(x => x.PreferredLanguage).OrderBy(x => x.Key, StringComparer.Ordinal).Select(group =>
        {
            var language = group.First().OutgoingLanguage;
            return new CampaignLanguageGroup(group.Key, group.Count(), language,
                language is null ? null : Render(campaign.Kind, variants[language]));
        }).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { campaign.Revision, recipients })));
        return new(recipients, groups, fingerprint);
    }

    private Confirmation? ReadConfirmation(string confirmation)
    {
        if (string.IsNullOrEmpty(confirmation) || confirmation.Length > 2048)
            return null;
        try { return JsonSerializer.Deserialize<Confirmation>(Confirmations.Unprotect(confirmation)); }
        catch (CryptographicException) { return null; }
    }

    private static MailContent Render(CampaignKind kind, MailVariant variant, string? unsubscribe = null)
    {
        var content = RichMail.Render(variant);
        return kind == CampaignKind.Newsletter ? RichMail.WithUnsubscribe(content, variant.Language, unsubscribe) : content;
    }

    private async Task<MailCampaign> Find(Guid id, CancellationToken ct)
        => await db.MailCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Campaign does not exist.");

    private static void RequireDraft(MailCampaign campaign)
    {
        if (campaign.QueuedAt is not null)
            throw new ValidationException("This campaign has been queued. Copy it to write a new draft.");
    }

    private static CampaignSummary Summary(MailCampaign x) => new(x.Id, x.Revision, x.Title, x.Kind, x.UpdatedAt, x.QueuedAt, x.RecipientCount);
    private static int Offset(int page) => (Math.Clamp(page, 1, int.MaxValue / PageSize) - 1) * PageSize;

    private sealed record Confirmation(Guid CampaignId, long ActorId, string Fingerprint);
    private sealed record Recipient(long UserId, string PreferredLanguage, string? OutgoingLanguage);
    private sealed record SendPlan(Recipient[] Recipients, CampaignLanguageGroup[] Groups, string Fingerprint);
}
