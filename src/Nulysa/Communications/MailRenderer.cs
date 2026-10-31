using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Data;
using Nulysa.Membership;

namespace Nulysa.Communications;

public sealed class MailRenderer(AppDbContext db, UserManager<User> users, MembershipCommands membership, AssociationClock clock,
    IOptions<MailOptions> mailOptions, IOptions<MembershipOptions> membershipOptions, IConfiguration config)
{
    public async Task<OutgoingMail?> Render(Delivery delivery, CancellationToken ct)
    {
        string email, language, text, subject;
        var application = delivery.ApplicationId is { } applicationId
            ? await db.Applications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId, ct) : null;
        var user = delivery.UserId is { } userId ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct) : null;
        language = application?.Language ?? user?.Language ?? membershipOptions.Value.DefaultLanguage;
        if (language is not ("en" or "fi")) language = membershipOptions.Value.DefaultLanguage;
        var fi = language == "fi";
        if (delivery.Kind == MailKind.Verification)
        {
            if (application is null || application.VerifiedAt is not null || application.RejectedAt is not null ||
                application.VerificationVersion != delivery.Version || application.CreatedAt.AddHours(membershipOptions.Value.UnverifiedRetentionHours) <= clock.Now) return null;
            email = application.Email;
            var link = Link("apply/verify", new() { ["id"] = application.Id.ToString(), ["token"] = membership.VerificationToken(application) });
            subject = fi ? "Vahvista jäsenhakemuksesi" : "Verify your membership application";
            text = fi ? $"Vahvista sähköpostiosoitteesi: {link}\nHakemus ei vielä anna kirjautumisoikeutta. Linkki vanhenee hakemuksen säilytysajan päättyessä."
                : $"Verify your email: {link}\nVerification does not grant login access. This link expires when the application's retention period ends.";
        }
        else if (delivery.Kind == MailKind.Rejection)
        {
            if (application?.RejectedAt is null) return null;
            email = application.Email;
            subject = fi ? "Jäsenhakemuksen päätös" : "Membership application decision";
            text = (fi ? "Jäsenhakemustasi ei hyväksytty." : "Your membership application was not approved.") + "\n" + application.RejectionExplanation;
        }
        else
        {
            if (user?.Email is null || !user.Admitted || !user.EmailConfirmed) return null;
            email = user.Email;
            if (delivery.Kind == MailKind.Restoration)
            {
                if (user.PendingDeletionAt is not null) return null;
                subject = fi ? "Tilin poisto peruttu" : "Pending deletion cancelled";
                text = fi ? "Hallinto peruutti tilisi poiston. Kirjautuminen ja jäsenpalvelut ovat jälleen käytössä. Poisto ei palauta laskutusta, kattavuutta tai tunnuksia. Ota yhteyttä hallitukseen, jos et pyytänyt tätä."
                    : "An administrator cancelled the pending deletion of your account. Login and member services are available again. Restoration does not reopen billing, coverage or credentials. Contact the board if you did not request this.";
            }
            else if (delivery.Kind == MailKind.DeletionWarning)
            {
                var member = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == user.Id, ct);
                if (user.PendingDeletionAt is null || member?.DeletionOperation != delivery.Version || member.DeletionEligibleAt is not { } eligibleAt) return null;
                var eligible = TimeZoneInfo.ConvertTime(eligibleAt, clock.Zone);
                subject = fi ? "Jäsenyyden päättyminen ja tietojen poisto" : "Membership ended: account deletion warning";
                text = fi ? $"Kirjautuminen ja jäsenpalvelut on estetty. Tilisi voidaan poistaa aikaisintaan {eligible:yyyy-MM-dd HH:mm zzz}. Poistoerä ajetaan 14 päivän välein, joten poisto tapahtuu tavallisesti {eligible:yyyy-MM-dd}–{eligible.AddDays(14):yyyy-MM-dd}, häiriöissä myöhemmin. Ota yhteys hallitukseen, jos haluat perua poiston."
                    : $"Login and member services are blocked immediately. Your account becomes eligible for deletion on {eligible:yyyy-MM-dd HH:mm zzz}. Batches run every 14 days: deletion is normally {eligible:yyyy-MM-dd}–{eligible.AddDays(14):yyyy-MM-dd}, possibly later after failures. Contact an administrator to request restoration before actual deletion.";
            }
            else
            {
                if (!user.MayLogIn || (delivery.Kind != MailKind.Welcome && delivery.CreatedAt.AddDays(1) <= clock.Now)) return null;
                if (delivery.Kind == MailKind.EmailChange)
                {
                    if (user.PendingEmail is null || user.PendingEmailVersion != delivery.Version || user.PendingEmailRequestedAt?.AddDays(1) <= clock.Now) return null;
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
                        text += fi ? "\nJäsenyys alkaa hyväksymisestä. Alkulaskutus odottaa S2-laskutusintegraatiota; laskua ei ole vielä luotu."
                            : "\nAdmission starts your membership. Initial billing is pending the S2 integration; no invoice has been issued yet.";
                }
            }
        }
        // Trusted plain-text templates only. Rich editable authoring/sanitisation belongs to S3.
        return new(email, (config["Association:Name"] ?? "Nulysa") + " — " + subject, text);
    }

    private string Link(string path, Dictionary<string, string?> values)
        => QueryHelpers.AddQueryString(new Uri(new Uri(mailOptions.Value.PublicUrl), path).AbsoluteUri, values);
}
