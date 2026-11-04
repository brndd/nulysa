using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Communications;

public sealed class MailRenderer(AppDbContext db, UserManager<User> users, MembershipCommands membership, AssociationClock clock,
    IOptions<MailOptions> mailOptions, IOptions<MembershipOptions> membershipOptions, InvoicePdf pdf, IOptions<BillingOptions> billingOptions, Campaigns campaigns, NewsletterPreferences preferences, OperationalTemplates templates)
{
    public async Task<OutgoingMail?> Render(Delivery delivery, CancellationToken ct)
    {
        string email;
        Dictionary<string, string?> fields;
        var application = delivery.ApplicationId is { } applicationId
            ? await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct) : null;
        var user = delivery.UserId is { } userId ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct) : null;
        if (delivery.CampaignId is not null)
            return await campaigns.RenderDelivery(delivery, user, preferences, ct);

        var language = application?.Language ?? user?.Language ?? membershipOptions.Value.DefaultLanguage;

        if (delivery.Kind == MailKind.Verification)
        {
            if (application is null || application.VerifiedAt is not null || application.RejectedAt is not null ||
                application.VerificationVersion != delivery.Version || application.CreatedAt.AddHours(membershipOptions.Value.UnverifiedRetentionHours) <= clock.Now)
                return null;
            email = application.Email;
            var link = Link("apply/verify", new() { ["id"] = application.Id.ToString(), ["token"] = membership.VerificationToken(application) });
            fields = new()
            {
                ["verification_url"] = link,
                ["verification_expires_at"] = LocalTime(application.CreatedAt.AddHours(membershipOptions.Value.UnverifiedRetentionHours))
            };
        }
        else if (delivery.Kind == MailKind.Rejection)
        {
            if (application?.RejectedAt is null)
                return null;
            email = application.Email;
            fields = new() { ["rejection_reason"] = application.RejectionExplanation };
        }
        else
        {
            if (user?.Email is null || !user.Admitted || !user.EmailConfirmed)
                return null;
            email = user.Email;

            if (delivery.Kind == MailKind.Invoice)
            {
                if (!user.MayLogIn || delivery.InvoiceId is not { } invoiceId)
                    return null;

                var account = await db.BillingAccounts.AsNoTracking().SingleAsync(x => x.UserId == user.Id, ct);
                var invoice = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == invoiceId && x.BillingAccountId == account.Id, ct);
                if (!account.EmailInvoices || invoice is null || invoice.ClosedAt is not null || invoice.Revision != delivery.InvoiceRevision)
                    return null;

                var attachment = await pdf.Render(invoiceId, delivery.InvoiceRevision, ct);
                if (attachment is null)
                    return null;
                return await templates.Render(delivery.Kind, language, email, new()
                {
                    ["invoice_number"] = invoiceId.ToString(CultureInfo.InvariantCulture),
                    ["billing_url"] = Link("billing", new())
                }, ct, attachment, $"invoice-{invoiceId}.pdf");
            }
            else if (delivery.Kind is MailKind.MembershipGrace or MailKind.MembershipInactive)
            {
                if (!user.MayLogIn)
                    return null;

                var account = await db.BillingAccounts.SingleAsync(x => x.UserId == user.Id, ct);
                var standing = (await BillingFacts.Load(db, account.Id, ct, billingOptions.Value.MembershipRenewalGraceDays)).Membership(clock.Today);
                if (delivery.Kind == MailKind.MembershipInactive && standing.Active)
                    return null;
                if (delivery.Kind == MailKind.MembershipGrace && (!standing.Active || !standing.IsMembershipGrace))
                    return null;

                fields = delivery.Kind == MailKind.MembershipInactive
                    ? new() { ["services_url"] = Link("services", new()) }
                    : new()
                    {
                        ["benefits_until"] = standing.ActiveUntilExclusive?.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["billing_url"] = Link("billing", new())
                    };
            }
            else if (delivery.Kind == MailKind.BankAlert)
            {
                if (!user.MayLogIn || !await Authority.IsAdministrator(db, new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString())], "mail")), ct))
                    return null;

                fields = new() { ["payments_url"] = Link("admin/payments", new()) };
            }
            else if (delivery.Kind == MailKind.Restoration)
            {
                if (user.PendingDeletionAt is not null)
                    return null;
                fields = [];
            }
            else if (delivery.Kind == MailKind.PasswordChanged)
            {
                if (!user.MayLogIn)
                    return null;

                fields = new()
                {
                    ["password_changed_at"] = LocalTime(delivery.CreatedAt),
                    ["password_reset_url"] = Link("Account/ForgotPassword", new())
                };
            }
            else if (delivery.Kind == MailKind.DeletionWarning)
            {
                var member = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
                if (user.PendingDeletionAt is null || member?.DeletionOperation != delivery.Version || member.DeletionEligibleAt is not { } eligibleAt)
                    return null;

                var eligible = TimeZoneInfo.ConvertTime(eligibleAt, clock.Zone);
                var account = await db.BillingAccounts.SingleAsync(x => x.UserId == user.Id, ct);
                var balances = (await BillingFacts.Load(db, account.Id, ct)).Balances;
                var unspent = balances.BillingCredit + balances.PurchaseBalance + balances.Held;
                fields = new()
                {
                    ["deletion_eligible_at"] = LocalTime(eligibleAt),
                    ["deletion_window_start"] = eligible.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["deletion_window_end"] = eligible.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["unspent_funds"] = unspent > 0 ? unspent.ToString("0.00", CultureInfo.InvariantCulture) + " EUR" : null
                };
            }
            else
            {
                if (!user.MayLogIn || (delivery.Kind != MailKind.Welcome && delivery.CreatedAt.AddDays(1) <= clock.Now))
                    return null;
                if (delivery.Kind == MailKind.EmailChange)
                {
                    if (user.PendingEmail is null || user.PendingEmailVersion != delivery.Version || user.PendingEmailRequestedAt?.AddDays(1) <= clock.Now)
                        return null;

                    email = user.PendingEmail;
                    var token = await users.GenerateChangeEmailTokenAsync(user, email);
                    var link = Link("Account/ConfirmEmail", new() { ["id"] = user.Id.ToString(), ["version"] = delivery.Version.ToString(), ["token"] = token });
                    fields = new() { ["confirmation_url"] = link };
                }
                else
                {
                    var token = await users.GeneratePasswordResetTokenAsync(user);
                    var link = Link("Account/SetPassword", new() { ["id"] = user.Id.ToString(), ["token"] = token });
                    fields = new() { ["password_url"] = link };
                    if (delivery.Kind == MailKind.Welcome)
                        fields.Add("billing_url", Link("billing", new()));
                }
            }
        }
        return await templates.Render(delivery.Kind, language, email, fields, ct);
    }

    private string LocalTime(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, clock.Zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    private string Link(string path, Dictionary<string, string?> values)
        => QueryHelpers.AddQueryString(new Uri(new Uri(mailOptions.Value.PublicUrl), path).AbsoluteUri, values);
}
