using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Nulysa.Accounts;
using Nulysa.Billing;
using Nulysa.Data;
using Nulysa.Membership;
using Quartz;

namespace Nulysa.Development;

public sealed record SeededAccount(string Email, string Password, string Role, string Scenario, long UserId, long AccountId);
public sealed record SeededApplicant(string Email, string Status, bool Under18);
public sealed record SeededReceipt(string Reference, decimal Amount, string State, long Id);

public sealed record SeedSummary(
    bool AlreadySeeded,
    string AdminEmail,
    string Password,
    long DoorServiceId,
    long DoorFeeId,
    long TrailerServiceId,
    long TrailerFeeId,
    IReadOnlyList<SeededAccount> Accounts,
    IReadOnlyList<SeededApplicant> Applicants,
    IReadOnlyList<SeededReceipt> Receipts,
    Reconciliation? Reconciliation)
{
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(AlreadySeeded
            ? "Development seed data already exists; nothing was changed."
            : "Development data seeded.");
        text.AppendLine();
        text.AppendLine($"Login: {AdminEmail} / {Password}");
        text.AppendLine();

        foreach (var account in Accounts)
            text.AppendLine($"{account.Email} / {account.Password}  {account.Role}  {account.Scenario}  (user #{account.UserId}, account #{account.AccountId})");

        if (Applicants.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Applicants:");
            foreach (var applicant in Applicants)
                text.AppendLine($"{applicant.Email}  {applicant.Status}{(applicant.Under18 ? " (under 18)" : "")}");
        }

        if (Receipts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Payment review data:");
            foreach (var receipt in Receipts)
                text.AppendLine($"#{receipt.Id}  {receipt.Reference}  {receipt.Amount:0.00} EUR  {receipt.State}");
        }

        if (DoorServiceId > 0)
            text.AppendLine($"{Environment.NewLine}Door access: service #{DoorServiceId}, fee #{DoorFeeId}, 10.00 EUR/month.");
        if (TrailerServiceId > 0)
            text.AppendLine($"Trailer access: service #{TrailerServiceId}, fee #{TrailerFeeId}, 5.00 EUR/month, self-subscribable.");
        if (Reconciliation is { } reconciliation)
            text.AppendLine($"Billing reconciliation: difference {reconciliation.Difference:0.00}, invalid lines {reconciliation.InvalidLines.Count}, invalid receipts {reconciliation.InvalidReceipts.Count}.");

        return text.ToString();
    }
}

