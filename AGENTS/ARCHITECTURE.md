# ASP.NET Core architecture and delivery blueprint

**ASP.NET Core architecture accepted in principle; S0–S2 are implemented with
their own acceptance suites. Later-slice implementation details remain proposals.**
The behaviour in [SPEC.md](SPEC.md) is preserved. C#/ASP.NET Core, PostgreSQL-only
database profiles and Quartz.NET are the selected platform direction. The stack
below makes that direction concrete without reopening agreed domain policies.
Package versions, detailed fields and configuration require implementation and
verification; previous Django tests do not certify this implementation. See
[the foundation status](../docs/foundations.md). Nulysa remains the project name.

Read sections 1–3 for the overall shape, 4–9 for implementation contracts, and
10–11 for tests and delivery. Reuse the reviewed
[billing foundation](BILLING_DESIGN.md) and [service architecture](SERVICE_DESIGN.md)
rather than replace them with another schema. [POLICY_REVIEW.md](POLICY_REVIEW.md)
records settled examples; [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md) remains the index
for engineering and deployment follow-up.

## 1. Recommended shape

Build a **modular ASP.NET Core monolith**: one application codebase and database, with
separate web and background-worker processes. At this association's scale, clear
ownership, correct transactions and usable administration matter more than
distributed infrastructure.

| Concern | Recommendation and reason |
| --- | --- |
| Runtime | C# on a supported .NET LTS release; .NET 10 LTS is the initial target, with SDK/patch pinned at scaffolding. Enable nullable-reference analysis. Do not inherit the Python dependency set. |
| Database | PostgreSQL in development, production and database CI, through EF Core and Npgsql. One application DbContext coordinates domain/Identity/OpenIddict tables; Quartz owns separate tables in the same database and explicitly shares application transactions for scheduling. No SQLite support initially. |
| Web UI | Blazor Web App with static SSR and ordinary HTTP forms by default; selective Interactive Server pages/components for richer interactions. Use Blazor's enhanced navigation/form handling where useful, not a parallel HTMX layer. Customizable association branding, including page titles, with unobtrusive Nulysa attribution. No initial WebAssembly/Auto mode, separate client project, frontend authentication system or mandatory public CRUD API. Protocol/device endpoints remain ordinary ASP.NET Core endpoints. |
| Background work | Quartz.NET 4 with PostgreSQL AdoJobStore, a separate worker process and a small configurable execution pool. Enable persistence, clustering, recovery, retry policies and database execution history explicitly. No Redis/RabbitMQ or custom queue framework. Prove transaction/recovery behaviour in S0 (section 7). |
| Email and PDFs | SMTP adapter initially, so Scaleway TEM or another transactional provider can be used through its SMTP endpoint; editable, localised email templates with restricted placeholders. Keep Typst for invoice PDFs, invoking a pinned CLI as a controlled child process and reusing the association's parameterized template. Use trusted source, structured input, controlled private assets/packages/fonts and no arbitrary runtime remote fetching. Rich-text editor and sanitisation-library choices remain deferred. |
| Identity integration | ASP.NET Core Identity for email/password accounts and cookies; OpenIddict for the registry's upstream OAuth/OIDC server. Keep Keycloak as the SSO intermediary. Prove local continuity (section 3); production access/testing is not an early-development prerequisite. |
| Tooling | Pin the .NET SDK and NuGet dependencies/lockfiles. Use compiler/nullable checks, .NET analyzers and formatting. Proposed tests: xUnit, ASP.NET Core integration testing and Playwright for critical browser journeys. Run database/transaction/worker tests on real PostgreSQL. Exact versions and test grouping are S0 work. |
| Deployment | Production uses Linux containers and Kestrel behind a trusted TLS reverse proxy. Prefer one release artifact with explicit web, worker and maintenance entry points, PostgreSQL and private document storage. Persist Data Protection and OIDC signing keys. Local application development may run on the host against containerized PostgreSQL. |

No plugin loader, multi-tenancy, general ledger, arbitrary workflow engine or
application-wide event sourcing. Provider adapters use small C# contracts where a
real external boundary needs one, not interfaces for every class or dynamically
installed plugins. No generic repositories over EF Core, mediator/handler pipeline
or automatic domain-event bus. Board administration uses purpose-built pages;
sysops have a separate superuser-only System Console and narrow maintenance
commands. There is no assumed generic replacement for Django admin.

### Confirmed UI rendering contract

- [UI_DESIGN.md](UI_DESIGN.md) records the agreed responsive portal/administration
  layouts, shared account-page styling and association-theme direction. Use Default
  and Tampere Hacklab themes with independent light/dark/system mode selection,
  semantic CSS tokens and trusted local stylesheet overrides. Bootstrap 5.3 CSS
  with a small theme layer is the initial styling direction, not a requirement for
  global interactivity. Its UI increment precedes S3 without renumbering S3–S6 or
  expanding later domain-feature scope.
- Static SSR is the default, not global interactivity. Ordinary pages/forms do not
  require a persistent circuit. Interactive Server is opt-in for a page or component;
  a shelf-selection grid is an example, not a requirement to build storage early.
- Keep UI state in components, but domain writes behind the same named commands
  used by endpoints/jobs. Recheck authorization, account gates and current domain
  facts on commands; a displayed selection or an authenticated circuit is not proof
  that a later reservation/write is allowed.
- Interactive operations use short-lived scoped DbContexts, not one held for the
  circuit's lifetime. Keep writes out of component initialization/prerendering;
  initialization may run again when interactivity starts.
- Keep Identity cookies and OpenIddict on their HTTP authentication/protocol paths.
  Login/logout need HTTP responses; interactive authentication requires revalidation.
  Existing account pages may remain Razor Pages during the UI transition; there is
  no requirement to rewrite working authentication endpoints merely to use Blazor.
- Test static form validation/antiforgery and interactive reconnect/retry behaviour,
  with accessible keyboard operation and server-side concurrency checks. A lost
  connection must not imply that a submitted domain operation failed or is safe to
  repeat without checking its outcome.

