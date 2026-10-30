# Remaining decisions and implementation follow-up

This is the consolidated review index for `SPEC.md`, not a second specification
or an implementation backlog. Confirmed behaviour remains in that document.
Confirmed outcomes are labelled below; remaining engineering proposals are not
new policy decisions.

The [architecture blueprint](ARCHITECTURE.md) carries the reviewed domain design
into ASP.NET Core/C#, EF Core, PostgreSQL-only database profiles, containers,
Quartz.NET persistent jobs and local OIDC continuity testing. Its
[S0 checkpoints](ARCHITECTURE.md#12-review-gate-before-scaffolding) now concern
verification/package pins and detailed implementation, not another broad architecture
or membership-policy approval round. S0 is now implemented with independent .NET
acceptance evidence; see the [foundation status](../docs/foundations.md) for the
verified contracts and remaining limits, not the separate Django suite's results.

The [billing design](BILLING_DESIGN.md) describes the EF Core entity/command
foundation and execution traces reviewed as workable. Its review outcomes record
confirmed billing-credit order, multi-payment effective dates and removal of
expired lines. Independent fulfilment of fully funded lines is now agreed,
superseding whole-invoice fulfilment. Field-level engineering choices remain
proposals, not approved policy.

The [service architecture](SERVICE_DESIGN.md) now proposes definitions, member
enrolment/authorisation, lifecycle commands and the billing/access contract. Its
compatibility assessment calls for typed owners, stable benefit targets, purchase
authorisation context and purchase-scoped duplicate prevention, not a new billing
engine. Confirmed subscription rules now use one self-subscription setting for
joining/returning and remove unpaid subscriptions rather than keep services
inactive. Membership-only inactivity blocks service use and new invoices, but
neither pauses the coverage clock nor requires reapproval while the subscription
remains valid. Stop renewal is confirmed to preserve remaining paid/granted time
while stopping new charges; immediate administrator removal is separate.
Initial service free days are granted `Coverage`, automatically once per member/
service across subscriptions; this association grants 14 initial free door days.
Administrators can tick an initially unchecked box to grant the configured days to
a repeat subscriber. This is an audited admin grant, not an eligibility reset;
retries must not duplicate it, and ordinary members cannot select the exception.
Free coverage expiry and the first-payment deadline are independent. These replace
optional-service initial access grace, not membership or renewal grace.

The service foundation and payment timing have now been reviewed as workable:
default 14 calendar days for the association-configurable first-payment window,
and per-service renewal grace including zero (door default 7 days). Grace renewals
settle the original full period; no renewal grace extends an unpaid initial
subscription. Merging positive initial/alignment periods shorter than a week with
the following full period is also confirmed, including billing-period-change
bridges. Member transfers between available balances are confirmed when purchase
balance is enabled. Calendar phase/deadline choices, future discounts/grants,
honorary membership, resignation handling, pending deletion, inactive-member portal
access and fob reuse are now settled. Engineering and deployment follow-up are
separate, not more service-policy blockers.

[POLICY_REVIEW.md](POLICY_REVIEW.md) records the confirmed C1–C2, P1–P4, A1–A3 and
F1 answers with concrete examples. **The policy review set is settled:** rejoining
after actual final deletion requires a fresh verified application/approval and new
account, without restoring personal records, qualifications or fob registrations.
Minimal fob-ownership history must support cached-event attribution without keeping
a restorable profile; its fields, protocol and justified retention are follow-up work.

The agreed design uses journal-backed line holds with a clear admin funding/
repair view, not an extra `FundsReservation` model. Time-limited storage offers
and atomic purchase-balance checkout do not currently require another money-hold
purpose. A bare release must not be immediately undone by automatic funding of
the same open line; owner cancellation/review semantics remain important.

An unresolved detail is not automatically a question for the membership
administrator, or a reason to block architecture and all earlier work. Decide it
before implementing the affected behaviour; keep unresolved policy explicit.

## Billing simplicity review

The goal is ordinary, understandable bills requesting advance payment, not a
general subscription engine or the legacy rule of adding 31 days per service
payment. Ordinary renewals should be straightforward: prepare the next bill,
match its payment, fund the stated periods, and send the next bill when due.

The agreed exceptions have real implementation cost:

| Agreed behaviour | Why it adds work |
| --- | --- |
| Independent billing periods per recurring item, combined by period | Requires shared boundaries and bridging when an item changes group. |
| Keep the schedule after inactivity | Returning members need a prorated bridge instead of a fresh cycle. |
| Receipt-date pricing for delayed initial payments | The amount finally bought can differ from the emailed quote; excess becomes credit. |
| One outstanding bill with independent line fulfilment | Track line funding and completion; preserve paid coverage while repricing/expiring unfinished charges and deriving invoice totals. |
| Discounts and administrative coverage grants | Pricing and purchased/granted coverage cannot be inferred from bank receipts alone. |
| Automatic billing credit and optional purchase balance | Keep recurring corrections separate from deliberate one-time purchasing funds; use one bounded ledger, not mandatory wallet routing. |

These are not six separate engines or a proposed table list. The same small set
of billing facts can support them: schedules, current requests and their charges,
receipts/allocations, coverage, and retained credit. Detailed structure comes with
architecture. Being simpler for members does not mean storing only a paid flag.

**Proposed implementation restraint:** one billing component with shared calendar,
pricing and allocation rules; a bounded credit ledger and audit records; no
general ledger, debt collection, document-version platform, configurable workflow
engine or full-application event-sourcing framework merely to support these rules.
Manual corrections are already part of the intended administrator workflow.

### Outcome of the fixed-bill discussion

- Freezing bills until expiry is not adopted. Receipt-date repricing and credit
  for elapsed unfunded days remain desired, confirmed behaviour; their complexity
  is considered worthwhile. This does not reprice ordinary renewals paid during
  grace, whose original full period is already agreed.
- Independent billing-period choices remain in scope. No common-period-only
  restriction has been adopted.

### Confirmed short-period merging and remaining calendar details

- Confirmed: merge a positive initial/alignment paid period of fewer than seven
  calendar days with the following full period in the same group; issue immediately.
  Apply the same rule to short bridges from billing-period changes. This is a day
  threshold, not a small-amount threshold, and excludes days already covered free.
- C1 confirmed: show one invoice due date, the earliest relevant date among its
  unfinished charges, including an earlier first-payment deadline. No per-line due
  dates need be presented. Retain internal charge-specific timing, receipt-date
  repricing and line fulfilment; this is neither fixed-bill pricing nor shared expiry.
- This avoids asking members to pay a tiny bridging bill and another regular bill
  shortly afterwards. It adds a scheduling/aggregation rule, rather than removing
  the underlying proration code. Maintain one outstanding bill per group.
- Free coverage and the independent first-payment window remain intact. C2 confirmed:
  new intervals use the original joining-anniversary phase, preserving existing
  target-group calendars and issued/paid periods. The merge assessment at new-bridge
  preparation, not every later repricing, remains a labelled execution proposal.

### Confirmed invoice funding and balance separation

- Allocate to unfinished lines, membership first and then configured service
  priority. EUR 10 toward EUR 10 membership plus EUR 30 door access buys membership
  immediately. Partial funding of a line buys no fractional days. Paid lines are
  not held waiting for the rest of the invoice and are not undone by cancellation
  or expiry of its unfinished charges.
- Invoice-reference receipts accumulate against lines automatically. No
  replacement PDF is required for every receipt; display the current remainder
  and use receipt/remaining-amount notices where appropriate.
- Maintain two available balances, with working names **billing credit** (the
  user's "excess balance") and **purchase balance** ("cash balance"). Names are
  not final. Excess recurring payments, repricing surplus and returned unspent
  invoice funds go to billing credit; deliberate prepaid deposits go to purchase
  balance for one-time purchases.
- Automatically apply billing credit to recurring invoices, including partial
  funding, seeking to bring it to zero. Purchase balance is not automatically
  consumed by recurring bills. Its availability therefore does not prevent a
  correctly directed standing order or several invoice payments completing a bill.
- When purchase balance is enabled, members may explicitly transfer available
  money between their own balances in either direction, without admin approval.
  Transfers into billing credit trigger ordinary automatic allocation; held/spent
  amounts are not transferable available funds. No automatic purchase-balance sweep,
  new bank receipt or income is created. See the worked examples in the policy review.
- Purchase balance is an association-level configuration-file option, not an admin
  UI toggle. With it off, billing-credit correction still works. This replaces the
  previous single-balance prepaid/correction-only modes without a per-member
  auto-spend checkbox. Do not build an automatic disablement migration for existing
  purchase balances; a rare change after use is an operational matter outside
  initial scope, not permission to discard or sweep those funds.
- Allocated line funds are committed, not available in either balance. On
  cancellation/expiry return only that unfinished line's unused funds to billing
  credit. Reprice only unfinished lines, preserve their funding and return surplus;
  completed coverage stays intact. No transfer is another bank receipt.
- Line completion supplies accounting categories and purchased coverage. Both
  available balances and held funds on incomplete lines remain advances; spent
  amounts from paid lines are not still-held liability for the remaining bill.

This supersedes whole-invoice fulfilment and the proposal to turn every insufficient
invoice receipt into freely spendable credit. The reason is lower timing/expiry
coupling and faster useful coverage, not a separate selective-checkout feature.
Optional services left unpaid too long are now confirmed to be removed from the
member, ending their future billing rather than retaining inactive subscriptions.
Returning is ordinary resubscription by the member if permitted, otherwise by an
administrator. Initial nonpayment uses the independent 14-day default window;
established renewals use the configured service grace, with no stacking of the two.

## Policy review outcome

The grouped policy review is complete, including post-final-deletion rejoining.
The summaries below distinguish confirmed behaviour from implementation and
pre-deployment work, rather than asking the same choices again.

### Billing schedule and grace

- When an item changes to a new interval, derive its phase from the original
  joining anniversary. Joining 12 March and switching from monthly coverage through
  12 July to quarterly bridges to 12 September, then follows March/June/September/
  December boundaries. Preserve existing target calendars and issued/paid periods.
- **Service timing is confirmed, not outstanding:** first payment defaults to
  14 calendar days from issuing that service's initial request/adding its line,
  configurable association-wide. Free coverage is independent: its expiry can
  leave the subscription awaiting payment without use; request expiry cannot
  automatically revoke remaining granted time. Then remove when no coverage/window
  supports the unpaid subscription, releasing unused line funding.
- Established paid renewals get configurable per-service grace, zero allowed;
  this association's door default is 7 calendar days after the paid term ends.
  Full funding during grace pays the original renewal period, not a new prorated
  period. At unpaid grace expiry remove the subscription and release unfinished
  holds. No renewal grace stacks onto an unpaid first request/free period. Bank
  receipt rather than import time determines timeliness. See
  [SERVICE_DESIGN.md](SERVICE_DESIGN.md) for the reviewed lifecycle, stop-renewal and
  admin free-days exception. Confirmed inactive-portal access is summarised below.
- Confirmed initial billing skips free-covered dates and uses the later of first
  uncovered day/funding effective date. Rollover preserves funding and the original
  deadline. C1–C2 in [POLICY_REVIEW.md](POLICY_REVIEW.md) confirm a single public
  earliest due date and the original-anniversary phase. Internal line timing must
  survive invoice revisions without resetting grace/expiry.

### Credit use and corrections

- Design automatic billing-credit allocation and the purchase-balance checkout UI.
  Apply the confirmed earliest-due-date, then issue-order/ID rule across valid
  open groups. Never spend allocated funds or available balances twice. Test
  effective-date and out-of-order receipt handling against the confirmed policy.
- Design auditable administrator correction/reassignment, reversal and withdrawal/
  refund commands, including exceptional reversed deposits whose credit was already
  spent. These require explicit treasurer review, not negative balances, automatic
  clawbacks or fabricated fee debt; do not build a general refund-policy engine.
  Purchase-balance disablement after use is not an initial policy/implementation
  workflow to design.
- Transfers are now confirmed in both directions when purchase balance is enabled.
  Explicit purchase-to-billing transfer supplies recurring-bill use through the
  normal allocator; a separate direct purchase-balance payment button is not required
  by that decision. Validate available source funds atomically, not held/spent money.
- Use manual, reasoned corrections for mistaken allocations;
  no automatic reshuffling of completed purchases when configuration changes.
  Membership-first/optional-service priority is restored within bills; deterministic
  tie-breaking needs a technical proposal. Billing-credit allocation across groups
  remains deliberate automation, not forbidden correction behaviour.
- Confirm the detailed accounting export/category mappings with the accountant.
  Generic CSV, reconciliation and a Beancount-oriented posting summary remain
  proposals, not a requirement for a general-purpose accounting application.

### Administrative pricing and service changes

Confirmed P1–P4 examples are in [POLICY_REVIEW.md](POLICY_REVIEW.md).

- New discounts apply to unused future time equally for unpaid/prepaid members.
  Reprice unfinished lines, preserving funding and timing and returning surplus.
  Paid future purchases use administrator-reviewed audited credits without rewriting
  original purchase/coverage facts; no routine retrospective repricing or bank refund.
- Coverage grants cancel/reduce now-covered unfinished charges and return unused
  funding to billing credit. Preserve calendars and independent service lines.
- Honorary members use ordinary 100% discounts with a very distant explicit expiry
  (e.g. approximately 1,000 years), not a separate exemption feature. Engineering
  must establish eligible zero-price coverage without pretend receipts/movements or
  permanent awaiting-payment status; ordinary lifecycle gates still apply.
- Resignation may drop now-unusable services with no automatic credit/refund for
  unused purchased time. Keep enough financial/removal audit for rare manual
  investigation/handling. This is system scope, not a legal determination of refund
  rights. Available balances/unused holds retain their separate financial claim.
- Stop renewal while membership continues instead preserves remaining covered
  time/completed future periods, cancels only unfinished charges and releases their
  holds, with no new unpaid grace or automatic refund. Storage's no-early-release
  rule remains separate. No extensive refund-entitlement/valuation engine is needed.

### Account lifecycle exceptions

See A1–A3 in [POLICY_REVIEW.md](POLICY_REVIEW.md).

- Deletion waiting time is a configuration-file option, default 14 days. A batch
  every 14 days deletes only eligible accounts, normally giving 14–28 days' actual
  waiting time. Failed/delayed runs may take longer; never delete early. An admin
  may cancel before actual final deletion, re-evaluating surviving standing/coverage
  rather than restoring expired time or separately revoked access.
- Pending-deletion receipts fail matching even with formerly recognised references
  and appear to the treasurer as failed-match payments. Preserve source facts and
  money, with no allocation, coverage or deletion cancellation. Cancelling deletion
  may still need manual payment repair; restoration/retries do not silently rematch.
  Final deletion proceeds despite credit, preserving the separate financial claim.
- Final deletion removes the account and personal records, not just a flag. Rejoining
  requires a fresh application, verification and approval with a new identity; do not
  retain a shadow profile, reconstruct qualifications/fobs/subscriptions or relink
  surviving claims/history by email. Keep only separately justified minimal financial/
  audit records under their retention rules, not data saved for future rejoining.
- Inactive members retain their own profile and necessary membership-payment
  information, with convenient member-services-page access and existing resignation/
  privacy routes. Storage, access-control tools and other members-only areas may be
  restricted; no blanket login ban or unrestricted-portal promise is implied.
- Initial-grace expiry notice wording/delivery is implementation follow-up, not a
  further grace period or automatic membership termination policy to decide.

### Credential ownership

See F1 in [POLICY_REVIEW.md](POLICY_REVIEW.md) for the lost/revoked fob example.

- Revocation removes the current registration and releases its UID for ordinary
  claiming/reuse; no administrator reassignment is needed. A still-owned fob denied
  for inactivity/qualifications is not released. Historical events identify their
  original member, with UID only supporting context, including delayed offline
  uploads. Test that UID reuse cannot reattribute history; no blocking tombstone.
- Keep minimal ownership history per fob to backtrack cached requests across
  revocation/reuse. Proposed cache/report assignment-grant versions let the server
  recover original decision context: UID plus event time can select the wrong owner
  when a stale grant is used after reclaiming. Preserve reported decisions without
  changing live-first/TTL/revocation rules or claiming to identify the physical user.
  Do not keep deleted profiles for this; identifiable history needs justified retention.

## Technical design and implementation follow-up

These need engineering proposals/tests, not a membership-policy interview for
every field or timer. Review defaults in batches where appropriate.

| Area | Remaining work |
| --- | --- |
| Architecture | S0 proves Quartz scheduling in the actual EF transaction, rollback/commit visibility, restart, worker-death/shutdown recovery, bounded ordinary retries, cancellation/overlap, crash-loop/stale-completion visibility, calendar behaviour and synthetic replacement/Keycloak/downstream integration. Pin compatible supported packages; enable persistence, clustering, job recovery, retries and database history explicitly. No production access, custom queue/lease framework, plugin platform or multi-tenancy. |
| Identity and login | S0 implements Identity/OpenIddict, the confirmed [365-day idle refresh lifetime](SPEC.md#login-and-password-setup), exchange-time expiry, daily cleanup, account gates and persistent keys; see [evidence and limits](../docs/foundations.md). S1 still needs real admission/password/deletion workflows. Password-guessing protection remains deferred: CAPTCHA after repeated failed portal/admin logins is a proposal; decide thresholds/shared tracking before public deployment. Django-specific library limits are historical, not permission to weaken OpenIddict validation. |
| Development and tooling | Use real PostgreSQL for schema, command, financial and worker tests; no SQLite provider/migrations or EF in-memory correctness substitute. Keep pure C# decimal/calendar tests database-independent. Pin the .NET SDK and NuGet dependency graph; configure nullable analysis, analyzers, formatting and test groups. Registered job types/minimal string data only. Persist Data Protection keys and separate OIDC signing keys; test context lifetimes and release-time schema/schedule installation. |
| Delivery | Review S0–S6 build slices in ARCHITECTURE.md: essential queued mail in membership, real initial-invoice integration in billing before any operational release, then communications/access/storage and one cutover. Sequencing is proposed implementation planning, not new member policy. |
| Calendar and prices | Test month-end/leap-year anchors, original-anniversary phases, rounding ties, bridges/merges, discount splits and free grants crossing boundaries. Test zero-price coverage, grant-driven charge reductions and reviewed future-purchase credits; no fake payments. |
| Financial processing | Finalise non-grouped one-off invoice ownership/reference routing on the same billing path. Test concurrent spending/grant retries, admin checkbox exceptions, stop renewal, independent initial windows and renewal grace, original-period settlement and no initial/grace stacking. Test expiry without an invoice and delayed receipts without undoing explicit stop/removal. Preserve eligibility, completed facts and typed owners. |
| Bank feed | Map provider dates to bank receipt dates, handle failures and renewal; choose alert recipients, thresholds and repeat suppression. Confirmed feed is GoCardless nightly. |
| Payment providers | Define purpose metadata, fees, settlement and bank reconciliation. Bank transfers and MobilePay are in the target scope; an initial SEPA route was discussed. Confirm the MobilePay delivery milestone rather than silently dropping support or assuming it already exists. |
| Member/admin UI | One public earliest invoice due date, preserving internal charge timing; clear coverage/credit and correction history. Keep inactive profile/payment/services-page access convenient while restricting other tools as appropriate. No invoice-version archive is required. |
| Email | Choose the rich-text editor, stored content format and server-side sanitisation library together; neither dependency is selected yet. Link expiry/resend, template safeguards, retries, rate limits and proposed operational-message priority. Specify rejection-send acceptance followed by immediate erasure, including terminal provider failure/minimal payload retention. Finish initial-grace notices and initial translations; Swedish remains possible, not committed. Preserve explicit language fallback and opt-outs. |
| Access protocol | Validate proposed TLS/device credentials/resource scoping; choose assignment/grant version and cache-generation context for cached-event attribution, exact API fields, timestamp validation and retry-safe outbox protocol. Live-first, denial, revocation and TTL rules stay unchanged. |
| Access/training records | Design minimal per-fob ownership history independent of current registrations, transactional assignment/revocation and administrator visibility. Test stale-cache attribution after UID reuse, delayed uploads and unresolved context without preserving deleted profiles. Client failover without usable cache is a client policy, not a fictitious server grant. |
| Deletion | Test waiting-time eligibility/fortnightly cadence, admin cancellation before actual deletion and failed-match dispositions across restoration/reimports. Final deletion erases personal records; fresh admission must not resurrect old records or relink retained claims/audit by email. Preserve only justified separate financial/audit facts. |
| Storage | Choose initial configurable duration/window defaults and test queue/handover transitions. Per-group settings, expiry/decline removal, inactive-entry handling and renewal limits are agreed. |
| Exports | Specify ledger/export fields, posting summaries and reconciliation tests once accountant-facing mappings are reviewed. |

## Before deployment, not reasons for a universal billing engine

- **Retention/privacy validation:** document purpose, lawful basis and appropriate
  retention for approval, access and qualification audit records, including linked
  IDs and deleted subjects; finalise authorised visibility and privacy information.
  Indefinite access-log retention is the requested default, not an established
  compliance conclusion. Keep financial claim records separate from the live
  registry and validate their identifying-data retention. Set minimal fob-ownership
  history/link retention and erasure compatible with the supported offline-reporting
  period; history is not a reason to preserve deleted profiles or assume indefinite
  identifiable ownership retention. Historical IDs are not automatically anonymous.
- **Exceptional financial handling:** validate refund rights/association terms
  outside the automation scope. No automatic unused-service refund on resignation
  is not a legal waiver; retain sufficient evidence for manual investigation and
  validate the accountant-facing correction/reversal mappings.
- **S6 migration validation:** inventory integrations and available source data;
  rehearse OIDC locally with synthetic users, retaining the realm/broker alias while
  switching registries and asserting stable user IDs/downstream subjects. This proves
  the mechanism, not actual production mappings. At cutover, the operator confirms
  actual Keycloak broker links, issuer/client/claim settings and downstream identities
  (a redacted export helps); no production access/testing is required for S0. Map opening
  coverage and credit without replaying historical receipts; decide the legacy
  archive/import boundary, validate imperfect mappings manually, rehearse cutover
  and prepare new-reference/password-reset instructions. Seed prior-service/
  initial-free eligibility facts so migration does not make returning/current
  subscribers look like first-time subscribers entitled to another free period.
- **Operational configuration:** supply bank/provider credentials, email fallback
  settings, message templates and configured grace/deletion/queue periods. Their
  existence does not require resolving every deployment-specific value now.

## Already deferred or outside initial scope

- Billing-date-move tool; automatic credit expiry/forfeiture.
- Automatic migration from purchase-balance-enabled to disabled after funds have
  been deposited; the feature is configured per deployment, not toggled in the UI.
- Delegated newsletter-only permission and scheduled manual bulk sending.
- E-invoicing, future goods/kiosk interfaces and a global cross-group storage cap.
- Full machine inventory/maintenance and training quizzes; basic resource
  enabled/unavailable control remains in scope.
- Challenge-response NFC, future-date storage bookings, voluntary storage early
  release, and self-service credit withdrawals are not initial requirements.
- A VAT engine, general ledger, collectible arrears and compulsory wallet routing
  are outside the agreed model.

Some deferred policies still require manual handling before deployment, not silent
implementation guesses. A candidate legal credit-expiry period is not an approved
automatic rule. No confirmed feature is removed solely to shorten this index.
