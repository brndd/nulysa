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
    IOptions<MailOptions> mailOptions, IOptions<MembershipOptions> membershipOptions, IConfiguration config, InvoicePdf pdf, IOptions<BillingOptions> billingOptions)
{
    public async Task<OutgoingMail?> Render(Delivery delivery, CancellationToken ct)
    {
        string email, language, text, subject;
        var application = delivery.ApplicationId is { } applicationId
            ? await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct) : null;
        var user = delivery.UserId is { } userId ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct) : null;
        language = application?.Language ?? user?.Language ?? membershipOptions.Value.DefaultLanguage;
        if (language is not ("en" or "fi"))
            language = membershipOptions.Value.DefaultLanguage;
        var fi = language == "fi";

        if (delivery.Kind == MailKind.Verification)
        {
            if (application is null || application.VerifiedAt is not null || application.RejectedAt is not null ||
                application.VerificationVersion != delivery.Version || application.CreatedAt.AddHours(membershipOptions.Value.UnverifiedRetentionHours) <= clock.Now)
                return null;
            email = application.Email;
            var link = Link("apply/verify", new() { ["id"] = application.Id.ToString(), ["token"] = membership.VerificationToken(application) });
            subject = fi ? "Vahvista jäsenhakemuksesi" : "Verify your membership application";
            text = fi ? $"Vahvista sähköpostiosoitteesi: {link}\nHakemus ei vielä anna kirjautumisoikeutta. Linkki vanhenee hakemuksen säilytysajan päättyessä."
                : $"Verify your email: {link}\nVerification does not grant login access. This link expires when the application's retention period ends.";
        }
        else if (delivery.Kind == MailKind.Rejection)
        {
            if (application?.RejectedAt is null)
                return null;
            email = application.Email;
            subject = fi ? "Jäsenhakemuksen päätös" : "Membership application decision";
            text = (fi ? "Jäsenhakemustasi ei hyväksytty." : "Your membership application was not approved.") + "\n" + application.RejectionExplanation;
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
                return new(email, (config["Association:Name"] ?? "Nulysa") + (fi ? " — Maksupyyntö " : " — Payment request ") + invoiceId,
                    (fi ? "Ennakkomaksupyyntö on liitteenä. Ajantasaiset maksutiedot: " : "Your advance payment request is attached. Current funding and remaining amount: ") + Link("billing", new()), attachment, $"invoice-{invoiceId}.pdf");
            }
            else if (delivery.Kind is MailKind.MembershipGrace or MailKind.MembershipInactive)
            {
                if (!user.MayLogIn)
                    return null;

                var account = await db.BillingAccounts.SingleAsync(x => x.UserId == user.Id, ct);
                var standing = (await BillingFacts.Load(db, account.Id, ct, billingOptions.Value.MembershipRenewalGraceDays)).Membership(clock.Today);
                if (delivery.Kind == MailKind.MembershipInactive && standing.Active)
                    return null;
                if (delivery.Kind == MailKind.MembershipGrace && (!standing.Active || standing.Description != "Active during grace"))
                    return null;

                subject = delivery.Kind == MailKind.MembershipInactive ? (fi ? "Jäsenpalvelut keskeytetty" : "Membership benefits paused") : (fi ? "Jäsenmaksun lisäaika" : "Membership payment grace");
                text = (delivery.Kind == MailKind.MembershipInactive
                    ? (fi ? "Jäsenmaksun määräaika päättyi. Jäsenyys jatkuu, mutta jäsenpalvelut ja jäsenlaskutus on keskeytetty. Voit palata maksamalla uuden paluujakson. " : "Your membership payment grace ended. You remain a member; benefits and membership billing are paused. Request a return period to resume. ")
                    : (fi ? $"Jäsenmaksu on maksamatta. Jäsenpalvelut jatkuvat {standing.Until:yyyy-MM-dd} asti. " : $"Your membership payment is overdue. Benefits remain active until {standing.Until:yyyy-MM-dd}. ")) + Link("billing", new());
            }
            else if (delivery.Kind == MailKind.BankAlert)
            {
                if (!user.MayLogIn || !await Authority.IsAdministrator(db, new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id.ToString())], "mail")), ct))
                    return null;

                subject = fi ? "Pankkisynkronointi vaatii huomiota" : "Bank import requires attention";
                text = (fi ? "Pankkisyöte on epäonnistunut, vanhentunut tai vaatii uuden valtuutuksen. Tarkista maksujen hallinta: " : "The bank feed failed, is stale or needs renewed authorization. Check payment administration: ") + Link("admin/payments", new());
            }
            else if (delivery.Kind == MailKind.Restoration)
            {
                if (user.PendingDeletionAt is not null)
                    return null;
                subject = fi ? "Tilin poisto peruttu" : "Pending deletion cancelled";
                text = fi ? "Hallinto peruutti tilisi poiston. Kirjautuminen ja jäsenpalvelut ovat jälleen käytössä. Poisto ei palauta laskutusta, kattavuutta tai tunnuksia. Ota yhteyttä hallitukseen, jos et pyytänyt tätä."
                    : "An administrator cancelled the pending deletion of your account. Login and member services are available again. Restoration does not reopen billing, coverage or credentials. Contact the board if you did not request this.";
            }
            else if (delivery.Kind == MailKind.DeletionWarning)
            {
                var member = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
                if (user.PendingDeletionAt is null || member?.DeletionOperation != delivery.Version || member.DeletionEligibleAt is not { } eligibleAt)
                    return null;

                var eligible = TimeZoneInfo.ConvertTime(eligibleAt, clock.Zone);
                subject = fi ? "Jäsenyyden päättyminen ja tietojen poisto" : "Membership ended: account deletion warning";
                text = fi ? $"Kirjautuminen ja jäsenpalvelut on estetty. Tilisi voidaan poistaa aikaisintaan {eligible:yyyy-MM-dd HH:mm zzz}. Poistoerä ajetaan 14 päivän välein, joten poisto tapahtuu tavallisesti {eligible:yyyy-MM-dd}–{eligible.AddDays(14):yyyy-MM-dd}, häiriöissä myöhemmin. Ota yhteys hallitukseen, jos haluat perua poiston."
                    : $"Login and member services are blocked immediately. Your account becomes eligible for deletion on {eligible:yyyy-MM-dd HH:mm zzz}. Batches run every 14 days: deletion is normally {eligible:yyyy-MM-dd}–{eligible.AddDays(14):yyyy-MM-dd}, possibly later after failures. Contact an administrator to request restoration before actual deletion.";
                var account = await db.BillingAccounts.SingleAsync(x => x.UserId == user.Id, ct);
                var balances = (await BillingFacts.Load(db, account.Id, ct)).Balances;
                if (balances.BillingCredit + balances.PurchaseBalance + balances.Held > 0)
                    text += fi ? $"\nKäyttämättömiä varoja: {balances.BillingCredit + balances.PurchaseBalance + balances.Held:0.00} EUR. Ota yhteyttä rahastonhoitajaan palautusta varten. Tilin poisto ei poista saatavaasi."
                        : $"\nUnspent funds: {balances.BillingCredit + balances.PurchaseBalance + balances.Held:0.00} EUR. Contact the treasurer for a refund. Scheduled account deletion does not forfeit your claim.";
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
                    subject = fi ? "Vahvista uusi sähköpostiosoite" : "Confirm your new email address";
                    text = fi ? $"Vahvista uusi osoitteesi: {link}\nNykyinen osoite toimii vahvistukseen asti. Linkki on voimassa enintään vuorokauden."
                        : $"Confirm your new email address: {link}\nYour current address remains in use until confirmation. This link is valid for at most one day.";
                }
                else
                {
                    var token = await users.GeneratePasswordResetTokenAsync(user);
                    var link = Link("Account/SetPassword", new() { ["id"] = user.Id.ToString(), ["token"] = token });
                    subject = delivery.Kind == MailKind.Welcome ? (fi ? "Tervetuloa jäseneksi" : "Welcome to membership")
                        : (fi ? "Salasanan palautus" : "Reset your password");
                    text = fi ? $"Aseta salasana: {link}\nLinkki on voimassa vuorokauden."
                        : $"Set your password: {link}\nThis link is valid for one day.";
                    if (delivery.Kind == MailKind.Welcome)
                        text += fi ? "\nJäsenyys alkaa hyväksymisestä. Alkulasku lähetetään erikseen ja löytyy portaalista: " + Link("billing", new())
                            : "\nAdmission starts your membership. Your initial invoice is sent separately and is available in the portal: " + Link("billing", new());
                }
            }
        }
        // Trusted plain-text templates only. Rich editable authoring/sanitisation belongs to S3.
        return new(email, (config["Association:Name"] ?? "Nulysa") + " — " + subject, text);
    }

    private string Link(string path, Dictionary<string, string?> values)
        => QueryHelpers.AddQueryString(new Uri(new Uri(mailOptions.Value.PublicUrl), path).AbsoluteUri, values);
}