Start a fresh ASP.NET Core project in this directory, separate from both Django
checkouts and their migration graphs. `~/code/mulysa/` remains the legacy integration/
data-migration reference; `~/code/nulysa-py/` supplies the reviewed design and synthetic
foundation-test scenarios. Do not port the Python framework machinery or treat its
development database as production data to migrate. This workspace's implementation
and evidence are indexed in `README.md` and
`docs/foundations.md`; later features remain scoped by the delivery slices.

### Confirmed deployment and development profiles

PostgreSQL is the only supported database. The application and worker may run
directly on the host with a supported .NET SDK; a fully containerized development
workflow is optional, not required.

| Local profile | Database and operation | Supported purpose |
| --- | --- | --- |
| Host-run development | PostgreSQL in a container, matching the selected production major version; web/worker/maintenance entry points run on the host with development transports/provider fixtures. | All local development, migrations, UI, financial assertions, races and real-worker tests. |
| Container integration | Disposable PostgreSQL plus the release web/worker images and local Keycloak where needed; synthetic data only. | Deployment/configuration, persistence, process-death and browser/broker acceptance tests and CI. |

Use the same domain commands, EF mappings/migrations, constraints and real persistent
job handlers in both profiles. PostgreSQL-specific partial indexes, exact numeric
aggregates and row locks are available without a second provider compatibility
layer. Pure calendar/pricing tests need no database. EF's in-memory provider and
SQLite are not substitutes for relational, financial or transaction tests.

Do not implement SQLite, a second migration set or a disposable reduced schema in
this initial build. It was a convenience, not a continuing compatibility contract.
Apply EF Core migrations and the pinned Quartz schema/upgrades once per release,
not independently at every web/worker startup. Never run a destructive vendor
table-recreation script against an existing database; review installation and
upgrade scripts and prove they preserve scheduled work.

## 2. Modules and ownership

Names below are working C# feature folders/namespaces, not separate services or a
requirement for a project/assembly/interface per module. Conceptual property names
in the planning docs are not final C# casing conventions.

| Module | Owns | Does not own |
| --- | --- | --- |
| `accounts` | Identity user, verified login email, profile, password/cookie integration, stable OIDC subject. | Membership standing, money or service eligibility. |
| `membership` | Applications, verification, approval, joining anniversary, pending deletion and member lifecycle audit. | A duplicate wallet or paid-through counter. |
| `billing` | Fees/prices/discounts, the nine reviewed core billing models, calendars, allocations, coverage, corrections and exports. | Door permissions or a general debt/accounting system. |
| `services` | Stable service definitions, subscription episodes, renewal intent and changes. | Another pricing engine or membership-like inactive-service state. |
| `communications` | Localised templates, drafts/audiences/preferences and per-recipient delivery records. | Decisions about admission, financial fulfilment or access. |
| `access` | Resources, qualifications/trainers, current fob registrations, ownership history, devices and access events. | Reimplementing billing on readers. |
| `storage` | Runtime-defined groups/positions, queues/offers, rentals, term options and handover. | Recurring subscriptions or its own credit ledger. |
| `integrations` | Bank/provider/OIDC adapters, external source identities, sync runs and reconciliation inputs. | Policy embedded in provider-specific payload parsing. |
| `operations` | Narrow durable work dispatch, scheduler bookkeeping, health and operator diagnostics. | Other modules' diagnostic views, a configurable business-process platform, unrestricted database editor or a second domain audit journal. |
| `flows` | Explicit cross-module commands and composed read queries. No domain tables. | A service locator, generic repository layer or universal event bus. |