public sealed class DevelopmentSeed(
    AppDbContext db,
    UserManager<User> users,
    RoleManager<IdentityRole<long>> roles,
    MembershipCommands membership,
    BillingCommands billing,
    BillingQueries queries,
    ISchedulerFactory schedulers,
    AssociationClock association,
    TimeProvider time,
    IOptions<BillingOptions> options,
    IConfiguration configuration)
{
    public const string DefaultPassword = "P4ssword!";
    public const string AdminEmail = "admin@seed.nulysa.test";
    public const string BoardEmail = "board@seed.nulysa.test";
    public const string SysopEmail = "sysop@seed.nulysa.test";

    private IScheduler scheduler = null!;
    private ClaimsPrincipal adminActor = null!;
    private long adminId;
    private DateOnly seedToday;

    private DevelopmentClock Clock => time as DevelopmentClock
        ?? throw new InvalidOperationException("Development data seeding requires the mutable development clock.");
    private BillingOptions Settings => options.Value;
    private string SeedPassword => string.IsNullOrWhiteSpace(configuration["DevelopmentSeed:Password"])
        ? DefaultPassword
        : configuration["DevelopmentSeed:Password"]!;

    public async Task<SeedSummary> Run(CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(x => x.Email == AdminEmail, ct))
            return await AlreadySeededSummaryAsync(ct);

        scheduler = await schedulers.GetScheduler(ct);
        var today = association.Today;
        seedToday = today;
        var end = At(today, 18);
        var original = Clock.Now;
        Clock.Now = end;
        try
        {
            await EnsureMembershipPriceAsync(ct);
            var admin = await CreateAdminAsync(today, ct);
            var door = await CreateServiceAsync("door", "Door access", "door", false, 14, 7, 10, 10m, ct);
            var trailer = await CreateServiceAsync("trailer", "Trailer access", "trailer", true, 0, 7, 20, 5m, ct);

            var accounts = new List<SeededAccount>
            {
                new(AdminEmail, SeedPassword, "Administrator + Superuser", "Paid active member", admin.User.Id, admin.Account.Id)
            };
            var board = await CreateBoardAsync(today, ct);
            accounts.Add(new(BoardEmail, SeedPassword, "Administrator", "Paid active member", board.User.Id, board.Account.Id));
            var sysop = await CreateSysopAsync(today, ct);
            accounts.Add(new(SysopEmail, SeedPassword, "Superuser", "Paid active member", sysop.User.Id, sysop.Account.Id));

            var accountIds = new List<long> { admin.Account.Id, board.Account.Id, sysop.Account.Id };
            foreach (var definition in Definitions(today))
            {
                var (user, account) = await CreateMemberAsync(definition, adminId, ct);
                await ApplyScenarioAsync(definition, user, account, door.ServiceId, trailer.ServiceId, ct);
                accountIds.Add(account.Id);
                accounts.Add(new(definition.Email, SeedPassword, "Member", ScenarioText(definition.Scenario), user.Id, account.Id));
            }

            var applicants = await SeedApplicantsAsync(today, ct);
            var receipts = await SeedReviewReceiptsAsync(today, ct);

            foreach (var accountId in accountIds)
            {
                Clock.Now = end;
                await billing.Scan(accountId, ct);
            }

            var reconciliation = await queries.Reconcile(adminActor, ct);
            var isReconciled = reconciliation.Difference == 0
                && reconciliation.InvalidLines.Count == 0
                && reconciliation.InvalidReceipts.Count == 0;
            if (!isReconciled)
                throw new InvalidOperationException("Development seed billing reconciliation failed.");

            return new(false, AdminEmail, SeedPassword, door.ServiceId, door.FeeId, trailer.ServiceId, trailer.FeeId,
                accounts, applicants, receipts, reconciliation);
        }
        finally
        {
            Clock.Now = original;
        }
    }

    private async Task<SeedSummary> AlreadySeededSummaryAsync(CancellationToken ct)
    {
        var known = new List<(string Email, string Role, string Scenario)>
        {
            (AdminEmail, "Administrator + Superuser", "Paid active member"),
            (BoardEmail, "Administrator", "Paid active member"),
            (SysopEmail, "Superuser", "Paid active member")
        };
        known.AddRange(Definitions(association.Today).Select(x => (x.Email, "Member", ScenarioText(x.Scenario))));
        var emails = known.Select(x => x.Email).ToArray();
        var existing = await db.Users.AsNoTracking().Where(x => x.Email != null && emails.Contains(x.Email)).ToListAsync(ct);
        var accountIds = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.UserId != null)
            .ToDictionaryAsync(x => x.UserId!.Value, x => x.Id, ct);
        var accounts = known
            .Select(x => (x, user: existing.SingleOrDefault(u => u.Email == x.Email)))
            .Where(x => x.user is not null)
            .Select(x => new SeededAccount(x.x.Email, SeedPassword, x.x.Role, x.x.Scenario, x.user!.Id, accountIds.GetValueOrDefault(x.user.Id)))
            .ToList();
        return new(true, AdminEmail, SeedPassword, 0, 0, 0, 0, accounts, [], [], null);
    }

    private async Task EnsureMembershipPriceAsync(CancellationToken ct)
    {
        if (await db.FeePrices.AnyAsync(x => x.FeeDefinitionId == 1, ct))
            return;
        db.FeePrices.Add(new FeePrice { FeeDefinitionId = 1, EffectiveOn = new DateOnly(2000, 1, 1), MonthlyAmount = 10m });
        await db.SaveChangesAsync(ct);
    }

    private async Task<(User User, BillingAccount Account)> CreateAdminAsync(DateOnly today, CancellationToken ct)
    {
        var admin = await CreateMemberAsync(
            new SeedMemberDefinition(AdminEmail, "Seed Administrator", "en", today, SeedScenario.Active, today.AddYears(-40),
                "admin", "+358 40 000 0000", "@admin:seed.test", "Helsinki"),
            null, ct);
        adminId = admin.User.Id;
        adminActor = Actor(adminId);
        await Superusers.Set(db, users, roles, adminId, true);
        await Superusers.Set(db, users, roles, adminId, true, "Administrator");
        await PayGroupAsync(admin.Account.Id, AdminEmail, 10m, today, "admin-initial", ct);
        return admin;
    }

    private async Task<(User User, BillingAccount Account)> CreateBoardAsync(DateOnly today, CancellationToken ct)
    {
        var board = await CreateMemberAsync(
            new SeedMemberDefinition(BoardEmail, "Seed Board", "en", today, SeedScenario.Active, today.AddYears(-45),
                "board", "+358 40 000 0001", "@board:seed.test", "Helsinki"),
            adminId, ct);
        await Superusers.Set(db, users, roles, board.User.Id, true, "Administrator");
        await PayGroupAsync(board.Account.Id, BoardEmail, 10m, today, "board-initial", ct);
        return board;
    }

    private async Task<(User User, BillingAccount Account)> CreateSysopAsync(DateOnly today, CancellationToken ct)
    {
        var sysop = await CreateMemberAsync(
            new SeedMemberDefinition(SysopEmail, "Seed Sysop", "en", today, SeedScenario.Active, today.AddYears(-35),
                "sysop", "+358 40 000 0002", "@sysop:seed.test", "Helsinki"),
            adminId, ct);
        await Superusers.Set(db, users, roles, sysop.User.Id, true);
        await PayGroupAsync(sysop.Account.Id, SysopEmail, 10m, today, "sysop-initial", ct);
        return sysop;
    }

    private async Task<(User User, BillingAccount Account)> CreateMemberAsync(SeedMemberDefinition definition, long? approvedBy, CancellationToken ct)
    {
        SetClock(definition.JoinedOn);
        var user = new User
        {
            Email = definition.Email,
            UserName = definition.Email,
            EmailConfirmed = true,
            Admitted = true,
            Name = definition.Name,
            Municipality = definition.Municipality,
            DateOfBirth = definition.DateOfBirth,
            Language = definition.Language,
            PhoneNumber = definition.Phone,
            Nickname = definition.Nickname,
            MatrixId = definition.Matrix
        };
        MembershipCommands.Check(await users.CreateAsync(user, SeedPassword));

        db.Members.Add(new Member { UserId = user.Id, JoinedOn = definition.JoinedOn });
        var account = await BillingAccount.Create(db, user.Id, At(definition.JoinedOn), ct);
        account.EmailInvoices = false;
        db.BillingAccounts.Add(account);
        db.Approvals.Add(new Approval
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            AdministratorId = approvedBy ?? user.Id,
            ApprovedAt = At(definition.JoinedOn)
        });
        await db.SaveChangesAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        using var enlistment = scheduler.EnlistTransaction(tx.GetDbTransaction());
        await billing.Initialize(account, definition.JoinedOn, At(definition.JoinedOn), scheduler, ct);
        await tx.CommitAsync(ct);
        return (user, account);
    }

    private async Task<(long ServiceId, long FeeId)> CreateServiceAsync(string code, string name, string category, bool selfSubscribe,
        int freeDays, int graceDays, int priority, decimal price, CancellationToken ct)
    {
        var serviceId = await billing.ConfigureService(adminActor, null,
            new ServiceInput(code, name, selfSubscribe, freeDays, graceDays, priority, category), ct);
        var fee = await db.Fees.AsNoTracking().SingleAsync(x => x.ServiceDefinitionId == serviceId, ct);
        await billing.SetPrice(adminActor, fee.Id, new DateOnly(2000, 1, 1), price, ct);
        return (serviceId, fee.Id);
    }

    private async Task ApplyScenarioAsync(SeedMemberDefinition definition, User user, BillingAccount account, long doorId, long trailerId, CancellationToken ct)
    {
        switch (definition.Scenario)
        {
            case SeedScenario.Active:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "active-initial", ct);
                await billing.SetEmailInvoices(adminActor, account.Id, true, Guid.NewGuid(), ct);
                break;
            case SeedScenario.Door:
                await SubscribeAsync(account.Id, doorId, definition.JoinedOn, ct);
                await PayGroupAsync(account.Id, definition.Email, 80m, definition.JoinedOn, "door-paid", ct);
                await ScanAsync(account.Id, definition.JoinedOn.AddMonths(1), ct);
                await ScanAsync(account.Id, definition.JoinedOn.AddMonths(2), ct);
                break;
            case SeedScenario.DoorFree:
                await SubscribeAsync(account.Id, doorId, definition.JoinedOn, ct);
                break;
            case SeedScenario.DoorDue:
                await SubscribeAsync(account.Id, doorId, definition.JoinedOn, ct);
                await PayGroupAsync(account.Id, definition.Email, 20m, definition.JoinedOn, "door-due-initial", ct);
                await ScanAsync(account.Id, definition.JoinedOn.AddMonths(1), ct);
                await PayGroupAsync(account.Id, definition.Email, 20m, definition.JoinedOn.AddMonths(1), "door-due-renewal", ct);
                await ScanAsync(account.Id, definition.JoinedOn.AddMonths(2), ct);
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn.AddMonths(2), "door-due-membership-only", ct);
                break;
            case SeedScenario.Grace:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "grace-initial", ct);
                break;
            case SeedScenario.Inactive:
            case SeedScenario.OldInactive:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "inactive-initial", ct);
                break;
            case SeedScenario.New:
                break;
            case SeedScenario.Partial:
                await PayGroupAsync(account.Id, definition.Email, 5m, definition.JoinedOn, "partial-initial", ct);
                break;
            case SeedScenario.Credit:
                await PayGroupAsync(account.Id, definition.Email, 25m, definition.JoinedOn, "credit-initial", ct);
                break;
            case SeedScenario.Deposit:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "deposit-initial", ct);
                if (Settings.PurchaseBalanceEnabled)
                {
                    await PayAsync(definition.Email, account.PurchaseDepositReference, 20m, definition.JoinedOn, "deposit-account", ct);
                    await billing.Transfer(adminActor, account.Id, Guid.NewGuid(), 5m, false, ct);
                }
                break;
            case SeedScenario.Honorary:
                await AddDiscountAsync(account.Id, definition.JoinedOn, 100m, ct);
                break;
            case SeedScenario.Discount:
                await AddDiscountAsync(account.Id, definition.JoinedOn, 50m, ct);
                await PayGroupAsync(account.Id, definition.Email, 5m, definition.JoinedOn, "discount-initial", ct);
                break;
            case SeedScenario.Granted:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "granted-initial", ct);
                SetClock(definition.JoinedOn);
                await billing.GrantMembershipMonths(adminActor, account.Id, 2, Guid.NewGuid(), "Synthetic development grant", ct);
                break;
            case SeedScenario.OneOff:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "oneoff-initial", ct);
                SetClock(definition.JoinedOn);
                await billing.OneOff(adminActor, account.Id, Guid.NewGuid(), "Synthetic workshop purchase", "workshop", 5m, seedToday.AddDays(14), ct);
                break;
            case SeedScenario.OneOffPaid:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "oneoff-paid-initial", ct);
                await PayOneOffAsync(account.Id, definition.Email, definition.JoinedOn, ct);
                break;
            case SeedScenario.Stopped:
            case SeedScenario.Removed:
                {
                    var enrollmentId = await SubscribeAsync(account.Id, doorId, definition.JoinedOn, ct);
                    await PayGroupAsync(account.Id, definition.Email, 80m, definition.JoinedOn, "service-stop-paid", ct);
                    await ScanAsync(account.Id, definition.JoinedOn.AddMonths(1), ct);
                    await ScanAsync(account.Id, definition.JoinedOn.AddMonths(2), ct);
                    await ScanAsync(account.Id, seedToday, ct);
                    SetClock(seedToday);
                    if (enrollmentId is { } id)
                        await billing.StopService(adminActor, account.Id, id, definition.Scenario == SeedScenario.Removed,
                            Guid.NewGuid(), "Synthetic development service change", ct);
                    break;
                }
            case SeedScenario.Pending:
                await PayGroupAsync(account.Id, definition.Email, 20m, definition.JoinedOn, "pending-initial", ct);
                SetClock(seedToday);
                await membership.BeginDeletion(adminActor, user.Id, Guid.NewGuid(), "Synthetic development termination", ct);
                break;
            case SeedScenario.Resigned:
                await PayGroupAsync(account.Id, definition.Email, 20m, definition.JoinedOn, "resigned-initial", ct);
                SetClock(seedToday);
                await membership.BeginDeletion(Actor(user.Id), user.Id, Guid.NewGuid(), "Synthetic development resignation", ct);
                break;
            case SeedScenario.Restored:
                await PayGroupAsync(account.Id, definition.Email, 20m, definition.JoinedOn, "restored-initial", ct);
                SetClock(seedToday);
                await membership.BeginDeletion(adminActor, user.Id, Guid.NewGuid(), "Synthetic development termination", ct);
                await membership.Restore(adminActor, user.Id, Guid.NewGuid(), "Synthetic development restoration", ct);
                break;
            case SeedScenario.Resumed:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "resumed-initial", ct);
                await ScanAsync(account.Id, seedToday, ct);
                SetClock(seedToday);
                await billing.ResumeMembership(adminActor, account.Id, Guid.NewGuid(), ct);
                await PayGroupAsync(account.Id, definition.Email, 10m, seedToday, "resumed-return", ct);
                break;
            case SeedScenario.Quarterly:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "quarterly-initial", ct);
                SetClock(definition.JoinedOn);
                var item = await db.RecurringItems.AsNoTracking()
                    .SingleAsync(x => x.MembershipUserId == user.Id && x.EndedAt == null, ct);
                await billing.ChangeInterval(adminActor, account.Id, item.Id, 3, Guid.NewGuid(), ct);
                await ScanAsync(account.Id, seedToday, ct);
                await PayGroupAsync(account.Id, definition.Email, 3, 30m, seedToday, "quarterly-renewal", ct);
                break;
            case SeedScenario.EmailOff:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "email-off-initial", ct);
                SetClock(definition.JoinedOn);
                await billing.SetEmailInvoices(adminActor, account.Id, false, Guid.NewGuid(), ct);
                break;
            case SeedScenario.Refunded:
                await PayGroupAsync(account.Id, definition.Email, 20m, definition.JoinedOn, "refunded-initial", ct);
                SetClock(seedToday);
                await billing.Refund(adminActor, account.Id, Guid.NewGuid(), 10m, false, "Synthetic development refund", ct);
                break;
            case SeedScenario.Trailer:
                await PayGroupAsync(account.Id, definition.Email, 10m, definition.JoinedOn, "trailer-initial", ct);
                await SubscribeAsync(account.Id, trailerId, definition.JoinedOn, ct);
                await PayGroupAsync(account.Id, definition.Email, 5m, definition.JoinedOn, "trailer-paid", ct);
                break;
            default:
                throw new InvalidOperationException("Unknown development seed scenario.");
        }
    }

    private async Task AddDiscountAsync(long accountId, DateOnly joinedOn, decimal value, CancellationToken ct)
    {
        SetClock(joinedOn);
        await billing.AddDiscount(adminActor, accountId, 1, joinedOn, joinedOn.AddYears(1000),
            DiscountKind.Percentage, value, Guid.NewGuid(), "Synthetic development discount", ct);
    }

    private async Task PayOneOffAsync(long accountId, string email, DateOnly joinedOn, CancellationToken ct)
    {
        SetClock(joinedOn);
        var invoiceId = await billing.OneOff(adminActor, accountId, Guid.NewGuid(), "Synthetic workshop purchase", "workshop", 5m, seedToday.AddDays(14), ct)
            ?? throw new InvalidOperationException("The development one-off invoice was not created.");
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == invoiceId, ct);
        await PayAsync(email, invoice.OneOffReference!, 5m, seedToday, "oneoff-paid", ct);
    }

    private async Task<long?> SubscribeAsync(long accountId, long serviceId, DateOnly on, CancellationToken ct)
    {
        SetClock(on);
        return await billing.Subscribe(adminActor, accountId, serviceId, 1, false, Guid.NewGuid(), ct);
    }

    private async Task ScanAsync(long accountId, DateOnly on, CancellationToken ct)
    {
        SetClock(on);
        await billing.Scan(accountId, ct);
    }

    private async Task<long> PayGroupAsync(long accountId, string email, decimal amount, DateOnly receivedOn, string label, CancellationToken ct)
        => await PayGroupAsync(accountId, email, 1, amount, receivedOn, label, ct);

    private async Task<long> PayGroupAsync(long accountId, string email, int months, decimal amount, DateOnly receivedOn, string label, CancellationToken ct)
    {
        var group = await db.BillingGroups.AsNoTracking()
            .SingleAsync(x => x.BillingAccountId == accountId && x.IntervalMonths == months, ct);
        return await PayAsync(email, group.PaymentReference, amount, receivedOn, label, ct);
    }

    private async Task<long> PayAsync(string email, string reference, decimal amount, DateOnly receivedOn, string label, CancellationToken ct)
    {
        SetClock(receivedOn);
        return await billing.Import(new ReceiptInput("seed", "seed-account", $"seed:{email}:{label}", amount, "EUR",
            receivedOn, reference, "Synthetic development payment", "Synthetic seed member"), ct);
    }

    private async Task<List<SeededApplicant>> SeedApplicantsAsync(DateOnly today, CancellationToken ct)
    {
        var applicants = new List<SeededApplicant>();
        await CreateApplicantAsync("applicant.verified@seed.nulysa.test", "Verified Applicant", today.AddYears(-30), true, today, ct);
        applicants.Add(new("applicant.verified@seed.nulysa.test", "Verified", false));
        await CreateApplicantAsync("applicant.under18@seed.nulysa.test", "Under-age Applicant", today.AddYears(-16), true, today, ct);
        applicants.Add(new("applicant.under18@seed.nulysa.test", "Verified", true));
        await CreateApplicantAsync("applicant.unverified@seed.nulysa.test", "Unverified Applicant", today.AddYears(-25), false, today, ct);
        applicants.Add(new("applicant.unverified@seed.nulysa.test", "Unverified", false));
        return applicants;
    }

    private async Task CreateApplicantAsync(string email, string name, DateOnly dateOfBirth, bool verify, DateOnly today, CancellationToken ct)
    {
        SetClock(today);
        var id = Guid.NewGuid();
        await membership.Apply(id, new ApplicationInput
        {
            Email = email,
            Message = "Synthetic development application.",
            AcceptTerms = true,
            Profile = new ProfileInput
            {
                Name = name,
                Municipality = "Helsinki",
                DateOfBirth = dateOfBirth,
                Language = "en",
                PhoneNumber = "+358 40 000 0000",
                Nickname = "Seed",
                MatrixId = "@seed:example.test"
            }
        }, ct);
        if (!verify)
            return;

        var application = await db.Applications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
        await membership.Verify(id, membership.VerificationToken(application), ct);
    }

    private async Task<List<SeededReceipt>> SeedReviewReceiptsAsync(DateOnly today, CancellationToken ct)
    {
        var receipts = new List<SeededReceipt>();
        var unknown = await PayAsync("review@seed.nulysa.test", "unknown-seed-reference", 12.50m, today, "review-unknown", ct);
        receipts.Add(new("unknown-seed-reference", 12.50m, "Review", unknown));
        var ignored = await PayAsync("review@seed.nulysa.test", "ignored-seed-reference", 8m, today, "review-ignored", ct);
        await billing.IgnoreReceipt(adminActor, ignored, "Synthetic development review item", ct);
        receipts.Add(new("ignored-seed-reference", 8m, "Ignored", ignored));
        var outgoing = await PayAsync("review@seed.nulysa.test", "outgoing-seed-reference", -20m, today, "review-outgoing", ct);
        receipts.Add(new("outgoing-seed-reference", -20m, "Review", outgoing));
        return receipts;
    }

    private void SetClock(DateOnly date) => Clock.Now = At(date, 12);

    private static DateTimeOffset At(DateOnly date, int hour = 12) => new(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);

    private static ClaimsPrincipal Actor(long id)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id.ToString())], "seed"));

    private static IReadOnlyList<SeedMemberDefinition> Definitions(DateOnly today) =>
    [
        new("active@seed.nulysa.test", "Aino Active", "fi", today.AddDays(-15), SeedScenario.Active, today.AddYears(-30), "aino", "+358 40 000 0100", "@aino:seed.test", "Helsinki"),
        new("door@seed.nulysa.test", "Mikko Door", "en", today.AddMonths(-3), SeedScenario.Door, today.AddYears(-35), "mikko", "+358 40 000 0101", "@mikko:seed.test", "Espoo"),
        new("door-free@seed.nulysa.test", "Noora Free", "fi", today, SeedScenario.DoorFree, today.AddYears(-28), "noora", "+358 40 000 0102", "@noora:seed.test", "Tampere"),
        new("door-due@seed.nulysa.test", "Oskari Due", "en", today.AddMonths(-2), SeedScenario.DoorDue, today.AddYears(-40), "oskari", "+358 40 000 0103", "@oskari:seed.test", "Helsinki"),
        new("grace@seed.nulysa.test", "Helmi Grace", "fi", today.AddMonths(-1).AddDays(-2), SeedScenario.Grace, today.AddYears(-32), "helmi", "+358 40 000 0104", "@helmi:seed.test", "Espoo"),
        new("inactive@seed.nulysa.test", "Ines Inactive", "en", today.AddMonths(-4), SeedScenario.Inactive, today.AddYears(-45), "ines", "+358 40 000 0105", "@ines:seed.test", "Helsinki"),
        new("old-inactive@seed.nulysa.test", "Olavi Old", "fi", today.AddMonths(-12), SeedScenario.OldInactive, today.AddYears(-50), "olavi", "+358 40 000 0106", "@olavi:seed.test", "Tampere"),
        new("new@seed.nulysa.test", "Nella New", "en", today, SeedScenario.New, today.AddYears(-22), "nella", "+358 40 000 0107", "@nella:seed.test", "Helsinki"),
        new("partial@seed.nulysa.test", "Paavo Partial", "fi", today.AddDays(-10), SeedScenario.Partial, today.AddYears(-29), "paavo", "+358 40 000 0108", "@paavo:seed.test", "Espoo"),
        new("credit@seed.nulysa.test", "Kaisa Credit", "en", today.AddDays(-15), SeedScenario.Credit, today.AddYears(-33), "kaisa", "+358 40 000 0109", "@kaisa:seed.test", "Helsinki"),
        new("deposit@seed.nulysa.test", "Daniel Deposit", "en", today.AddDays(-15), SeedScenario.Deposit, today.AddYears(-36), "daniel", "+358 40 000 0110", "@daniel:seed.test", "Tampere"),
        new("honorary@seed.nulysa.test", "Hilla Honorary", "fi", today.AddDays(-15), SeedScenario.Honorary, today.AddYears(-41), "hilla", "+358 40 000 0111", "@hilla:seed.test", "Helsinki"),
        new("discount@seed.nulysa.test", "Dana Discount", "en", today.AddDays(-15), SeedScenario.Discount, today.AddYears(-27), "dana", "+358 40 000 0112", "@dana:seed.test", "Espoo"),
        new("granted@seed.nulysa.test", "Greta Granted", "fi", today.AddDays(-15), SeedScenario.Granted, today.AddYears(-38), "greta", "+358 40 000 0113", "@greta:seed.test", "Helsinki"),
        new("oneoff@seed.nulysa.test", "Olli Oneoff", "fi", today.AddDays(-15), SeedScenario.OneOff, today.AddYears(-31), "olli", "+358 40 000 0114", "@olli:seed.test", "Tampere"),
        new("oneoff-paid@seed.nulysa.test", "Petra Paid", "en", today.AddDays(-15), SeedScenario.OneOffPaid, today.AddYears(-34), "petra", "+358 40 000 0115", "@petra:seed.test", "Helsinki"),
        new("stopped@seed.nulysa.test", "Sanna Stopped", "fi", today.AddMonths(-3), SeedScenario.Stopped, today.AddYears(-39), "sanna", "+358 40 000 0116", "@sanna:seed.test", "Espoo"),
        new("removed@seed.nulysa.test", "Riku Removed", "en", today.AddMonths(-3), SeedScenario.Removed, today.AddYears(-42), "riku", "+358 40 000 0117", "@riku:seed.test", "Helsinki"),
        new("pending@seed.nulysa.test", "Pekka Pending", "fi", today.AddMonths(-2), SeedScenario.Pending, today.AddYears(-37), "pekka", "+358 40 000 0118", "@pekka:seed.test", "Tampere"),
        new("resigned@seed.nulysa.test", "Riina Resigned", "en", today.AddMonths(-2), SeedScenario.Resigned, today.AddYears(-26), "riina", "+358 40 000 0119", "@riina:seed.test", "Helsinki"),
        new("restored@seed.nulysa.test", "Roosa Restored", "fi", today.AddMonths(-2), SeedScenario.Restored, today.AddYears(-43), "roosa", "+358 40 000 0120", "@roosa:seed.test", "Espoo"),
        new("resumed@seed.nulysa.test", "Reetta Resumed", "en", today.AddMonths(-3), SeedScenario.Resumed, today.AddYears(-44), "reetta", "+358 40 000 0121", "@reetta:seed.test", "Helsinki"),
        new("quarterly@seed.nulysa.test", "Qiana Quarterly", "en", today.AddMonths(-1), SeedScenario.Quarterly, today.AddYears(-30), "qiana", "+358 40 000 0122", "@qiana:seed.test", "Tampere"),
        new("email-off@seed.nulysa.test", "Eeva Email", "fi", today.AddDays(-15), SeedScenario.EmailOff, today.AddYears(-28), "eeva", "+358 40 000 0123", "@eeva:seed.test", "Helsinki"),
        new("refunded@seed.nulysa.test", "Rafael Refunded", "en", today.AddDays(-15), SeedScenario.Refunded, today.AddYears(-46), "rafael", "+358 40 000 0124", "@rafael:seed.test", "Espoo"),
        new("trailer@seed.nulysa.test", "Taina Trailer", "fi", today.AddDays(-15), SeedScenario.Trailer, today.AddYears(-24), "taina", "+358 40 000 0125", "@taina:seed.test", "Helsinki")
    ];

    private static string ScenarioText(SeedScenario scenario) => scenario switch
    {
        SeedScenario.Active => "Active, paid",
        SeedScenario.Door => "Active, Door paid",
        SeedScenario.DoorFree => "Active, Door initial-free days",
        SeedScenario.DoorDue => "Membership paid, Door due",
        SeedScenario.Grace => "Membership renewal grace",
        SeedScenario.Inactive => "Inactive after grace",
        SeedScenario.OldInactive => "Inactive for many months",
        SeedScenario.New => "New, initial invoice unpaid",
        SeedScenario.Partial => "Initial invoice partially paid",
        SeedScenario.Credit => "Overpaid, billing credit",
        SeedScenario.Deposit => "Purchase balance",
        SeedScenario.Honorary => "100% discount",
        SeedScenario.Discount => "50% discount",
        SeedScenario.Granted => "Administrator whole-month grant",
        SeedScenario.OneOff => "Open one-off invoice",
        SeedScenario.OneOffPaid => "Paid one-off invoice",
        SeedScenario.Stopped => "Door renewal stopped",
        SeedScenario.Removed => "Door removed",
        SeedScenario.Pending => "Pending deletion",
        SeedScenario.Resigned => "Self-resigned",
        SeedScenario.Restored => "Pending deletion restored",
        SeedScenario.Resumed => "Inactive member resumed",
        SeedScenario.Quarterly => "Three-month interval",
        SeedScenario.EmailOff => "Invoice email disabled",
        SeedScenario.Refunded => "Recorded refund",
        SeedScenario.Trailer => "Self-subscribed Trailer access",
        _ => scenario.ToString()
    };

    private sealed record SeedMemberDefinition(
        string Email,
        string Name,
        string Language,
        DateOnly JoinedOn,
        SeedScenario Scenario,
        DateOnly DateOfBirth,
        string? Nickname = null,
        string? Phone = null,
        string? Matrix = null,
        string Municipality = "Helsinki");

    private enum SeedScenario
    {
        Active,
        Door,
        DoorFree,
        DoorDue,
        Grace,
        Inactive,
        OldInactive,
        New,
        Partial,
        Credit,
        Deposit,
        Honorary,
        Discount,
        Granted,
        OneOff,
        OneOffPaid,
        Stopped,
        Removed,
        Pending,
        Resigned,
        Restored,
        Resumed,
        Quarterly,
        EmailOff,
        Refunded,
        Trailer
    }
}