The System Console is a cross-cutting web area, not another domain module or a
synonym for `operations`. Add diagnostic views from any module alongside its feature
implementation, keeping data/rules with their owning module and composing queries
where necessary. No plugin framework or upfront catalogue of diagnostic screens is
required; see the [System Console contract](SPEC.md#system-console).

Within a feature, start with entities/mappings, commands, queries, pages/input
models and tests. Split files when they become large, not into an elaborate layer
hierarchy. Use DbContext directly, explicit relationships and projected read queries;
keep lazy loading disabled. Program.cs visibly registers dependencies, middleware
and endpoints. One scoped DbContext per request/command/job, including a fresh
operation scope for interactive component commands rather than a circuit-long
DbContext; it is not thread-safe and must never be shared by parallel operations
or a singleton scheduler service.

### Dependency rules

- Model dependencies start at `accounts`; membership and subscriptions reference
  the live member. Billing's typed recurring owners reference membership or a
  subscription, while those owners do not point back to a current invoice row.
  Coverage targets a stable service or basic membership through the billing
  account. Storage purchases link to that same financial account.
- Keep domain writes behind named commands. Cross-module workflows compose those
  commands in `flows`; page handlers, endpoints, jobs and maintenance commands call
  the same entry points directly. No financial work in ORM interceptors,
  `SaveChanges` overrides, property setters, templates or permission checks.
  Administrative writes must not bypass these commands.
- Calendar/pricing functions take explicit inputs and return typed values.
  Membership-standing and service-use evaluators consume dated facts; composing
  their ORM inputs belongs in queries, not mutually recursive model properties.
- Split **may issue**, **may fulfil** and **may use now**. A payment need not pass
  a door-use check, and a successful purchase cannot silently subscribe a member.
- Keep module dependencies/migrations acyclic; use explicit typed foreign keys and
  normal staged EF migrations, not untyped polymorphic owner links to avoid ownership
  decisions. Configure financial-history deletion behaviour rather than inherit
  required-relationship cascade defaults.

## 3. Identity, roles and admission

**Proposed records:** `User`, a one-to-one `Membership`, a separate `Application`,
and a small approval/lifecycle audit. An applicant need not have a usable auth user.
Choose the Identity user/key shape in the new project's first migration. Preserve
the numeric non-reused historical IDs required by the approval audit; the OIDC
subject is a separate immutable string, including frozen legacy subjects. Separate login
availability from active membership: an inactive member may log in; an unapproved
applicant or pending-deletion account may not.

- Use immutable internal identifiers/member numbers; never recycle historical IDs.
  Email is a verified, unique login/contact attribute, not a primary key or a live
  OIDC subject. Define email normalisation once, with database uniqueness; do not
  apply provider-specific mailbox rewrites. A pending email change is verified
  before replacing the old address, including a concurrent uniqueness recheck.
- Use ASP.NET Core Identity password hashing, cookie authentication, antiforgery
  protection and password setup/reset machinery. Add explicit admission/deletion
  checks to login, every protected portal request, code redemption, refresh and
  userinfo. Identity's periodic security-stamp validation alone is not immediate
  denial of an existing cookie. Password setup/reset must not bypass the gates;
  never use membership payment standing as the account login-enabled flag.
- Use OpenIddict's server support upstream of Keycloak, not merely an OIDC-client
  handler. Configure code flow, S256 PKCE, allowed grants/scopes, client authentication,
  signing/issuer/redirect validation and standard scope-limited claims. Disable
  unneeded grants. Refresh tokens expire after 365 idle days; a successful refresh
  renews that lifetime. Configure and test rolling/sliding behaviour explicitly,
  including the expiry boundary and blocked-account refresh. Expiry is enforced at
  exchange even when daily authentication cleanup is late.
- Persist and protect shared Data Protection keys for cookies and Identity tokens
  across web restarts/instances. OIDC signing keys are separate, persistent,
  externally supplied secrets with an explicit rotation policy; no ephemeral
  development keys in production. Test restart/multi-instance cookie continuity.
- Keep password-guessing/CAPTCHA design deferred as specified, not silently adopted
  through framework defaults. Antiforgery, account gates and secure cookies are not
  deferred. Daily expired-token/authorization cleanup must preserve usable refresh
  chains; cookie-only authentication has no assumed Django-style session table.
- Have one full board-administrator role and separately granted sysop/superuser
  capability for System Console access; board authority must not satisfy the superuser policy
  or permit self-escalation. An account may hold both. See the confirmed
  [System Console contract](SPEC.md#system-console). Identity's `Superuser` role is
  assigned/revoked through operator-only CLI commands. Its policy checks the live
  database assignment and account gates in a fresh scope, not cached role claims.
  Trainers get qualification-scoped authority records, not staff access.
  Check permissions and object ownership in
  commands as well as views. An ID in a URL or hidden navigation item grants no
  access; ordinary members cannot search the member registry.
- Define safe member, trainer and administrator query projections. Trainer lookup
  returns only name, nickname, member ID and relevant qualifications. Inactive
  members retain profile, membership-payment and convenient services-page access;
  pending deletion blocks the portal immediately, including existing sessions.

### Local OIDC integration and later continuity rehearsal

**Agreed:** use a local Keycloak instance and synthetic users; do not require
production Keycloak access or experiment against production. The reference checkout
at `~/code/mulysa/` has `api/mulysaoauthvalidator.py`, which emits email as the legacy
upstream `sub`. Its `README.md` documents the broker setup. These are starting
points, not proof of the deployed configuration. The operator-provided redacted
DOT application settings and signing compatibility notes are recorded
in [OIDC reference notes](../docs/oidc-reference.md); they are not a realm export.

**S0:** automatically test the replacement → local Keycloak → small downstream
client with synthetic users and credentials. Exercise code flow, issuer, signatures,
client authentication, redirects, scopes and PKCE. Verify email changes preserve
the Keycloak identity and downstream subject, disabled login blocks fresh upstream
authentication, and existing downstream sessions are not falsely claimed revoked.
No legacy registry, importer or production configuration is required.

**S1:** test password setup/reset and real admission/pending-deletion commands when
those workflows exist, including existing registry-session denial.

**S6:** rehearse legacy → replacement with synthetic/import fixtures, retaining the
realm, broker alias and associations. Verify frozen legacy subjects preserve
Keycloak user IDs and downstream subjects without duplicates; validate legacy
claims/mappers and signing/client changes. Do not recreate broker associations
merely because an endpoint changes. Production cutover mappings remain operator-
assisted validation, not a reason to inherit the legacy domain schema.

This proves the migration mechanism in a controlled environment, not compatibility
with an unknown production realm. Actual provider alias/external IDs, issuer/client
configuration, claim mappers and Matrix/Nextcloud account associations remain an
operator-assisted **cutover validation**. A redacted configuration export would help
but is not required to begin development. Do not copy production secrets or personal
data into the local rehearsal, or claim the local test validates actual production mappings.

## 4. Data, time and audit contracts

Reuse `BillingAccount`, `BillingGroup`, `RecurringItem`, `Invoice`, `InvoiceLine`,
`Receipt`, `MoneyMovement`, `Coverage` and `BillingOperation` as described in
[BILLING_DESIGN.md](BILLING_DESIGN.md). Supporting fee/price/discount records are
ordinary dated pricing configuration, not extra billing engines. Retire definitions
with history; live-origin relations may become null without erasing financial facts.

True one-off payment requests reuse invoice lines, receipts and the journal without
inventing a recurring instruction or monthly schedule. The reviewed billing draft
details recurring-group routing first; settle explicit account/reference ownership
for non-grouped invoices before S2's schema is final. This is a bounded extension of
that same billing path, not storage's funded checkout or another engine.

| Boundary | Required representation |
| --- | --- |
| Money | C# `decimal`, explicitly mapped PostgreSQL `numeric` precision/scale, currency and cent precision for stored charges; no `float`/`double` arithmetic. Available balances/holds derive from journal aggregates, tested on PostgreSQL. |
| Calendar | `DateOnly`/PostgreSQL `date`, start-inclusive/end-exclusive periods. Preserve original anniversary phase and intended month-end day; one shared calendar per member/interval. Keep an explicit association timezone, proposed `Europe/Helsinki` for this deployment, resolved through `TimeZoneInfo`. Do not use UTC offsets or 24-hour intervals as local-calendar rules. |
| Events | UTC `DateTimeOffset` (offset zero)/PostgreSQL `timestamptz` for occurrence/processing. Retain actual bank receipt dates separately. Inject `TimeProvider`; never substitute import time for receipt time. |
| Prices | Retain applicable base price, effective discount segments, proration basis and rounding result on a quote/purchase. Proposed tie handling is decimal half-up, with final line rounding after unrounded segment calculation; do not rely on .NET's default midpoint-to-even rounding. Verify positive and correction examples before pricing implementation. |
| Membership | Admission/lifecycle facts plus a derived dated standing result. Grace is not paid coverage. Cached dashboard standing is not an authority for access or checkout. |
| Subscription | Current/ended episodes, with explicit stop/removal causes and stable service identity. Coverage and once-only introductory eligibility are not reset by a new episode. |
| Invoice | One public earliest unfinished-charge due date, separate from internal charge-specific grace/window/expiry facts. No PDF-version archive or editable per-line due-date UI. |
| Audit | Domain-specific action records with actor, reason, effective/processing times and retry key where needed. Financial corrections append linked facts; generic before/after dumps of full profiles are not an audit strategy. |

Database constraints enforce local checks/uniqueness: valid periods, nonnegative
line prices, positive movements, one open bill per group, one current subscription
per member/service, unique source receipts and coverage/operation origins. Commands
enforce cross-row rules under locks. A fulfilled zero-price line creates coverage
without a fabricated receipt or spending movement. A reviewed future-discount credit
links the original purchase and adjustment without rewriting completed coverage.

Business commands still own pricing, authorisation, lifecycle transitions and
friendly validation. Database constraints are independent structural safeguards,
not a replacement for that logic or transaction locks. Configure check constraints
and unique/partial unique indexes through EF Core mappings/migrations to enforce
one open/current row among historical rows. This is for integrity, not a performance
requirement. Separate authoritative current/history relationships with ordinary
uniqueness remain an alternative where clearer; do not add SQL triggers or stored
procedures for billing rules. Inspect generated migrations, especially delete
behaviour, filtered-index predicates and decimal precision. Entity validation and
change tracking do not replace database enforcement under concurrent connections.

## 5. Transactions and command boundaries

Keep money and the eligibility changes that can authorise its spending in the
**same explicit EF Core/PostgreSQL transaction**, not
eventually consistent events between apps.
The durable queue is for external delivery/slow work, not for finishing half a
purchase after a successful response.

### Locking proposal

1. Provision the member's `BillingAccount` at approval, even before its first
   receipt. Use this stable row as the shared per-member transaction lock for
   financial, subscription and membership-ending commands. Approval first locks
   its verified application before creating the new member/financial owner.
2. Acquire PostgreSQL row locks with parameterized `SELECT ... FOR UPDATE` on the
   actual EF connection/transaction; do not assume EF tracking or an optimistic
   concurrency token supplies a row lock. Lock affected billing accounts in
   ascending ID order, then shared storage
   groups if needed, then affected domain rows in a documented consistent order.
   Recheck permissions, lifecycle, availability and dates **after** locking and
   loading/refreshing authoritative state, not from stale tracked entities.
3. For storage, a group-level lock serialises queue/position allocation at this
   scale. A member lock alone does not protect a position from another buyer.
   Queue advancement selects a candidate, acquires locks in the same order and
   revalidates/retries; never hold a group lock while then waiting for its member
   lock in the reverse order.
4. Fob claim/revoke changes current registration and assignment history atomically.
   Unique current UID registration resolves simultaneous claims, including when no
   existing row could be locked; handle conflicts as an ordinary failed claim.
   Address revocation by the owned registration/assignment ID, not UID alone, so
   a delayed revoke cannot remove another member's replacement registration.
5. Append the operation result and durable notice/work intents before commit.
   Schedule through Quartz enlisted in that same transaction (section 7); a
   callback that enqueues only after commit is not the durable intent. Provider
   HTTP calls and PDF rendering happen outside member/money locks.

Use one outer command transaction across composed domain/Identity writes and
Quartz scheduling. `SaveChangesAsync()` inside it does not itself commit the command.
Nested helpers participate rather than open/commit independent transactions.
Rollback the whole transaction on validation/database/scheduling failure; never
catch a failed scheduling write and commit partial work. If transient-error retries
are enabled, retry the whole command with its original operation key and a fresh
DbContext, not an individual SQL statement or external send. Cancellation before
commit rolls back; a lost response after commit still requires idempotent retry.

Every retryable command has a scoped idempotency key tied to its actual action:
provider transaction, logical dated purchase, explicit grant, checkout or correction.
Reusing a key with different inputs is an error/review case, not another purchase.
Bulk maintenance commits bounded per-member work, not one association-wide lock.

| Entry point | Atomic outcome |
| --- | --- |
| Approve application | Recheck verified/unapproved status; establish user/membership and approval audit, joining anchor, billing account and initial membership request; enqueue welcome/password setup and invoice delivery. Payment is not admission. |
| Match receipt | Import/deduplicate source facts separately first; matching locks the routed account before the receipt/domain rows and rechecks pending deletion. Allocate valid money to independent lines, fulfil funded lines and update credit/standing/notices together. Pending-deletion references stay in failed-match review. |
| Subscribe/stop/remove | Apply self/admin authority and episode cause; grant eligible initial days or explicit admin exception, coordinate recurring instruction and unfinished charges. Stop preserves remaining purchased/granted use; immediate removal is distinct. |
| Transfer balances | Check enabled feature and available source; record internal transfer. Purchase-to-billing immediately runs the normal allocator in the transaction; held/spent funds cannot be moved back. |
| Buy/renew storage | Recheck active membership, offer/position, group limits, term and confirmed purchase balance; debit and allocate/extend rental together. A failed checkout commits neither. |
| Begin deletion | Mark pending and block login/access immediately; stop new purchases/billing and close unfinished requests/release holds coherently. Preserve completed purchase evidence and unspent-money claims, not usable services. Enqueue the final warning once. |

Out-of-order bank imports require stored quote/cause facts. A timely receipt can
correct a mistaken automatic timeout without undoing explicit resignation,
stop/removal or a newer subscription. Ambiguous/reallocated funds go to manual
review, not a universal historical replay engine. Preserve source receipts and
review dispositions across retries and administrator restoration.

## 6. Access and storage contracts

### Live access and cached-event attribution

Expose a narrow, versioned device API for live decisions, scoped cache exports and
retry-safe offline event upload. Device credentials are revocable and resource-
scoped over TLS. Do not expose general member/financial APIs to hardware.

**Proposed access records:** `Resource`, qualification definitions/holdings/trainer
authorities, `FobRegistration`, `FobOwnership`, `Device` and `AccessEvent`.
Resource links express required qualifications/services explicitly. Current UID
registration is unique; closed ownership history does not block UID reuse.

- A live decision queries current registration, membership, resource availability,
  qualifications and applicable service coverage/grace. Return a decision/reason
  and bounded validity, not raw billing or trainer records. Date gates apply even
  if a cleanup job has not run. A live denial always beats an older cache grant.
- Cache entries carry minimal UID/permission validity plus opaque assignment/grant
  context and generation time, not profiles. Bind that context to the device/
  resource, using a server-recorded or authenticated export reference; do not trust
  a caller-supplied member ID as attribution. TTL defaults to the confirmed 24
  hours and is also bounded by known earlier eligibility ends.
- Offline reports identify device, client event ID, occurrence time, resource,
  reported decision and the cached context used. Deduplicate `(device, event ID)`;
  conflicting payloads are flagged, not overwritten. Preserve original occurrence
  and server-receipt times; old uploads do not refresh the one-hour claiming list.
- Resolve historical assignment/grant context, not today's UID owner. A stale
  Alice grant used after Bob reclaimed the UID must not become Bob's history.
  Preserve the reported decision and distinguish stale context from a live allow;
  unresolved attribution stays unresolved. This identifies an authorisation
  context, not proof of who physically carried a UID fob.
- Live-first behaviour remains mandatory even on cache-capable clients. Offline
  fallback is optional and cannot guarantee immediate revocation. Ownership-history
  retention and delayed-report limits must be compatible with erasure, not an
  excuse to preserve a deleted member profile forever.

### Storage is allocation, not subscription billing

**Proposed records:** `StorageGroup`, `StoragePosition`, `QueueEntry`,
`StorageAllocation`, `StorageOffer`, `Rental` and dated purchase/renewal facts linked
to billing movements. The shared allocation row claims a position for both an offer
and its eventual rental, with conditional uniqueness for one unreleased claim per
position and per billing account/group. Accepting an offer converts that same claim;
it does not create a competing one. This is a position claim, not a money hold or
`FundsReservation`. Separate offer/rental tables alone cannot enforce cross-table
position uniqueness. A stable financial-owner link lets paid dates survive profile
erasure without retaining a live member.

Use deterministic FIFO order (entry time then ID). Offer generation, expiry/decline,
inactive-candidate skipping and handover call the same locked allocation commands.
A time-limited offer reserves a position, **not money**. Checkout uses confirmed
purchase balance; it does not issue a storage-specific SEPA reference or recurring
invoice. Respect paid-term protection, continuous-occupancy limits and clearance/
junk-area notices; database allocation is not evidence of physical clearance.
Membership inactivity blocks use/purchase without changing rental dates or triggering
automatic handover/refund. Erasure must not accidentally shorten a paid term by
cascading away its allocation/financial facts.

## 7. Durable work, email and scheduled maintenance

### Selected backend: Quartz.NET 4 with PostgreSQL

Use [Quartz.NET](https://www.quartz-scheduler.net/documentation/quartz-4.x/) for
persistent immediate/delayed/recurring work. Use its PostgreSQL AdoJobStore in the
application database, not RAMJobStore or a separate queue database. A separately
run .NET worker hosts Quartz; web processes schedule work but do not execute it.
Use a stable scheduler name and distinct instance IDs, shared persistent tables
and explicit clustering when running multiple worker instances. Start with a small
configurable pool, scoped dependencies per job and development transports/fixtures.
Do not substitute `Task.Run`, an in-memory channel or inline handlers for durable work.

**Transactional scheduling:** enable `AcceptEnlistedTransactions` and use
`scheduler.EnlistTransaction(tx.GetDbTransaction())` in the same async scope as
awaited scheduling calls. It must be the actual transaction/connection used by the
domain DbContext. Commit before disposing the enlistment scope so signalling occurs
after commit. Enlistment established inside an async helper does not flow back out
to its caller. Do not run concurrent scheduler calls on that connection, or start
the scheduler inside the business transaction. An ambient `TransactionScope` alone,
two connections to the same database, or after-commit scheduling is not equivalent.
Keep transactions short because Quartz locks last until commit; roll back the whole
business command if scheduling fails. See the
[transaction documentation](https://www.quartz-scheduler.net/documentation/quartz-4.x/tutorial/job-stores.html#joining-an-existing-transaction).

Quartz owns its schema, trigger acquisition and execution bookkeeping. Apply its
pinned fresh-install/upgrade schema through controlled release steps and validate
it at startup; never blindly run drop/recreate DDL. Domain delivery records and
operation keys retain business intent and duplicate prevention. Do not build a
parallel general job engine or use a Quartz job/trigger ID as a purchase/grant key.
S0 pins Quartz 4.6.2 and verifies the contracts on the selected
.NET/EF/Npgsql/Quartz/PostgreSQL combination; see `docs/foundations.md`. Repeat
these gates when upgrading rather than treating package documentation as proof.

### Recovery contract, not a prescribed lease implementation

Quartz's persistent recovery/cluster coordination is not Q2's visibility/ack scheme
or a prescribed application lease-token design. Require **observed recovery and
duplicate-safe effects**, not a particular internal mechanism:

- Request recovery on jobs that must survive worker death. Configure cluster
  check-in/failure detection and prove recovery both after a restart and through a
  surviving peer. Prove graceful-shutdown cancellation too: it is not necessarily
  treated like a crash. Where needed enable `RecoverFiringsCancelledByShutdown`
  with the matching cancellation/recovery settings and test the complete combination.
- Configure bounded [retry policies](https://www.quartz-scheduler.net/documentation/quartz-4.x/how-tos/retrying-failed-jobs.html)
  explicitly; exceptions are not retried by default. Distinguish retries after the
  first attempt from total attempts. Recovery triggers may not carry the original
  trigger's policy, so configure/test the appropriate job/default policy too.
  A retry may be declined when it collides with the next scheduled occurrence;
  failure monitoring must not depend solely on a retries-exhausted callback.
  Ordinary retry budgets do not establish a bound on repeated process deaths.
- Use bounded HTTP/database/renderer operations and cooperative cancellation with
  an explicit shutdown deadline. In-process .NET timeouts cannot forcibly stop code
  that ignores cancellation; no Q2-style hard execution-process timeout is implied.
  Run potentially uninterruptible rendering in a killable child process, clean up
  partial output and treat cancellation-after-external-send as an uncertain outcome.
- In S0, test scheduling rollback and visibility only after commit, queued-work
  restart, worker death before/after a domain commit, competing worker instances,
  graceful cancellation/restart, timeout/overlap, bounded ordinary failures and
  operator visibility on real PostgreSQL. Inject stale/late completions and false
  failure-detection overlap; neither an overlap attribute nor a cluster claim is
  proof that an old worker cannot still act. Report material mismatches rather than
  silently add a custom lease framework or weaken money/notification guarantees.
- For a given operation key, domain effects commit at most once through transactions/
  unique keys; job execution is **at least once**. External delivery is not exactly
  once. Use provider idempotency where supported and record accepted provider IDs;
  ambiguous timeouts need reconciliation/review, not blind repeat sending.
- Before an external send, inspect the delivery/attempt record so a redelivered task
  does not deliberately repeat a completed send. A previously started attempt with
  an uncertain outcome must be reconciled or marked for review, not blindly retried
  by Quartz. Package retry machinery does not settle that application-level ambiguity.
- Give each recipient a delivery record. Classify messages as optional routine,
  optional newsletter or mandatory operational; recheck eligibility/preferences
  at dispatch, with explicit exceptions for verification/rejection/final-warning
  messages that do not require an active member login. Apply explicit localisation
  fallback, per-provider rate limits and proposed operational-message priority.
- Render editable rich-text email through server-side sanitisation and allow-listed
  placeholders, not unrestricted Razor/C# template execution. Select the editor,
  stored content format and sanitisation library together when implementing authoring;
  an editor does not replace server-side safeguards. Prefer trusted Typst invoice
  templates with structured parameters, not user data interpolated as executable
  Typst source. Validate the existing template and pin/vendor its required packages
  and fonts before claiming reproducible rendering. Invoice jobs identify the
  intended charge revision; stale queued work must not send a superseded payable
  quote. Document generation cannot change prices, allocate credit or require a
  permanent PDF-version archive.

### Trusted job types and minimal payloads

Application scheduling selects registered `IJob` types and stable job/trigger keys.
Pass only minimal identifiers, revision/operation keys and necessary primitive
values; prefer string-valued JobDataMap data (`StoreJobDataAsStrings`) and the
supported System.Text.Json serializer for scheduler state. Never serialize DbContext,
tracked entities, service objects, delegates or arbitrary CLR types as job arguments.
Requests, device input and editable templates cannot select job types, assemblies,
hooks or executable definitions. Restrict scheduler-table writes and management
endpoints; JSON is not an authorization boundary or a sandbox for untrusted jobs.
Avoid copying profiles/secrets into job data, results, history or stack traces.

Enable database execution history with explicit retention and redaction so all
instances see failures across restarts. History writes are separate from domain
commits and may fail or be trimmed; history is diagnostics, not durable delivery
truth or an audit journal. Monitor oldest unfinished business work, heartbeats,
misfires, crash loops and final failures rather than success/failure counts alone.
Expose technical status/history in the initially read-only, superuser-only
System Console, separate from the board's attention dashboard. Project existing
diagnostic/domain records rather than build a second source of truth. Scheduler
diagnostics are one contribution, not the limit of the console's scope; add other
views as their features are implemented. Protect each route/query and test denial
for ordinary members and board-only administrators, as well as superuser access.
Assume trusted sysops with server access; omit unnecessarily sensitive data without
building a console-specific redaction framework, as specified in
[SPEC.md](SPEC.md#system-console). Existing log/history redaction remains unchanged.
The CLI status command remains usable independently for external monitoring; the
System Console's first increment is implemented after S0: a static SSR shell and
read-only scheduler diagnostics, not an added S0 exit gate or completed inventory
of future diagnostic views.
Any later retry/cancel/repair action must go through the relevant authorized,
duplicate-safe application operation with confirmation, reason and audit. Do not expose
arbitrary Run now, type selection or schedule editing as an administrative shortcut.

**Rejection-email ordering needs an early failure-path test.** Rejection prevents
approval/login, queues the notice, and purges the application immediately after
provider acceptance of the send, without the member deletion waiting period.
Do not call an enqueued message delivered or keep rejected applicant data forever
when a provider fails. Minimise pending delivery data; specify terminal-failure
escalation/erasure handling before this slice is complete. Similar short-lived
delivery payloads must not become undeclared archives of deleted profiles.

Use Quartz triggers for registered periodic wake-ups; domain dates and persisted run
watermarks determine the actual due work. Enqueue deduplicated commands so scheduler
catch-up cannot manufacture extra cycles, grace or repeated notices. Verify timezone/
DST behaviour for local-midnight jobs; do not assume a daily interval preserves local
wall-clock time. Set each schedule's misfire/catch-up policy explicitly. For ordinary
due-work scans, coalesce missed wake-ups and process outstanding dated work in bounded
commands rather than replay every missed tick. Persist the fortnightly phase; a
restart or schedule reinstall must not move it or delete early. Install/update
stable named jobs/triggers once per release without resetting next-fire times or
duplicating schedules; do not overwrite them with `StartNow()` at every startup.
Use named schedules, not one giant daily function:

| Schedule | Responsibility |
| --- | --- |
| Foundation heartbeat | Frequent persisted observation showing that a real scheduler and worker made progress; not proof that every job is healthy. |
| Daily authentication cleanup | Remove expired/obsolete authentication records in bounded batches without breaking usable refresh tokens/authorizations. Expiry/account gates remain enforced on requests even when cleanup is late. |
| Frequent due-work scan | Invoice lead dates, grace/window expiry, service removal, unverified-application expiry, storage offers/clearance and required notices. Proposed minute-level scan; dated eligibility does not wait for the scan. |
| Nightly at association-local midnight | Confirmed GoCardless/Nordigen sync, with booked-date mapping, source deduplication and visible failed/stale/reauthorisation status. |
| Every 14 days | Final-deletion batch, rechecking configurable waiting-time eligibility (default 14 days). Normally 14–28 days' actual wait; a late batch may take longer, never delete early. Use an anchored calendar-aware 14-day interval in the association timezone, not day-of-month `*/14` or a fixed 336-hour approximation across DST. |
| Regular reconciliation | Check journal/coverage/allocation consistency and worker/provider health; report discrepancies rather than silently repair completed purchases. |

## 8. Erasure, security and operations

Final deletion is an explicit command, not a cascade experiment or soft-delete
flag. Lock the member, recheck pending/eligible status and cancellation, then:

1. End usable authentication, credentials, subscriptions and qualifications; remove
   personal profile/application data, pending email changes and unnecessary payloads.
2. Preserve only required purchase/audit/claim facts through deliberate nullable
   relations and minimal historical identifiers. Live member deletion must not
   cascade into money movements or erase remaining member advances. Preserve
   unspent claims separately; unused purchased service time needs no automatic refund.
3. Detach/erase personal ownership links under the approved retention rules and
   show deleted subjects without reconstructing profiles. New admission creates
   a new identity; never relink by email or use old financial/audit rows as a shadow
   registry. Historical identifiers remain potentially personal data.
4. Cancel unnecessary queued work and minimise retained delivery diagnostics. Do
   not send a deletion-completion message. Track erasure failures without copying
   the deleted profile into an error log.

Synchronisation with Keycloak/providers, backups, logs, exports and a read-only
legacy archive needs an explicit erasure/retention runbook. A database delete cannot
claim to purge every external copy or backup immediately. Keep encrypted backups
with defined expiry and restore procedures that reapply required deletions; any
temporary erasure bookkeeping must be minimal and justified, not a returner index.

Configuration-file settings include purchase-balance enablement and deletion
waiting time, bound and validated through ASP.NET Core configuration/options;
deployment environment variables/secret mounts supply secrets outside source
control. Propose administrator UI settings
for supported fee/service/resource/storage options, not arbitrary rule expressions.
Validate configuration and automated-language fallbacks on startup/activation.

Operate with structured, redacted logs, request/operation IDs, worker/scheduler
heartbeats, last successful bank sync, failed-job/review counts and tested backup
restores. Protect private PDFs/exports with server-side authorisation; use antiforgery
on cookie-authenticated mutations, secure cookies, HTTPS and explicit trusted-proxy/
forwarded-header settings. Apply verification/device rate limits; settle deferred
portal/admin password-guessing protection before public deployment. Do not assume
Identity lockout protects a sign-in path without deliberate configuration. No
unauthorised access-log lookup or contact-data exports through trainer/device endpoints.

One database is a deliberate operational dependency, not a claim of cluster-level
high availability. Document outage behaviour, monitor it and rehearse recovery.
Optional reader caches reduce some access outages, not database durability risks.

## 9. Integration and cutover boundaries

- Bank adapters retain source transactions and sync outcomes, then pass normalised
  receipts to the same billing commands used by manual review. Unknown/ignored
  transactions and outgoing bank details remain traceable without turning every
  bank entry into a portal purchase.
- Payment-provider callbacks require verified authenticity, stable source IDs,
  explicit member/purpose metadata and amount/currency checks. Provider payout is
  settlement reconciliation, not a second member receipt. Fees/refunds are explicit
  facts; the browser's return page is not proof of payment.
- Bank transfer and MobilePay remain target scope. Build a real MobilePay adapter
  and reconciliation tests before claiming that support. The approved milestone is
  bank transfer/GoCardless in S2, with MobilePay implementation and sandbox acceptance
  mandatory before S6; it is not permission to drop the requirement.
- Keep actual credentials and live provider calls out of CI; use contract fixtures
  and sandbox integration checks. Do not reuse legacy allocation rules or email-
  based identity assumptions through an adapter.
- Migration is a repeatable, dry-runnable .NET maintenance command with source mapping
  and discrepancy reports. Import opening coverage/balances and prior-service/free-
  eligibility facts, not historical receipts replayed as new income. Identity broker
  mappings, fobs/qualifications, storage occupants and queues need explicit mappings.
- Rehearse one full-replacement cutover after all five phases: freeze legacy writes
  and payment consumers, import/reconcile final data and the receipt watermark,
  switch integrations/readers, then enable new jobs and send new-reference/password
  instructions. Old references go to review, not compatibility routing. Avoid both
  systems importing/spending the same transition receipts.

Treat rollback after new payments, access changes or notices as a reconciliation
operation, not restoring an old database and pretending external effects did not
happen. The archive/import boundary and imperfect mappings remain deployment work.

## 10. Acceptance and engineering test matrix

Use the existing worked examples as executable fixtures. Suggested test identifiers
below are labels, not a commitment to a new test framework or another policy list.

| Test group | Essential checks |
| --- | --- |
| `MEM` | Verification before approval/login; rejection-send/erasure failure paths; approval before payment; initial versus renewal grace; manual termination; inactive profile/payment access. |
| `CAL` | 31st/leap-year anchors; March-origin quarterly phase; actual-day proration/rounding; discount-expiry splits; under-seven-day bridges without repeated merging or rebilling covered days. |
| `BILL` | Partial membership-first funding; independent fulfilment; journal conservation and nonnegative availability; cancellation releases only unfinished holds; one visible earliest due date without resetting line timing. |
| `DATE` | Receipt versus import date, several/out-of-order funding events, resumption versus original-period renewal settlement, no daily repricing to make insufficient money sufficient. |
| `ADJ` | P1 unpaid/prepaid future-discount parity with reviewed credits; P2 grant overlap/releases; honorary 100% discount without fictitious money; no automatic unused-time resignation refund. |
| `SVC` | Once-only introductory eligibility plus retry-safe admin exception; free days remain free; first-payment window independent of coverage; zero/seven-day renewal grace; stop versus remove; membership lapse without frozen service time. |
| `RACE` | PostgreSQL transaction tests for competing spends, fulfilment versus removal/deletion, duplicate receipts/grants, simultaneous fob claims and competing storage checkouts. Both success and rollback leave coherent facts. |
| `WORK` | Quartz/EF shared-transaction rollback/commit visibility including scheduling failure, restart, competing instances, worker death before/after domain commit, shutdown cancellation/recovery, timeout/overlap and stale completion. Bounded ordinary retries and visible crash loops/final failures without depending on history alone; no duplicate domain effects or lost durable intent. Ambiguous sends go to review; completed recipients are not deliberately resent; opt-out/deletion rechecked at dispatch. |
| `DEV` | PostgreSQL fresh-install EF schema, Quartz 4.5→4.6 upgrade and intended check/partial-unique constraints and delete behaviour; host/container configuration; real-worker schedule installation/reinstall without phase resets; persisted Data Protection/signing-key continuity across restarts/instances. No SQLite or EF in-memory substitute. |
| `ERASE` | Default waiting time plus fortnightly batch; admin cancellation before actual deletion; pending-deletion failed-match disposition survives restoration/reimport; new admission restores nothing; retained claims reconcile without a live profile. |
| `ACCESS` | Qualification-scoped trainer privacy; live denial beats cache; earlier validity end beats TTL; stale Alice grant/revoke after Bob's UID claim; delayed/repeated/conflicting offline uploads and unresolved attribution. |
| `STORE` | FIFO/skip/decline/expiry; position and member/group uniqueness; manual funded renewal; demand-based caps protect paid terms; clearance/junk-area handover; inactivity does not trigger automatic refund/release. |
| `INTEGRATE` | SMTP/provider throttling and fallback, GoCardless reauthorisation/failure alerts, MobilePay duplicate callbacks/payout reconciliation. S0: automated synthetic replacement/Keycloak/downstream flow, email-change identity stability and login/session boundaries. S1: feature-specific authentication restrictions. S6: legacy-switch continuity and actual claim/broker mappings. |
| `CUTOVER` | Repeatable opening imports, no receipt/income duplication, migrated free-day eligibility, new references, archive restrictions and an end-to-end rehearsal with representative readers. |

Use pure unit tests for calendars/prices, PostgreSQL tests for constraints/commands,
independent DbContexts/connections with real transactions for races, ASP.NET Core
request integration tests and a small real-browser suite for critical member/admin
journeys. Proposed tools are xUnit, WebApplicationFactory and Playwright. Inject
TimeProvider and fake HTTP/provider transports; never sleep to test business
deadlines. Bounded readiness polling is appropriate for real worker/container tests;
retain a small real-timer wiring test rather than test only manually invoked jobs.
Include fresh-install migration and Quartz upgrade checks, compiler/nullable/analyzer/format
checks and restricted-query/API permission tests in CI.

Run the **full correctness suite on PostgreSQL**, including journal-derived money
assertions, row-lock races, multi-process workers and browser/broker integration.
Use isolated disposable databases and synthetic credentials, never production data.
Keep fast unit/targeted checks convenient locally and explicit slow/integration
groups; CI must include all acceptance gates. Report missing prerequisites/skips
clearly; never silently skip an invariant's only test, use a different provider or
relax assertions to obtain green CI. Package docs are not runtime proof.

## 11. Delivery sequence

S0–S2 are implemented with their acceptance suites; see
[the foundation status](../docs/foundations.md), [S1 evidence](../docs/membership.md)
and [S2 evidence](../docs/billing.md). The remaining bounded build slices are below;
these are not operational cutovers. The association keeps one full-replacement
cutover after all five agreed delivery phases.

| Slice | Deliverable and exit check |
| --- | --- |
| S3: communications | Extend the existing queue/templates to drafts, rich localised newsletters/official notices, preference/fallback previews, test sends, progress and failures. Do not postpone authentication mail until this phase. |
| S4: access | Qualifications/trainers, resources, claim/revoke/history UI, live device API, cache export and retry-safe offline events. Pass a real reader contract/sandbox test, including reused UIDs and outages. |
| S5: storage | Runtime groups/positions, FIFO offers, atomic purchase-balance checkout/manual renewal, paid-term protection and handover. Pass multi-member contention and queue/date scenarios. |
| S6: readiness + single cutover | Migration rehearsal including legacy → replacement Keycloak/broker/downstream identity continuity, accountant reconciliation, privacy/retention and backup/erasure runbooks, provider/identity/device validation and operator/member instructions. Enable production only after cross-phase acceptance. |

The portal uses Blazor static SSR; login/logout remain HTTP-based Razor Pages.
`UiTests` verifies portal rendering and antiforgery-protected logout;
`InteractiveUiTests` uses a test-only component to verify opt-in interactivity,
transport reconnection/state retention and account-gate revalidation. Preserve the
authentication/infrastructure gates as later slices add real pages and domain
operations; the synthetic widget does not prove future interactive business
workflows.

Membership and billing are implemented in the same application; later slices
extend those contracts and must not retrofit a second billing path or replace the
foundational user model. See [`docs/membership.md`](../docs/membership.md) and
[`docs/billing.md`](../docs/billing.md) for their configuration, acceptance and limits.
