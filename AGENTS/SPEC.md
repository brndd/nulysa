# Membership registry and portal specification — ASP.NET Core

This is the working specification for Nulysa, the replacement for the existing
membership registry and portal. `AGENTS/` holds the planning documentation.
This edition adapts the Django project's specification to C#/ASP.NET Core,
PostgreSQL and Quartz.NET. Domain policies and their confirmed/proposed/open status
are preserved; previous implementation results do not certify this new platform.

The document describes intended behaviour before implementation details. Existing
code is a reference for migration and integrations, not an authority on how the
replacement should behave.

## How to read and maintain this document

- **Confirmed** means an agreed requirement or decision, recorded in this
  specification. This document is the authoritative requirements reference.
- **Proposed** means a suggested approach awaiting agreement; tentative ideas
  remain proposals until explicitly confirmed.
- **Open** means a decision is still needed. Implementers, including AI
  assistants, must not silently invent policy to fill these gaps.
- The assistant does the drafting. The domain expert supplies decisions and
  corrections through short rounds of questions; there is no separate ticketing
  process or requirement to write formal specification prose.
- Keep decisions with the relevant subject. Split this document only when doing
  so makes it easier to use.
- Remaining questions are consolidated and prioritised in
  [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md). It separates policy review, technical
  design, pre-deployment validation and already-deferred work. Not every open
  detail requires another administrator-policy decision or blocks architecture.
- [BILLING_DESIGN.md](BILLING_DESIGN.md) is the shorter human-review proposal for
  EF Core billing entities, operations and execution traces. This specification
  remains the detailed behaviour reference; that design is not automatically
  approved policy or a requirement to reread this entire document.
- [SERVICE_DESIGN.md](SERVICE_DESIGN.md) proposes service definitions, enrolment/
  authorisation, derived eligibility and the billing/access contract. Its foundation
  has been reviewed as workable. Subscription/removal, stop-renewal and free-grant
  behaviour, service timing and calendar policies are confirmed; field-level
  engineering proposals are not automatically approved.
- [ARCHITECTURE.md](ARCHITECTURE.md) is the first system-wide technical blueprint:
  proposed stack/modules, identity, transactions, durable work, erasure, integration,
  acceptance tests and build slices. It reuses the billing/service foundations;
  its overall direction is accepted in principle. ASP.NET Core/C#, PostgreSQL-only
  development/production/CI, containers, Quartz.NET persistent background work and
  local OIDC continuity testing are the selected direction. Detailed fields,
  configuration and package pins still require implementation/verification.
  The ASP.NET Core S0 implementation and its independent acceptance evidence are in
  [the foundation status](../docs/foundations.md).

## 1. Purpose and constraints

**Confirmed**

- Serve small-to-medium associations, typically up to approximately 1,000 members
  and definitely fewer than 10,000. The initial association is a Finnish
  hackerspace with more than 500 members.
- Reduce administrative and accounting work and make member self-service clear.
- Replace the service-subscription-centric model rather than reproduce its
  limitations in a new implementation.
- Provide purpose-built administrator tools instead of relying on a generic
  database administration interface for ordinary work.
- Support actual removal of personal information, not just an inactive flag.
- Plan for AI-assisted development by a small volunteer group.
- Prefer text documents and an efficient collaborative planning process.
- Use C# and ASP.NET Core on a supported .NET LTS release. Describe EF Core entities,
  relationships, database constraints and explicit transaction boundaries; let EF Core
  migrations manage application schema. Quartz's versioned schema has its own
  controlled installation/upgrade path.
- Use a Blazor Web App as the primary UI, with static server-side rendering (SSR)
  and ordinary HTTP forms by default. Enable Interactive Server selectively for
  richer pages/components, such as a graphical storage-position selector; do not
  make the whole application interactive. WebAssembly, Auto render mode and a
  separate client project are not initial scope. This replaces the Razor Pages/HTMX
  direction; it does not change domain policies or require a separate frontend API.
- Use PostgreSQL for development, production and database tests, with containerized
  production deployment. The .NET application and worker may run directly on the
  host against a local PostgreSQL container; a fully containerized development
  workflow is optional. SQLite support, provider-specific migration sets and a
  disposable SQLite schema are not initial scope. See the
  [development contract](ARCHITECTURE.md#confirmed-deployment-and-development-profiles).
- Billing should present conventional, clear advance-payment bills rather than
  ask members to understand separate service-payment counters. Simplify the
  legacy behaviour where each service payment adds 31 days; do not reproduce it
  behind new PDFs or turn the replacement into a general-purpose billing platform.

**Proposed direction**

- Use C#'s static types and nullable-reference analysis, explicit composition and
  ordinary named commands. ASP.NET Core is selected, not an outstanding framework
  choice; detailed tooling and package versions are pinned during scaffolding.
- Features should be separable and not unnecessarily specific to one association.
  The extent of reuse and the extension mechanism remain open; this is not yet a
  commitment to a general-purpose plugin platform or multi-tenant product.
- Avoid inherited architectural traps and make trade-offs explicit rather than
  promise that the project will never incur technical debt.

## 2. Delivery phases

**Confirmed**

| Phase | Scope |
| --- | --- |
| 1. Membership | Applications, approval, resignation and other membership lifecycle operations; registry administration; deletion; agreed connections to payment status. |
| 2. Billing and payments | Recurring charges and one-off invoices; payment processing and accounting support; payment-dependent membership status and paid services. |
| 3. Communications | Email delivery, templates, member preferences and newsletter tools. |
| 4. Access control | One component for doors and machines; support multiple doors, machine qualifications and any required paid services. |
| 5. Storage | Storage availability, queues, reservations, payment and rental administration. |

- **Registry release:** phases 1 through 3. The system can operate purely as a
  membership registry at this point.
- **Full replacement:** all five phases. Matching the old system's limitations is
  not a useful intermediate feature-parity target.
- These are delivery phases, not isolated design silos. Establish their shared
  concepts and connections before committing to an implementation.
- For this association, plan one operational cutover to the full replacement,
  rather than a registry-only deployment alongside legacy billing/access/storage.
  The payment-processing and reference-format differences make split operation
  impractical. Development and delivery can still proceed in phases.

**Implementation planning:** sequence essential application/login emails with the
features that depend on them; see [technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
A registry-only deployment remains useful for other associations, but is not this
association's planned migration path.

## 3. Shared concepts and boundaries

### Proposed vocabulary

These are conceptual distinctions, not database tables or a settled data model.

| Concept | Meaning |
| --- | --- |
| Identity/account | Who a person is and how they log in; independent of a mutable email address. |
| Membership | The association's relationship with a person, including admission and departure. |
| Charge/payment request | The price requested in advance for an item or period; not a collectible debt. |
| Invoice | An advance payment-request document covering one or more charges; issuing it does not create a receivable. |
| Payment | Money received; distinct from what it settles. |
| Payment allocation | The connection between received funds and the requested purchases they fund. |
| Paid entitlement | A benefit whose availability depends on an applicable paid service. |
| Access permission | Permission to use a particular door or machine, subject to its requirements. |
| Qualification | A recorded training or other prerequisite for using a machine. |
| Storage reservation/rental | An allocation of physical storage with its own lifecycle and charges. |

### Confirmed boundaries

- Approval establishes membership. First payment is not a prerequisite for
  admission. Subsequent nonpayment can end membership.
- All billing is strictly in advance. Unpaid requests do not become debt owed to
  the association, and grace-period benefits do not create accounting debt.
- Only unpaid membership fees can trigger termination for nonpayment. Unfunded
  door access or other services do not themselves end association membership.
- Payment standing is determined from the membership fee line and coverage, not
  an aggregate unpaid-invoice flag. A combined bill can remain open for door
  access after its membership line is paid; that does not make membership overdue.
- An inactive person is still a member, with restricted benefits or portal
  functions. Ending membership is a separate, later step.
- Grace expiry makes membership inactive and pauses membership billing.
- Membership termination is a manual administrator action, not an automatic
  consequence of remaining inactive for a configured period.
- Termination initiates account deletion, including the pending-deletion warning
  and scheduled final deletion. There is no retained "terminated" non-member state.
- An inactive member whose account still exists and is not pending deletion can
  resume without settling the old unpaid membership invoice. Keep the existing
  shared billing schedule and prorate the return period from reactivation to its
  next boundary; reactivation does not reset the billing anchor.
- Once an account is marked for deletion, payment alone cannot rescue or
  reactivate it. The person must contact an administrator.
- Whether payment automatically reactivates membership remains configurable.
  Services instead use subscription/removal: after removal, the member must
  subscribe again if self-subscription is allowed, otherwise an administrator
  must add the service again. One self-subscription setting governs both first
  subscription and returning; no separate service-reactivation setting is needed.
- Membership standing uses the payment/coverage and grace rules specified below;
  it is distinct from whether a particular optional service is funded.
- All member services require active association membership, including membership
  that remains active during grace. Already-paid service coverage does not bypass
  this eligibility requirement when membership becomes inactive.
- Membership-only inactivity does not pause or remove an otherwise valid service
  subscription, freeze its coverage or require administrator reapproval on return.
  Coverage keeps expiring normally; a service that expires and is removed instead
  requires normal self/admin resubscription. Inactive members retain profile and
  membership-payment access, including convenient member-services-page access;
  storage, access-control tools and other members-only areas may be restricted.
- Door access may require a paid service. Some machines may also require a
  specific paid extra service in addition to other prerequisites.
- Door and machine access share one component, but must not be presented as
  indistinguishable peers in the same user-interface grouping.
- Changing email must not break identity or SSO.
- Cancelling a service must not destroy its payment-reference history or make
  historical accounting exports incomplete.

### Proposed system-wide guarantees

- Membership standing, login availability, paid entitlements and personal-data
  deletion are distinct decisions, even when one triggers another.
- Repeated processing of the same financial input must not credit money twice.
- Important administrative and financial changes must remain explainable and
  correctable without silently rewriting history.
- Retention and erasure rules must reconcile privacy requirements with justified
  record retention. Keeping everything forever and deleting every record on
  resignation are not assumed policies.

## 4. Phase 1: Membership registry

### Confirmed behaviour

- People can apply for membership, applications can be approved, and members can
  resign. Other lifecycle paths and their rules remain to be specified.
- Applicants have no portal login access while awaiting approval. Approval grants
  login access as well as establishing membership.
- Applicants must verify their email by clicking an emailed link before their
  application appears in the administrator approval list. Unverified applications
  cannot be approved.
- On rejection, send an email with an optional administrator explanation, then
  immediately and permanently delete the application and associated applicant
  data. Do not enter the member pending-deletion workflow.
- Approval establishes membership even if the first membership fee is unpaid.
- Only nonpayment of membership fees can subsequently end membership for
  nonpayment. Other unpaid services may affect their corresponding entitlements,
  but do not themselves end membership.
- An inactive member remains an association member, with restricted benefits or
  portal functions. For recurring membership invoices, inactivity begins
  immediately when the unpaid membership line's grace expires, and membership
  billing pauses at that point. Initial-payment rules are described separately.
- Any portal restrictions for overdue membership fees must preserve login to view
  bills and pay, maintain personal details, resign and make personal-data/privacy
  requests while membership continues. Keep the member-services page convenient
  to reach; storage, access-control tools and other members-only areas may be
  restricted during inactivity. For renewals, reaching the membership charge's due
  date alone does not make membership inactive; its initial grace instead ends at
  its own due boundary, not another line's earlier invoice-visible date.
- Progression through overdue status and inactivity is automatic. Invoice lead
  time and grace-period timing must be configurable, not fixed to the illustrative
  intervals below. Membership termination requires a manual administrator action;
  elapsed inactivity alone must not terminate membership.
- Administrators need usable tools to maintain the registry and delete people.
- Administrator panels must help identify long-inactive members, for example by
  highlighting inactivity beyond a chosen duration or sorting longest-inactive
  members first. Exact presentation and thresholds remain to be specified.

### Permissions and information visibility

**Confirmed**

- Keep board-level administration simple: board administrators have access to all
  membership, finance, association-configuration and communications functions. Do
  not split these into separate membership/finance board-administrator roles.
- Sysop/superuser capability is separately granted technical authority. Board
  administration does not grant it; the same account may hold both capabilities.
  Unless explicitly stated otherwise, "administrator" elsewhere in this specification
  means a board administrator, not a sysop. The superuser-only System Console
  is distinct from the day-to-day administrative workspaces below.
- Defer the separate communications permission to a later release. Initial-product
  communications access is restricted to full administrators.
- When implemented, delegated communications access is newsletter-only, so a
  member can volunteer for newsletter work without full administrator access.
  It does not include transactional templates for authentication, invoices or
  membership notices. Administrators retain full communications access.
- Ordinary members access their own records and self-service functions only.
  There is no member directory or ability to browse other members' profiles.
- The future newsletter permission does not grant general registry, financial or
  System Console/superuser access. Its exact recipient-data visibility remains to be
  specified with that later feature.
- Non-administrator trainers have qualification-scoped grant/revoke authority as
  defined in phase 4. This narrow operational permission does not introduce layered
  board-administration roles or grant general registry/finance access.

### Proposed administrative workspaces

These organise already-discussed jobs, not a final screen layout or a commitment
to additional bulk operations.

| Workspace | Principal tasks |
| --- | --- |
| Attention dashboard | Identify verified applications awaiting approval, long-inactive members, pending deletions and payment issues needing intervention. |
| Registry and member details | Search/filter records; understand membership and service standing; edit details; inspect relevant history; perform authorised adjustments, termination and restoration. |
| Application review | Read the submitted details/message, highlight under-18 applicants, approve or reject with an optional explanation. |
| Payments and accounting | Review imported payments, allocations, credits and mismatches; correct mistakes; handle refunds and retained credit; export and reconcile records. |
| Configuration | Manage supported association settings and business permissions, not superuser grants; the GUI versus configuration-file boundary remains open. |
| Communications | Prepare and send newsletters/bulk messages and manage templates within the user's permitted scope. |

Existing requirements for recorded reasons, expiry dates and audit trails apply
to the relevant actions. Destructive operations should make their consequences
clear rather than require editing raw database records.

### System Console

**Confirmed**

- Provide a superuser-only **System Console** web area for technical inspection,
  troubleshooting and exceptional circumstances. It supplements, rather than
  replaces, the board's membership/payment/subscription/storage workspaces and
  operator maintenance commands. It is a cross-cutting "under the hood" view of
  the whole application, not a UI limited to the `operations` module. Each module
  retains ownership of its data, rules and commands.
- Enforce superuser authorization on routes and underlying queries/actions, not
  merely navigation visibility. Ordinary members, trainers and board administrators
  without superuser capability must not access these details. Board administration
  must not be able to grant superuser capability through ordinary settings or member
  editing. Operator-only `grant-superuser <user-id>` / `revoke-superuser <user-id>`
  commands assign/remove the Identity `Superuser` role for an existing account;
  ordinary login/account gates still apply. Check the live assignment when accessing
  the console so an old cookie cannot retain revoked authority.
- The initial area is read-only. Add useful diagnostics alongside features as they
  are implemented throughout the delivery slices; there is no fixed inventory of
  screens to complete upfront, nor does this requirement retroactively expand S0.
  Potential views include release/schema status, schedules and failures, integration
  activity, authentication state, effective configuration, and internal records,
  relationships or operation keys from any domain module. These are examples, not
  an exhaustive or mandatory initial checklist. Show useful technical detail rather
  than only green/red summaries, linking related records where helpful.
- Superusers are trusted sysops assumed to have SSH access to the application
  server and its data already; the console is not intended to conceal server data
  from them. Elaborate console-specific redaction machinery is not required.
  Nonetheless, do not display sensitive user data or credentials where they are
  unnecessary for the diagnostic task. Privacy/retention rules still apply: do not
  preserve erased profiles or duplicate personal data merely to populate the console.
  This does not relax existing protections for logs, job payloads, exports or
  lower-privilege interfaces.
- Any later repair action needs an explicitly designed, authorized application
  command with preview/confirmation, a recorded reason and an audit trail. Superuser
  status does not bypass domain validation, financial invariants or transactional
  boundaries. No unrestricted database-row editor, SQL console, arbitrary job
  execution or schedule editing is implied by System Console access. An uncertain
  external send still requires reconciliation, not blind retry.

### Application intake and verification

**Confirmed**

| Field | Requirement |
| --- | --- |
| Name | Required |
| Email address | Required; verified through an emailed link before entering the approval list |
| Municipality | Required |
| Date of birth | Required |
| Preferred language | Required |
| Freeform message to administrators | Required |
| Acceptance of membership terms | Required |
| Phone number | Optional |
| Nickname | Optional |
| Matrix ID | Optional |

- Email verification must precede visibility in the approval list, both to reduce
  spam and to check that the email address works. Verification itself grants no
  portal login access.
- Automatically delete applications that remain unverified beyond a configured
  retention period. The default is 24 hours. Configuration-file control is
  sufficient; no administrator GUI setting is required.
- Highlight applicants under 18 in the approval interface so administrators can
  identify cases that may need parental consent or other age-related checks.
- Do not enforce a hard automatic age cutoff. The under-18 highlight assists
  administrator review; it is not an automatic rejection rule.
- Any required parental consent is handled outside the portal. Do not collect or
  store consent documents, confirmations or other parental-consent records in the
  portal; underage applicants are uncommon and this process remains external.

**Implementation follow-up:** verification-link expiry/resending; see
[technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
Legal consent checks are an external administrative responsibility, not an
age/consent workflow to implement in the portal.

### Login and password setup

**Confirmed**

- Members log in using email and password.
- The welcome message sent on approval includes a link to set the initial password.
- Provide ordinary email-based password resets.
- Password setup and reset must not bypass the no-login-before-approval rule or
  the immediate access block on accounts pending deletion.
- OIDC refresh tokens have a one-year (365-day) idle lifetime. Successful refreshes
  renew that lifetime; this is not an absolute one-year limit on an actively used login.

**Deferred login protection:** solve automated password guessing for both portal
and administrator login before public deployment. ASP.NET Core Identity's lockout
and request-rate-limiting features require deliberate configuration and do not
settle the protection policy by themselves. A CAPTCHA after several unsuccessful attempts
is the proposed approach, not a settled implementation choice. The threshold,
challenge and failed-attempt tracking across processes remain to be designed;
do not implement CAPTCHA in the current foundation slice. See
[technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).

### Profile maintenance and approval accountability

**Confirmed**

- Members can edit their own personal details without administrator intervention,
  including name, date of birth and contact/profile information. System-managed
  identifying details such as member ID are not member-editable.
- A changed email address must be verified before replacing the current address,
  to guard against broken email addresses. Changing email must not change the
  person's underlying identity or break SSO.
- Any single authorised membership administrator may approve an application.
  Approval does not require multiple administrators or a board-decision reference.
- Record which administrator approved the application and when.

### Administrative membership adjustments

**Confirmed**

- Support two distinct adjustments rather than one generic active-status override:
  1. Extend the currently paid-for portion of membership by one or more whole
     months without requiring another payment for the extension, even if the
     selected billing period is longer than one month.
  2. Apply a discount with an explicit expiry date.
- Do not support arbitrary-day administrative extensions. If finer compensation
  is wanted, a monetary discount is sufficient; it need not promise an exact
  number of extra active days.
- Discounts support both percentage reductions and replacement prices (for
  example, a reduced monthly price), with an explicit expiry date in either case.
- When preparing an invoice for a period spanning a discount's expiry, split the
  charge at the expiry boundary. Apply the discounted price only to the days
  before expiry and the normal price thereafter, using the agreed actual-day
  proration. This applies to both discount types and all billing-period lengths;
  a discount valid at the period's start must not cover the entire period after
  its expiry.
- Worked example: a period runs from 12 March to 12 April, and a 50% discount
  expires at the start of 27 March. Price 12 to 27 March at the discounted rate
  and 27 March to 12 April at the normal rate.
- Both require a recorded reason and an explicit end date. For a duration
  extension, the end date is the new paid-through date; a discount has its own
  expiry date. There is no separate indefinite mode; use an appropriately distant
  end date for an effectively indefinite adjustment.
- Keep an audit trail identifying the member, the administrator, when the action
  occurred, the reason and what changed, including the relevant end date. Changes
  to or withdrawal of an adjustment must also be auditable.
- Do not create fictitious payments. A duration extension changes the recognised
  covered period; a discount changes pricing, not whether payment was received.
- Expiry must not automatically terminate membership or mark the account for
  deletion; nonpayment termination remains an administrator decision.

**Confirmed adjustments to future periods (P1–P2)**

- New discounts apply to unused future time from their explicit effective date,
  equally for unpaid and prepaid members. Reprice applicable unfinished lines,
  preserving committed funding and original timing; return surplus to billing credit.
- For completed purchases of future time, use an administrator-reviewed, audited
  credit adjustment. Keep original purchase/payment/coverage facts rather than
  rewrite them. This explicit correction is not routine automatic retrospective
  repricing or a bank refund. Example: a 50% discount effective 1 July makes a
  EUR 30 July charge EUR 15; if July is already purchased, the reviewed credit is
  EUR 15. Already-used time is not automatically refunded.
- A whole-month coverage grant cancels/reduces unfinished charges for the now-covered
  days and returns unused/surplus funding to billing credit. Keep the shared calendar
  and independent service lines intact; ordinary credit allocation may then use the
  returned money elsewhere. Example: granting July membership cancels its unfinished
  EUR 10 charge and returns its EUR 5 hold, without changing July's door charge.
- Retain relevant old/new amounts, dates, reason and actor. Superseded PDFs or full
  invoice versions need not be archived. Detailed correction commands remain design
  work; see [the billing design](BILLING_DESIGN.md).

**Confirmed honorary membership and zero-price processing (P3)**

- Use the ordinary percentage discount at 100%, with an explicit very distant expiry
  such as approximately 1,000 years. No separate fee-exemption feature or indefinite
  adjustment mode is needed.
- Establish eligible fully discounted periods as covered without waiting for money,
  creating fictitious receipts or zero-value money movements. Do not leave a zero-price
  period permanently awaiting payment or trigger nonpayment inactivity for it. If
  repricing releases real held money, record that release normally.
- Retain the discount/pricing basis and retry-safe coverage origin. Normal dated
  pricing resumes at expiry, including a split period where appropriate. This does
  not bypass admission, explicit subscription removal or pending-deletion gates.
  Exact model/command representation remains engineering work, not a new policy gap.

### Approval-log contents and deletion

**Confirmed design decision**

- Keep the approval event to the approved person's database user ID, the approving
  administrator's database user ID and the approval timestamp. Do not copy names,
  email addresses, birth dates or application messages into the audit entry.
- The approval entry can survive account deletion with the historical numeric ID;
  do not retain a member profile merely to keep the entry identifiable.
- Historical IDs must not be reassigned to another person.

**Privacy qualification / open retention policy**

- A database-specific ID is not automatically anonymous merely because its user
  row was deleted. Retained invoices, exports, other logs, backups or other
  reasonably available information may still link it to a person.
- Treat the entry as personal data while such identification remains reasonably
  possible. This also applies to the approving administrator's ID while it remains
  identifiable. Truly non-identifying event data is a different case.
- For identifiable entries, specify the lawful basis, justified retention period,
  access restrictions and privacy-notice information. The ID-only design reduces
  retained data but does not itself establish a legal basis for indefinite storage.
- No specific retention duration or Finnish statutory obligation is assumed here.

Guidance for resolving this question (not a determination of the association's
specific legal obligations): the Finnish Data Protection Ombudsman's guidance on
[storage limitation](https://tietosuoja.fi/en/storage-limitation),
[legitimate interests](https://tietosuoja.fi/en/controller-s-legitimate-interests)
and [the right to erasure](https://tietosuoja.fi/en/right-to-erasure).

### Joining and first payment

**Confirmed intended flow**

1. A person submits an application, without gaining portal login access.
2. Send an email-verification link. Only after the applicant clicks it does the
   application appear in the administrator approval list.
3. An administrator approves the verified application, establishing membership and
   granting login access without waiting for payment. Record the approving
   administrator and approval timestamp. If rejected, send a rejection email with
   an optional administrator explanation, then permanently delete the application
   and associated applicant data immediately instead.
4. Send a welcome message containing an initial set-password link and an initial
   membership invoice using the configured default membership period. The first
   period starts at approval, not payment.
5. Membership is "active but overdue" during an initial grace period ending on
   the initial membership charge's due boundary, for example two weeks after
   approval/invoicing. A different line's earlier public invoice date does not
   shorten this grace.
   This initial grace can be configured separately from renewal-invoice grace.
6. If the initial membership line is still unpaid at its own due boundary, immediately make
   membership inactive and pause membership billing. Do not add renewal grace
   after initial grace. Any subsequent termination remains a manual action under
   the normal termination/deletion workflow.

Payment during initial grace settles the period that began at approval; it does
not move the billing anchor. For example, approval on 1 June followed by payment
on 10 June pays for 1 June to 1 July, not 10 June to 10 July. Payment after initial
grace has expired and membership has become inactive follows the prorated
reactivation rules instead, keeping the original shared schedule.
An eligible fully discounted membership period is established without payment;
do not demand payment of zero or apply nonpayment inactivity to that period.

The phrase "active but overdue" describes the intended membership standing during
initial grace; it does not require calling an invoice financially overdue before
its due date. The final status terminology remains open.

**Implementation follow-up:** initial-grace notice wording/delivery is grouped under
[technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
Initial grace and renewal grace must not stack, and initial nonpayment must not
introduce automatic membership termination.

The no-login-before-approval rule does not prescribe whether an internal account
record exists before approval.

### Recurring-invoice nonpayment timeline

**Confirmed**

- Deadlines are relative to the individual membership charges, not an association-wide annual
  cutoff. Billing schedules are anchored to when the member joined; month-end
  handling retains the intended day as specified in phase 2, including using
  February's last available day in leap and non-leap years.
- Send the invoice ahead of its due date, for example two weeks beforehand.
- If the membership line is unpaid when due, its overdue grace begins, for example
  one week. Membership remains active throughout this grace period despite the
  invoice being overdue.
- Notify the member when the grace period begins and when it ends.
- At grace expiry, make membership inactive immediately and pause membership
  billing. Membership itself continues until resignation or manual termination.
- The timing is configurable; two weeks and one week are examples, not fixed
  requirements or agreed defaults.

For a membership line that remains unpaid throughout:

The illustrative due date below is this membership charge's own boundary; a
combined invoice can show an earlier date for another unfinished charge without
bringing forward membership's grace or expiry.

| Event | Illustrative timing | Notice and membership effect |
| --- | --- | --- |
| Invoice sent | Due date minus 14 days | Invoice/payment notice. |
| Invoice becomes overdue; grace begins | Due date | Grace-start notice; membership remains active. |
| Grace ends | Due date plus 7 days | Grace-end notice; membership becomes inactive and membership billing pauses. |
| Membership terminated by administrator | Manual decision, not a timer | Mark account for deletion and send final warning; no separate retained terminated state. |

Unfunded requests expire under the nonpayment rules without leaving collectible
arrears. Grace is temporarily granted membership/service availability, not credit
extended to the member or a receivable in accounting.

Funding may be partial per line, but each purchase needs its line fully funded.
On expiry of an unfinished line, release only its unused allocated funds to
billing credit with a traceable transfer, not another bank receipt/refund. Already
fulfilled lines and coverage remain intact. Expiry alone must not reactivate
membership, start fresh grace or recreate the expired purchase request.

**Confirmed settlement during renewal grace**

- A qualifying payment received while membership remains active during renewal
  grace settles the originally invoiced period. Do not prorate away the grace
  days, move the period start to the payment date or reset the billing schedule.
- Worked example: a renewal covers 1 July to 1 August, with grace ending on
  8 July. Full payment on 5 July funds 1 July to 1 August, not a shorter period
  starting on 5 July. These dates are illustrative, not timing defaults.
- Payment actually received after grace has ended instead follows the prorated
  return-period rules below; a delayed import of an on-time receipt is different.

**Confirmed bank receipt date versus processing time**

- For bank transfers, use the bank receipt date to determine coverage and whether
  payment arrived during grace, not the later import/matching time. Processing
  delays must not reduce the member's purchased coverage.
- Worked example: grace ends on 8 July, the bank receives full payment on 7 July,
  and the system imports it on 9 July. Settle the original renewal period as an
  on-time-within-grace payment, even if the system temporarily marked membership
  inactive before learning of the receipt. Do not treat that import as a new
  prorated return beginning on 9 July.
- Access can temporarily lapse before the system learns of the payment; receipt
  dating does not let controllers authorise using an as-yet-unknown payment.
- For receipt-date proration after genuine inactivity or on initial service
  funding, the bank receipt date likewise differs from import time. This does
  not make an unconfirmed deposit spendable at checkout or override the separate
  administrator-intervention requirement for accounts pending deletion.

**Confirmed effective date when funding accumulates**

- Use the last funding event needed to complete each line. A bank receipt uses
  its receipt date, not its import time; applying previously available billing
  credit uses its application effective date, not the original deposit date.
- Do not reprice daily merely to make an insufficient held amount become enough
  without a new qualifying funding/application event. Processing timestamps and
  effective dates remain distinct; out-of-order imports need engineering tests.

### Reactivation after inactivity

**Confirmed**

- If the account still exists and is not marked for deletion, an inactive member
  can resume without settling the old unpaid membership invoice or paying for
  the intervening inactive months.
- Keep the member's shared billing schedule during inactivity. The return period
  starts on the date the reactivating payment is received (the bank receipt date
  for a transfer, not its import date) and is prorated up to the next shared
  billing boundary. Subsequent periods follow the
  unchanged schedule; do not reset the billing anchor or backdate coverage to the
  old unpaid invoice.
- This supersedes the earlier proposal to start a new full period and reset the
  billing anchor on reactivation.
- Reactivation on payment versus administrator approval must be configurable.
  Association membership can reactivate automatically on payment; if renewed
  application/approval were wanted, the account would instead have been deleted.
- Expiry of an unpaid request and release of its funds to billing credit must not
  recreate that expired request or restart its grace. Billing credit may fund
  other valid recurring requests automatically; resumption requires a qualifying
  completed purchase and any applicable administrative approval.
- Membership reactivation does not resubscribe removed services. This association's
  24/7 access is not self-subscribable, so an administrator must add it again after
  removal. If its subscription and paid coverage remain valid, membership return
  makes it usable again under the ordinary access checks, without admin reapproval.

Payment while deletion is pending appears as a failed-to-match receipt for treasurer
review, even with a formerly recognised reference; it must not fund purchases,
automatically reactivate the account or cancel deletion. The person must contact
an administrator. Cancelling deletion may still require manual payment repair;
restoration/repeated imports must not silently rematch these receipts.

Restoration before actual final deletion is administrator-only, as specified below.
Reactivation price uses the bank receipt date, not the quote or import date; a
changed quote needs the agreed traceable reconciliation, not another scheduling
policy. Old unfunded requests expire and must not absorb a returning payment.

### Termination and pending deletion

**Confirmed**

- Membership termination is a manual administrator action. Administrators can use
  inactivity information to decide which memberships to terminate; no automatic
  termination deadline applies.
- Terminating membership also marks the account for deletion. There is no separate
  "terminated" non-member state in which the person remains indefinitely in the
  registry. Pending deletion is a transitional stage of the deletion workflow,
  not a retained former-member account category.
- When an account is marked for deletion, send a final warning stating
  approximately when final deletion will take place.
- If unused credit remains, include it in that notice and tell the person to
  contact the treasurer for a refund. Delete the account on schedule even if the
  credit has not yet been refunded; preserve the separate minimal financial record
  described in the balance section.
- Waiting time is a configuration-file option, default 14 calendar days from
  marking for deletion. A batch job every 14 days deletes eligible accounts on the
  next successful run after their waiting time elapses: normally 14–28 days after
  marking, potentially longer if a job fails or is delayed. Never delete before
  eligibility. Waiting time and batch cadence are separate concepts.
- The warning must reflect eligibility plus the batch schedule, not promise exact
  execution or deletion at the next batch regardless of elapsed waiting time.
- Do not send a message when the account is actually deleted.
- Once marked for deletion, the person must contact an administrator to seek to
  save the account. Payment or another member self-service action must not cancel
  deletion automatically; any restoration requires administrator intervention.
- An administrator may cancel pending deletion on a verified request any time
  before actual final deletion, including after eligibility but before the batch.
  Re-evaluate surviving membership, subscriptions and coverage; do not restore
  expired time or separately revoked access. Payments received while pending remain
  in failed-match review unless explicitly repaired; retain their source facts.
- Marking for deletion immediately disables portal login and access, whether
  initiated by an administrator or by resignation. An existing logged-in session
  must not allow continued portal access while deletion is pending.
- Final deletion actually removes the account/profile and personal service,
  qualification and current credential records; it is not a soft-delete flag or
  an archived restorable member. Do not retain these data to recognise returning
  people or reconstruct their old account. Minimise separately justified financial/
  audit retention under the existing retention rules, rather than keep a shadow profile.
- Rejoining after final deletion requires a fresh application, email verification
  and administrator approval, creating a new identity/member ID. Do not restore old
  qualifications, fob registrations or subscriptions, or silently relink retained
  claims/history by matching an email. Separate financial claims remain treasurer-led.

Lifecycle policy is settled. Batch execution and erasure implementation are technical
design; retention validation is
[pre-deployment work](OPEN_QUESTIONS.md#before-deployment-not-reasons-for-a-universal-billing-engine).

### Voluntary resignation

**Confirmed**

- Members can resign through self-service. Resignation immediately initiates the
  same pending-deletion workflow, including its warning and scheduled final
  deletion; it does not wait until the paid period ends.
- Basic association membership fees are not refunded on resignation, including
  unused prepaid membership periods.
- The system may drop now-unusable services on resignation without automatically
  refunding or crediting unused purchased service time. Preserve enough purchase,
  payment, covered-period and removal audit facts for later manual investigation
  and rare manual handling; no extensive refund-entitlement/valuation engine is
  needed. This system scope is not a legal determination that refund rights vanish.
- Available balances and unused held funds are not unused purchased time: preserve
  their separate financial claim, releasing unfinished holds without forfeiture.
  Stopping an individual service while membership continues instead preserves
  remaining covered time; that rule does not override resignation's access loss.

### Lifecycle established so far

```text
submitted application --emailed link clicked--> verified application in approval list
unverified application --retention expires (default 24 hours)--> deleted automatically
verified application --approval--> member
pending applicant: no portal login access
application --rejection--> rejection email, then applicant data permanently deleted
new member: welcome with set-password link + initial invoice
new member: first period starts at approval
new member: active during initial grace until membership charge's own due boundary
initial grace --unpaid at initial due date--> inactive member (no renewal grace)

member --recurring invoice overdue--> active member in grace period
active member in grace period --unpaid at grace expiry--> inactive member
inactive member: still a member; membership billing paused
inactive member --return-period payment, automatic reactivation enabled--> active member
reactivated member: prorated return to next boundary; existing billing anchor unchanged
member (active or inactive) --manual admin termination--> pending deletion
member --self-service resignation--> pending deletion immediately

pending deletion: portal access disabled; final warning sent
pending deletion: saving account requires contacting an admin
pending deletion --payment--> failed-match review; remains pending deletion
pending deletion --admin cancels before final deletion--> re-evaluate surviving standing
pending deletion --waiting time elapsed + successful batch--> deleted (no message sent)
deleted person applies again --> fresh application, verification and approval; new account
```

This is not a settled implementation state model. Grace-period status describes
active membership. Rejoining after final deletion is a new admission, not a
resurrection path. The account-deletion workflow is initiated by administrator
termination or self-service resignation.
Marking for deletion and final erasure are separate stages of that workflow; there
is no persistent terminated account state between membership and deletion.

### Acceptance example: verification gates the approval list

- Given a submitted application whose email has not been verified,
- it is absent from the administrator approval list and cannot be approved.
- Clicking the emailed verification link makes the application available for
  approval, but still does not grant portal login access.
- If the applicant is under 18, highlight this in the approval interface.
- Age alone must not automatically reject the application or require entering
  parental-consent records into the portal.
- If the application remains unverified beyond the configured retention period
  (24 hours by default), automatically delete it instead.

### Acceptance example: approval before payment

- Given an email-verified pending application whose first membership fee has not
  been paid,
- the applicant cannot log in before approval.
- when an authorised person approves the application,
- the applicant becomes a member and gains login access without waiting for payment.
- Record the approving administrator and approval time.
- Send a welcome message with a set-password link and an initial invoice, and grant
  initial active grace through the membership charge's own due boundary. A combined
  invoice's earlier public date for another charge does not shorten that grace.
- If approved on 1 June and paying on 10 June during initial grace, the first
  monthly paid period covers 1 June to 1 July; payment does not shift it.
- If the membership line remains unpaid at its due date, membership becomes inactive and
  billing pauses immediately, without an additional renewal grace period.
- Any later overdue restrictions or termination follow separately specified
  rules; unpaid status does not undo the fact that approval established membership.

### Acceptance example: renewal overdue but still active during grace

- Given an unpaid recurring membership invoice reaching its due date,
- when the grace period begins,
- the member receives a grace-start notice and remains active during that period.
- Being overdue alone must not prematurely apply the inactive-membership state.
- If the membership line remains unpaid through grace expiry, the member receives the
  grace-end notice, immediately becomes inactive and has membership billing paused.

### Acceptance example: inactive but still a member

- Given a member whose unpaid membership fee reaches the inactivity threshold,
- when the inactivity rules take effect,
- the person remains an association member with restricted benefits or features.
- They retain portal access to view bills and pay, maintain personal details,
  resign and make personal-data/privacy requests.
- Keep membership-payment information and the member-services page conveniently
  accessible; storage/access-control tools and other members-only areas may be restricted.
- Membership termination and personal-data erasure have not occurred merely
  because the member became inactive.
- Membership billing pauses. Remaining inactive for longer does not automatically
  terminate membership; an administrator must make that decision.

### Acceptance example: returning after several inactive months

- Given a member who became inactive in March, whose account still exists and
  is not pending deletion, and whose existing monthly cycle renews on the 10th,
- when a return-period payment is received by the bank on 25 June and subsequently
  matched in the system, with
  automatic membership reactivation enabled,
- membership becomes active without requiring payment of March's unpaid invoice
  or the intervening inactive months.
- The prorated return period covers 25 June to 10 July. Subsequent monthly periods
  remain on the 10th-to-10th schedule; no new 25th-to-25th schedule is created.

### Acceptance example: final deletion warning

- Given an account being marked for deletion through administrator termination
  or self-service resignation,
- the person receives a final warning with the approximate deletion timing.
- Portal login and access are disabled immediately, including continued access
  through an already logged-in session.
- If payment arrives while deletion is pending, it does not cancel deletion or
  automatically reactivate membership. Even an old recognised reference goes to
  failed-match review. The person must contact an administrator; restoration need
  not automatically repair that payment.
- With the default 14-day waiting time and fortnightly batch, deletion normally
  happens 14–28 days after marking, never before waiting-time eligibility.
- When final deletion later takes place, no deletion-completion message is sent.
- Administrator termination and deletion marking are one workflow, not separate
  actions separated by a retained terminated-member state.

### Acceptance example: unpaid extra service

- Given a member whose membership fee is paid but whose door-access fee or another
  service invoice is unpaid,
- that unpaid service does not itself trigger termination of association membership.
- Any effect on the corresponding service follows the confirmed first-payment/
  renewal-grace, subscription-removal and access rules in phase 4.

### Remaining membership work

The consolidated [review outcome](OPEN_QUESTIONS.md#policy-review-outcome)
records the settled lifecycle/adjustment decisions, including fresh admission after
final deletion. Technical work and retention validation have their own categories;
these are not additional copies of settled questions to answer independently.

## 5. Phase 2: Billing and payments

### Ordinary billing, before exceptions

**Summary of confirmed intent, not an additional architecture**

1. The member chooses a billing period for membership and each recurring item.
   Items with the same period share a schedule and a bill/reference.
2. Before renewal, request payment for explicit upcoming periods on a clear bill
   available as a PDF. At most one bill is outstanding in each group.
3. Match received payments to that bill and allocate to unfinished lines,
   membership first, then the configured optional-service priority. Apply billing
   credit automatically and put excess into billing credit. Fulfil each fully
   funded line immediately; direct payments need not pass through a wallet.
4. A fully funded, authorised line establishes its purchased coverage. Use of the
   benefit remains subject to active membership and any required service approval/
   qualifications. Normal payments buy the stated period, not an amount-derived
   multiple of 31 days. The proposed distinction between purchase authorisation
   and live resource eligibility is in [SERVICE_DESIGN.md](SERVICE_DESIGN.md).
5. Repeat at the next boundary. If payment is absent, apply the agreed grace and
   expiry rules; do not accumulate collectible debt or bill inactive membership.

Alignment, changed quotes, credit and manual corrections are supporting rules,
not separate member-facing billing systems. Storage is a one-time checkout,
not another recurring schedule. The complexity review and proposed implementation
restraints are in [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md#billing-simplicity-review);
none silently withdraws a confirmed requirement.

### Confirmed requirements

- Support both recurring payments and one-off invoices.
- All billing requests payment before membership or service is provided. Unpaid
  invoices are expiring payment requests, not collectible receivables. Grace does
  not create debt or a negative member-credit balance.
- Collate a member's recurring charges with the same recurrence period onto the
  same bill, grouped by the selected billing period (for example, one, three or
  twelve months), using the shared schedules and alignment rules below.
- Support bank transfers and at least MobilePay. Other providers must not require
  a fundamental redesign; SumUp is a possible later integration.
- Do not require members to adopt standing orders as their normal way to pay.
- Integrate payment status with membership and applicable paid services.
- Respect administrative paid-period extensions and time-limited discounts as
  specified in phase 1; neither is a fictitious payment or generic status override.
- Pause membership billing and issuance of new recurring-service payment requests
  while membership is inactive. Preserve already-purchased service coverage and
  its original expiry dates; pausing invoicing does not freeze or extend paid time.
- Membership inactivity blocks service use even if its paid term has not ended.
  The subscription itself is not paused, removed or marked for reapproval solely
  because membership became inactive. When membership returns, a still-valid
  subscription/coverage needs no administrator action. A service already expired
  and removed instead requires the same self-subscription/admin addition as before.
- Worked example: membership becomes inactive on 8 July while door access is
  paid through 1 October. Door use stops, the paid-through date stays 1 October,
  and no new recurring-service invoices are issued while membership is inactive.
  Membership return in September permits using the remaining paid door term with
  no reapproval. Return after that service has expired and been removed requires
  normal resubscription. There is no frozen clock or extension for the inactive time.
- A paid service must still expire and be removed under its normal coverage/grace
  rules when no renewal occurs, even if no renewal invoice was issued during
  membership inactivity. Do not create fresh grace or leave an indefinitely dormant
  subscription merely because no unpaid renewal line exists. Apply the configured
  service renewal grace (door default 7 days) only where applicable to an established
  paid renewal, not as extra time caused by membership inactivity.
- Already-issued unfunded service requests retain their ordinary expiry rules;
  membership inactivity does not immediately expire them. Keep partial line
  funding allocated until completion or ordinary expiry; release unused funds to
  billing credit on expiry. If membership resumes before expiry, funding can
  complete subject to the service's normal purchase/subscription eligibility. This
  rare scenario does not require a separate inactivity-triggered cancellation.
- Storage renewals are manual purchases, not part of this recurring-invoice pause.
- Incoming bank transfers are still recorded while new service requests are
  stopped. Without a valid request they do not automatically buy more service
  time; retain funds through the ordinary credit/review rules. This does not alter
  the agreed ordinary expiry/funding treatment of already-issued valid requests.
- **Confirmed service nonpayment follow-through:** remove a service subscription
  when its own fee remains unpaid too long; do not retain an inactive service on
  the member. End its future billing and expire/cancel its unfinished requests,
  returning only their unused funds to billing credit. Preserve completed purchases,
  reference/history and qualifications; do not terminate membership or create arrears.
  Returning is a new subscription using the same self-subscription/admin rule.
  The [service architecture](SERVICE_DESIGN.md) proposes the episode/history records;
  timing is confirmed: an association-wide configurable 14-day first-payment
  window, and per-service renewal grace including zero (door default 7 days).
  Renewal grace does not stack onto an unpaid initial subscription/free period.
- **Confirmed stop renewal:** a member stopping a recurring service stops future
  charges, not use of remaining paid/granted time. Keep existing covered dates and
  completed future periods, subject to ordinary membership/resource gates. Cancel
  only that service's unfinished invoice lines and return their unused funds to
  billing credit; other lines/services stay intact. At the end of retained coverage,
  close the subscription without new unpaid grace. Do not automatically refund or
  revoke completed purchases. Immediate administrator removal is a distinct action;
  storage's separate no-early-release rule is unchanged.
- On membership reactivation, charge for the prorated return period up to the next
  boundary of the existing shared schedule. Do not restart that schedule. Old
  unfunded membership requests expire; they must not absorb a return payment or
  become a prerequisite for reactivation.
- Do not refund basic association membership fees on resignation, even for unused
  prepaid periods. Unused purchased service time needs no automatic refund/credit
  on resignation; preserve evidence for rare manual handling and unspent-money claims.
- Provide useful accounting support beyond reference-to-service mapping.
- Preserve available payment messages and counterparty information, including
  recipients of outgoing transfers, rather than discarding them on import.
- Retain historical reference associations after a service is cancelled.

### Bank transaction ingestion and unmatched-payment review

**Confirmed**

- Retain automated bank transaction ingestion through GoCardless Bank Account
  Data, called Nordigen in the legacy code. The association currently refreshes
  this PSD2/open-banking feed every midnight; preserve that daily automated route.
  Deployment credentials/configuration are not supplied by the repository.
- Payments without a valid matching reference go to an administrator review queue.
  Administrators can identify the member and explicitly assign the payment's
  purpose; do not guess the member or purpose from payer name or amount.
- While an account is pending deletion, even its previously recognised references
  fail automatic matching and appear to the treasurer in that queue. Record the
  source receipt without member allocation or coverage. Cancelling deletion may
  require manual repair; retries/restoration do not automatically rematch it.
- The queue also supports marking an item read/ignored without assigning it to a
  member, invoice or credit balance. Some bank transactions intentionally have
  no portal purchase to match, such as donations. Do not force all bank activity
  into the membership/service payment model or expand this into general bookkeeping.
- Ignoring is a review disposition, not deletion of the bank transaction or a
  fabricated payment allocation. Preserve source details for traceability and
  reconciliation, and keep ignored items out of the pending-review list. Repeated
  feed imports must not duplicate transactions or undo their review disposition.

**Existing-code references**

- `../../mulysa/nordigenautomation/` implements the bank connection and authorisation flow.
- `../../mulysa/users/jobs/daily_fetch_transactions_from_nordigen.py` runs the daily fetch;
  `../../mulysa/users/management/commands/fetch_nordigen_transactions.py` provides a manual
  command invoking the same import path. The midnight timing is reported deployment
  behaviour, not established by the daily job class alone.
- `../../mulysa/utils/dataimport.py`, `DataImport.import_nordigen`, imports booked transactions,
  skips pending entries and checks transaction identifiers against existing records.
  Use this as an integration reference, not an instruction to retain its parsing
  and matching limitations.

**Confirmed operational safeguards**

- Show the last successful bank synchronisation on the administrator dashboard.
- Email administrators when imports fail or become stale, or when bank
  authorisation needs renewal, so payment-dependent membership/access is not
  silently affected by an unnoticed broken feed.

**Implementation follow-up:** bank date mapping, alert configuration and review UI
are grouped under [technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
Correction permissions/workflow are in the [credit review](OPEN_QUESTIONS.md#credit-use-and-corrections).

### Membership billing period and scheduling

**Confirmed intended behaviour**

- The minimum resolution for membership and recurring services such as door
  access is one month, rather than imposing a January-to-December membership year.
  A longer chosen billing period does not change that underlying resolution.
  This is the intended future policy, not a claim about the current rules.
- The monthly membership fee and door-access fee should align so they can appear
  on the same invoice.
- Members choose the billing period separately for membership and each recurring
  service through settings on their user page. They need not choose the same
  period for all services.
- Default available choices are integer numbers of months from 1 through 12.
  Administrators can configure the available choices; whether this uses a GUI or
  configuration file is left to the later configuration design.
- A change takes effect on the next invoice not yet issued. Already-issued
  invoices remain unchanged, even if their covered period has not started yet.
  Do not immediately rebill a member solely because they changed this setting.
- Paying in larger batches is primarily achieved by changing the service's
  recurring billing period, not by a separate one-off prepayment selector.
- Invoice deadlines are individual; do not impose an association-wide annual
  renewal date. Spreading billing avoids the beginning-of-year accounting rush.
- On approval, use the configured default membership period for the initial bill.
  The first period starts on the approval date, even when payment arrives later
  during initial grace. On reactivation after inactivity, coverage resumes from
  the payment-receipt date (bank receipt, not import time, for transfers) and is
  prorated to the existing schedule's next boundary; the billing anchor does not
  change.
- Billing is anniversary-anchored to the member's joining/approval date,
  potentially any day of the month, rather than forced onto calendar-month
  boundaries. Worked example: approval on 12 March gives a monthly period of
  12 March to 12 April, followed by 12 April to 12 May.
- A newly used interval derives its calendar phase from that same original
  anniversary, not the date of the billing-period change. Example: joining
  12 March and monthly coverage through 12 July, then first choosing quarterly
  billing, bridges 12 July–12 September and follows March/June/September/December
  boundaries. Preserve an existing target group's calendar and paid/issued periods.
- An anniversary-anchored calendar month (for example, 12 March to 12 April) is
  distinct from a fixed 31-day interval. A calendar-month definition does not
  require all members' periods to begin on the first of the month.

- Preserve the intended anchor day when a month is too short: use that month's
  last available day, then return to the anchor day when available. Worked
  example in a non-leap year: 31 January to 28 February to 31 March, not a
  permanent drift to the 28th. Do not treat every calendar month as 31 days.

**Deferred:** an administrator tool for moving a member's billing date on request.
This is not part of the initial scope; transition/proration rules for such changes
will be specified only if the feature is added later.

### Invoice collation and period alignment

**Confirmed simplified model**

- Use a shared schedule per member and selected billing period: one-month items
  share a schedule, three-month items share a schedule, and so on. Grouping is by
  the selected period, not whether the item is membership or another service.
- Distinguish the shared invoice schedule, each item's already-paid or granted
  coverage, and each item's member-selected billing period.
- When a service starts mid-cycle, prorate its first period to the next shared
  boundary, then include full periods on the consolidated recurring invoice.
  For example, a service starting 25 June on a 10th-to-10th cycle first covers
  25 June to 10 July. If the group already has an outstanding invoice, add the
  initial prorated charge immediately using the rules below.
- When changing billing period to one already used by another service, bridge to
  that group's next shared boundary before charging full periods together.
- For example, if monthly membership next renews on 10 August and existing
  quarterly door access renews on 10 October, changing membership to quarterly
  billing first produces a two-month bridge, 10 August to 10 October. Thereafter,
  both appear together for 10 October to 10 January and subsequent quarters.
- Administrative extensions add whole months and do not move the shared schedule.
  This applies even to an item billed every twelve months: an administrator may
  extend it by one month. Omit charges for already-covered time and automatically
  prorate the uncovered remainder where the extension crosses a schedule boundary.
- For example, a one-month membership extension from 10 July covers through
  10 August. The invoice for 10 July to 10 August has no payable membership charge
  but can still include the normal door-access charge for that period.
- Resumption after inactivity uses a prorated return period up to the next shared
  boundary, without moving the anchor.
- Business logic should also automatically prorate where necessary to bring
  dates back into alignment if they drift out of sync. The simple UI must not
  make the underlying proration capability artificially unavailable.
- The restriction on arbitrary-day administrative extensions is a UI/workflow
  rule, not a ban on automatic partial-period reconciliation. Members and
  administrators should not have to repair normal alignment issues by hand.
- Monetary discounts provide finer compensation where needed; an exact grant of
  a corresponding number of extra active days is not required.

**Confirmed short-period merging**

- When preparing a new request whose positive initial/alignment period covers
  fewer than seven calendar days,
  combine it with the following full period in the same billing group and issue
  the combined request immediately. This also applies to short bridges caused by
  billing-period changes; it is not an amount threshold or a change of anchor.
- Charge only uncovered days and preserve already-issued/completed facts. Combining
  periods does not combine unrelated services into all-or-nothing fulfilment, grant
  free days, or permit a second open invoice in the group. Skip no-charge days when
  preparing a payable bridge; do not fabricate zero-length charges to trigger merging.
  Fully discounted periods still establish coverage under the zero-price rules.
- Example: an illustrative EUR 30/month service starts 28 March on a 1st-of-month
  schedule, with no free grant. Combine 28 March–1 April (4 days, EUR 3.87) and
  1 April–1 May (EUR 30): request EUR 33.87 immediately. Receipt-date pricing still
  applies to an unfinished initial line; no fixed-price-until-expiry rule is adopted.
- The first-payment window/free-coverage dates remain independent. Show one invoice
  due date using the earliest relevant date among unfinished charges, including an
  earlier initial deadline; merging does not extend the 14-day window or another
  line's grace. C1 in [POLICY_REVIEW.md](POLICY_REVIEW.md) illustrates due 28 March
  rather than 1 April; explanatory invoice text may follow later.

**Confirmed proration calculation**

- Calculate partial-period charges using actual calendar days in the relevant
  full billing period: full-period price multiplied by covered days divided by
  full-period days. Round the resulting charge to the nearest cent; do not assume
  a fixed 30-day or 31-day month.
- Count coverage from its start date up to, but not including, the next boundary,
  so adjacent periods do not charge for the same day twice.
- Worked example: a EUR 10 monthly period from 12 March to 12 April contains
  31 days. Reactivation with bank receipt on 27 March covers the remaining
  16 days through the 12 April boundary, costing EUR 10 x 16/31 = EUR 5.16.

New intervals use the original anniversary phase; existing target calendars remain
unchanged. The agreed coverage/deadline rules preserve paid/granted
coverage despite another charge being overdue. Implement and test reconciliation
against the calendar/proration rules above; do not treat those settled principles
as unanswered policy or erase financial events to update a bill.

### Outstanding invoices and business history

**Confirmed**

- At most one invoice is outstanding per member and billing-period group. Reusing
  the group's payment reference does not require an oldest-invoice-first allocator
  across several simultaneously outstanding invoices in that group.
- For this association, underlying bank transaction data is the bookkeeping
  evidence for membership receipts; individual membership payment-request PDFs do
  not serve as bookkeeping evidence in that workflow.
- Retain records needed for business logic, financial traceability and credit
  reconciliation: charges and covered periods, relevant deadlines/expiry state,
  receipts, allocations and adjustments. Do not lose these facts merely because
  the current payment request changes.
- There is no requirement to retain every superseded payment-request PDF or full
  historical invoice snapshot, or to build a member-facing document-version
  archive. Financial event history and rendered-document history are distinct.
- This does not discard bank source data, credit-ledger history or other necessary
  accounting exports. Their traceability requirements remain in force.

- For a service still awaiting its first payment at a billing boundary,
  update the same outstanding request using the confirmed rollover rules in
  phase 4: price only uncovered days, replace elapsed unfunded periods and preserve
  that line's funding and original payment deadline. Free days crossing the boundary
  remain granted, never charged again. Release any surplus caused by a
  lower revised total to billing credit, rather than lose or double-spend it.

- When a new service is added to a group with an outstanding invoice, immediately
  add its initial prorated charge to that same open invoice. Preserve
  existing lines' allocated funds, completed coverage and deadlines/grace. The new service uses
  its own configured initial-payment window; do not postpone the existing
  membership deadline or restart grace for other charges.
- One invoice may therefore contain charges with different deadlines. Determine
  eligibility and expiry per charge, not from a single aggregate invoice status.
  Fully funded lines fulfil independently. Paid lines stay paid while the bill
  remains open for other charges; service access/payment rules still apply.
- Present one invoice due date, derived from the earliest relevant date among its
  unfinished charges. Recompute it when those charges change or complete; per-line
  due dates need not be presented or separately editable. Keep each charge's
  internal lifecycle timing so an earlier public date does not shorten or reset
  another charge's first-payment window, coverage, grace or expiry.
- When an unfulfilled line expires, remove it from the payable bill and revise
  or recreate the request without that line. The remaining bill may then become
  fully funded and complete automatically; this expiry-driven reduction is allowed
  and does not purchase the expired service.
- In-place expiry/removal is sufficient with independent fulfilment. If recreating
  a request, carry forward only unresolved charges and their funding; do not copy
  paid lines as new purchases. Preserve deadlines/grace and financial provenance.
  Completed purchases stay linked to their original facts, not refunded/rebought.
- Worked example: EUR 10 arrives against EUR 10 membership plus EUR 30 awaiting
  door payment. Fulfil membership immediately. Later expiry of that payment window removes
  only that unfinished line and returns only its held funds, if any. Membership
  stays paid. Do not re-add the same expired request during replacement; returning
  after removal requires normal resubscription, with no automatic repeat free days;
  only an explicit administrator checkbox selection grants the configured days again.
- Worked example: an outstanding monthly invoice requests EUR 10 for membership.
  Adding a door service with a EUR 6 initial prorated charge updates that invoice
  to EUR 16, without changing the membership deadline or losing allocated funds.

### Invoice references, forecasts and payments in flight

**Confirmed**

- Recurring invoice-payment references are stable per user and billing-period
  group, not one reference across all of that user's invoices. For example, the
  user's monthly and quarterly groups have distinct references, each reused for
  subsequent invoices in that group.
- These invoice-payment references are separate from the user's permanent
  balance-deposit reference. Preserve reference history when settings change;
  do not forget old references or reassign them to another person.
- Members can see upcoming invoices generated from their current service and
  billing settings, including changes applicable to the next unissued invoice.
  Present these as forecasts, distinct from issued invoices. Recomputing a forecast
  must not issue a financial document or consume credit.
- A member may send a payment and then change invoicing settings before the
  transfer arrives. This known edge case is acceptable; do not require freezing
  settings or a special workflow to prevent payments in flight.
- Preserve the received payment and original reference. If the changed settings
  cause a mismatch, use normal payment-correction handling rather than lose the
  money or silently classify an invoice payment as an intentional balance deposit.

### Payment due date, first-payment window and free coverage

**Confirmed distinction**

- A charge's own due/renewal boundary governs nonpayment grace or inactivity under
  its applicable membership/service rules. One invoice-visible due date summarises
  the earliest relevant unfinished-charge date; it is not an invoice-wide lifecycle
  trigger for all lines. No public per-line due dates are required.
- An initial payment request has a final payment-window boundary, after which a
  receipt needs normal correction/review rather than silently settling that expired
  request. This is not a separate "purchase offer" workflow.
- Initial free coverage has its own end date, distinct from that payment window.
  Its expiry blocks unfunded use but can leave the subscription awaiting payment
  until the request's deadline. A deadline does not make free days chargeable or
  by itself revoke remaining granted coverage.
- These dates can coincide but must not be conflated. This describes
  business meaning, not a requirement for a particular database-field layout.

**Confirmed:** merge positive initial/alignment periods shorter than a week with
the following full period, including applicable billing-period-change bridges.
If an initial deadline is earlier than the following period's ordinary due date,
use that earlier date for the invoice. Keep internal timing and free coverage intact. See
[short-period merging](OPEN_QUESTIONS.md#confirmed-short-period-merging-and-remaining-calendar-details)
and [the concrete policy examples](POLICY_REVIEW.md).
Receipt-date repricing is retained, not replaced with fixed bills.

### Line funding and independent fulfilment

**Confirmed; supersedes the whole-invoice fulfilment compromise**

- Consolidate charges on one bill/reference, but allocate funding to individual
  unfinished lines. Membership has priority; administrators configure the order
  of other recurring services. Priority applies to new available funding, not
  reshuffling funds already committed or undoing completed purchases.
- A fully funded line fulfils immediately after its applicable receipt-date
  repricing. An incomplete line retains partial funding but buys no proportional
  number of days. Ordinary partial-period alignment pricing remains supported.
- Worked example: a EUR 10 payment toward EUR 10 membership plus EUR 30 door access
  fulfils membership now. Another EUR 5 is held on the door line without activating
  it; EUR 25 more completes door access. The unpaid door remainder cannot make
  fully paid membership overdue or inactive.
- The purpose is to reduce timing/expiry coupling and process useful coverage
  sooner, not to create a separate pick-and-choose checkout workflow. The earlier
  requirement to cancel optional charges before membership can be bought is removed.
- Direct invoice receipts route to their group and unfinished lines in priority
  order, not through a compulsory wallet. Excess becomes billing credit. Multiple
  receipts and automatic billing credit can complete lines without purchase balance.
- Allocated funds are held for that line, not freely available in either balance.
  On cancellation/expiry return only that unfinished line's unused funding to
  billing credit. Repricing preserves funding and releases surplus with a traceable
  transfer; fulfilled lines are not routinely repriced or refunded.
- Cancelling a bill cancels its unfinished purchases only. Preserve completed
  coverage and financial facts. An explicit refund/correction of a completed
  purchase is a separate operation governed by its own policy.
- Show line status, paid amounts, held amounts and remaining amounts, with invoice
  summaries derived from those facts. Close the bill when no unresolved lines
  remain, including a mixture of fulfilled and cancelled/expired lines. A paid
  line's spent funds must not be counted as held money for another line.
- Each receipt need not regenerate/email a PDF. Financial events, coverage origins
  and adjustments remain traceable without a document-version archive.
- Fulfilment records accounting categories but does not bypass membership,
  approval or qualification gates. Deliberate administrative corrections remain
  possible; they do not silently rewrite completed purchases.
- Outstanding means funding needed for an uncompleted advance purchase, never debt.
  Journal-only line holds suffice; no extra reservation model is introduced.

### Member-facing balances

**Confirmed roles; names are working terminology**

Use two available balances rather than the earlier single-balance spending modes:

| Working name | Source and purpose | Spending |
| --- | --- | --- |
| Billing credit ("excess balance") | Excess/corrections/returned recurring funds, plus explicit transfers from purchase balance. | Automatically apply to valid recurring bills, seeking to reduce it to zero. |
| Purchase balance ("cash balance") | Deliberate prepaid deposits and explicit transfers from billing credit. | Explicit purchase checkout or transfer; never automatically swept into recurring bills. |

- Enable purchase balance through an association-level configuration-file option,
  chosen for deployment, not a runtime administrator-UI toggle. Billing credit and
  its automatic recurring-bill use remain available when it is disabled, giving
  the correction-only behaviour previously discussed. No separate per-member
  automatic-spending toggle is required.
- Both available balances are nonnegative. Do not offer overdrafts or express
  unpaid invoices as a negative balance. Funds allocated to an outstanding invoice
  are shown separately; they are not an additional freely spendable wallet.
- Apply billing credit to outstanding recurring invoices, including partial funding,
  up to their remaining amount. Fulfil fully funded lines independently. Prioritise
  the earliest applicable unfinished-line due date, then invoice issue order/ID, across
  valid open groups. Directly referenced receipts target their own group; do not
  reshuffle already-committed funds to implement this ordering. Within a chosen
  bill, fund membership first, then the configured optional-service order.
- Matching invoice receipts and billing credit may together complete lines
  automatically. Purchase deposits and accumulated purchase balance do not trigger
  recurring-bill funding by themselves; only an explicit transfer into billing credit
  makes that money subject to the recurring allocator.
- Do not build an automatic downgrade/disablement workflow for organisations
  already holding purchase balances. Such a rare configuration change requires
  a separately planned operational migration, outside initial scope. It cannot
  forfeit existing funds/history or silently turn them into spendable billing credit.
- **Confirmed member transfers:** when purchase balance is enabled, members may
  explicitly transfer any available amount between their own billing credit and
  purchase balance in either direction, without administrator approval. Source
  availability/nonnegative balances still apply: held or spent funds are not an
  available balance and are not released/refunded by a transfer.
- Moving purchase balance into billing credit makes it subject to ordinary automatic
  recurring-line funding immediately. Warn/show that result; it may no longer be
  available to transfer back after reservation/spending. This supplies explicit use
  for recurring bills without an automatic purchase-balance sweep. A separate direct
  purchase-balance payment button is not implied by the transfer decision.
- Transfers are atomic, audited internal movements, not bank receipts or income.
  Applying transferred billing credit uses its application effective date, not the
  original purchase deposit's bank date. Purchase balance disabled means no such
  transfers or one-time purchasing through billing credit; disabling after use still
  needs its separately planned operational handling.
- Storage is supported through purchase balance; retain the possibility of later
  materials/snack sales without bringing those sales interfaces into initial scope.
- Eligible activities remain non-VAT-liable as specified by the association; no
  hypothetical tax engine is introduced by having two balances.
- Self-service bank withdrawal remains out of scope. Refund/withdrawal requests go
  to the treasurer; verification and authorisation details remain to be specified.
- A bounded ledger records the balances, allocations, spending and corrections for
  accounting/export. Two purposes do not require two unrelated financial systems.

**Funded checkout and delayed payment requests**

**Confirmed**

- Distinguish purchases requiring confirmed funds before fulfilment (storage and
  possible future materials sales) from advance payment requests that allow time
  to pay (membership and recurring services). This is a fulfilment policy, not a
  requirement to route every payment through stored credit.
- For initial SEPA-backed storage purchases, require sufficient available purchase
  balance at checkout. Members deliberately deposit funds first; those funds become
  spendable only after the incoming transfer is confirmed/matched by the system.
  A promised transfer or unprocessed payment is not available credit.
- These SEPA transfers use the user's permanent credit-deposit reference, not a
  purchase-specific reference. Do not generate a direct SEPA payment reference for
  storage checkout. Deposits fund reusable credit independently of any purchase
  or queue offer; there is no storage-payment deadline for a deposit to miss.
- Checkout rechecks membership eligibility, the offer/position or renewal, allowed
  duration and occupancy limit, and available funds. Debit the credit and allocate
  or extend the rental together, without double spending or duplicate allocation.
  An unsuccessful purchase must not consume credit.
- A deposit alone does not buy storage, accept an offer or reserve a position.
  The member separately initiates checkout using available credit; depositing
  funds must never trigger a purchase automatically.
- An active member, including during grace, may explicitly spend purchase balance
  on storage even when membership or other service invoices remain unfunded.
  Funding those invoices first is not a checkout prerequisite; do not redirect
  credit from that explicitly selected one-time purchase into recurring bills.
- Initial storage checkout requires purchase balance to be enabled. Without an
  alternate instantly confirmed payment method, associations with purchase balance
  disabled cannot use this feature, even if billing credit exists. This restriction
  is intentional; do not add a delayed-payment workaround. Existing credit remains
  accounted for and usable/resolvable through other agreed credit-handling routes.
- A later immediately confirmed payment method could pay directly for a purchase
  without artificial deposit/redemption entries. MobilePay is an example of a
  desired future method, not a confirmed initial integration.

**Deposit references and credit-use details**

- Deliberate purchase-balance deposits use a permanent reference unique to each user,
  separate from invoice-payment references. Do not infer deposit intent from an
  invoice reference, transfer amount or the presence of open invoices.
- Applied billing credit is a payment source, separate from pricing discounts.
  Show charges, billing credit applied, direct payments allocated and the remaining
  amount requested. Do not account for correction credit as a discount.
- Specify triggers and ordering for automatic billing-credit application. An
  automatic migration when purchase balance is disabled after use is not initial
  scope. These balance roles supersede the earlier single-balance
  prepaid/correction-only modes and
  the temporary proposal to make every insufficient invoice receipt freely spendable.

**Ledger and member display**

- Show both available balances and an understandable transaction history. Present
  outstanding invoice funding separately, not as a negative wallet balance.
- Available amounts exclude funds allocated to invoices or spent on purchases.
  Allocation and checkout must be atomic with the corresponding balance debit,
  so concurrent operations cannot use the same money twice. Completing a line
  consumes its allocated funds once; returning its unused funding restores billing
  credit once without reversing already-completed purchases.
- Record deposits, purchases/redemptions, refunds and corrections as traceable
  events. Spending cannot exceed available funds, including concurrent spending.
- Use auditable adjustment/reversal entries rather than unexplained edits to a
  balance or silent rewriting of past transactions.
- Export enough detail to link deposits to bank/payment-provider receipts and
  spending to the purchased item, accounting category and relevant invoice/period.
- Distinguish direct/partial invoice receipts, purchase deposits, billing credit
  and their allocations in records/exports. Both payment routes require matching,
  allocation, audit and reconciliation; they are not separate sets of financial
  truth.

**Confirmed accounting model for both balances**

- All retained member credit in both balances is accounted for as a liability/
  member advances, not immediate membership or sales income. Allocated funds on
  unfinished lines remain advances, not income from the incomplete purchase. A bill
  may therefore contain income from a paid membership line alongside liability
  held toward its unfinished door line; keep them distinct.
  Spending on completed purchases reduces that liability and is classified
  according to the membership, service or goods bought.
- Balance roles differ in funding source and automatic versus explicit use, not
  in the accounting meaning of retained member credit. Purchase-balance disablement
  does not change the meaning of billing credit or already-retained funds.
- The flow is liability to the appropriate income category on completed purchases.
  Export the transaction date, category and covered service period so the accountant
  can apply the association's periodisation policy where necessary.
- Unfunded payment requests and grace are not posted as receivables or negative
  wallet transactions. Issuing an unfunded request alone does not spend credit;
  allocating billing credit is a distinct transfer from available credit to line
  funding, not purchase fulfilment or another bank receipt. Recognise the purchase
  categories when each line completes; do not recognise them again for its PDF.
- Keep receipts, purchases, allocations and adjustments distinct. Spending stored
  credit is not a second bank receipt and must not recognise the same income twice.
- Reconcile opening credit, deposits, allocations/returns, spending, refunds and
  adjustments to closing amounts. Reconcile both available balances plus funds
  committed to unfinished lines to the corresponding liability without double
  counting transfers. Include funds preserved for deleted accounts.
- Keep individual member balances in the portal's subledger. The general ledger can
  use one aggregate member-credit liability account rather than one account per
  member or balance purpose, with portal detail supporting reconciliation. Exact
  posting categories remain for accountant review.
- This is a bounded member-credit subledger feeding bookkeeping, not a replacement
  general ledger or a general-purpose payment platform.

**Proposed export contract**

- A detailed, program-neutral CSV with stable transaction identifiers, source
  references, event types, dates, amounts and accounting categories, including
  corrections/reversals and covered service periods where applicable.
- A period reconciliation showing opening credit, additions, uses/refunds/
  adjustments and closing credit, with totals traceable to the detailed ledger.
- An accountant-approved posting summary by account/category for the period.
  Allow aggregated journal postings backed by the detailed export, so keeping the
  liability account does not require manually posting every member transaction.
- Distinguish categorisation/splits of bank receipts already imported by the
  accountant from non-cash credit redemptions and adjustments. Imports must not
  duplicate cash movements or income already recorded elsewhere.
- Beancount is a proposed first concrete integration target because it is the
  current accountant's actual workflow. A generic CSV is not a promise of direct
  import into every accounting program; program-specific adapters can follow a
  real need. Summary postings with the detailed ledger attached provide a fallback.

**Remaining:** [credit use and corrections](OPEN_QUESTIONS.md#credit-use-and-corrections)
consolidates spending/refund/reversal policy and accountant review. Provider
metadata, fees, export schemas and reconciliation tests belong to
[technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
Basic membership's no-refund rule does not automatically apply to unspent credit.

**Unused credit after account deletion**

- Delete the account on schedule; an unresolved credit refund must not keep a live
  login account or full former-member profile in the registry.
- The deletion notice informs the person of remaining credit and directs them to
  contact the treasurer for a refund. Do not add a deletion-completion email.
- Preserve the credit in a minimal separate financial record. The intended record
  contains the amount, name, necessary contact information and references needed
  to trace the balance and process a refund, not the full member profile.
- Account for both balance purposes and any unused allocated funds on unfulfilled
  invoices when preserving the person's remaining claim. Deletion must not make
  those funds disappear or count the same funds as both available and allocated.
- Limit access to the financial record to those handling the liability/refund and
  document its purpose, lawful basis and retention in the privacy information.
- Keep the amount in the credit-liability reconciliation until it is refunded or
  the obligation has validly ended. Account deletion alone does not forfeit it.
- Once the liability has validly expired, the intended accounting treatment is a
  traceable release to an appropriate income category, not silent deletion of the
  ledger history. Necessary bookkeeping records may have separate retention rules.

**Expiry deferred; no automatic expiry initially**

- Automatic expiry is explicitly deferred. Do not implement a timed forfeiture or
  automatic liability-to-income release in the initial system.
- Gift-card-style terms may be relevant to a later policy, but the validity of a
  particular shorter period is not established merely by that analogy. Settle
  the terms, legal basis and retention rules separately before implementing expiry.
- Three years is a candidate discussed by the domain expert, not an agreed fixed
  duration or a verified rule running from account deletion. Determine the legal
  nature of the refund claim, when time starts and what interrupts or extends it.
- A shorter personal-data retention preference does not itself extinguish the
  financial obligation. Any shorter contractual expiry needs a valid basis and
  appropriate terms; do not apply an arbitrary shorter cutoff retroactively.
- Define when identifying information can be erased/anonymised independently of
  required financial evidence, including how active refund claims are protected.
- Any initial review/closure process is treasurer-handled; a review date does not
  itself extinguish the credit. Revisit whether to automate closure only after the
  policy is settled; no general-purpose legal limitation-period engine is required.

References for legal validation, not a determination that consumer/gift-card rules
apply identically to this association's member credit:
[KKV: Velan vanhentuminen](https://www.kkv.fi/kuluttaja-asiat/perinta-ja-maksujarjestelyt/velan-vanhentuminen/)
and [Kuluttajariitalautakunta: Lahjakortin voimassaolo, 12 July 2024](https://www.kuluttajariita.fi/paatokset/lahjakortin-voimassaolo/).

### Invoice delivery and one-off purchases

**Confirmed**

- Issue PDFs by email and make them available in the portal. Members can disable
  emailed invoices without losing access to the documents.
- The original proposal for one-off storage payment requests is superseded by the
  confirmed funded-checkout flow above; storage does not generate a direct SEPA
  payment request.

**Agreed technical preference:** generate invoice PDFs with **Typst**, rather than
an HTML-to-PDF renderer. In the .NET implementation, use a controlled Typst CLI
child process rather than retain the Python binding. The association already has a
parameterized Typst invoice template; reuse it once supplied and validate its
compatibility during billing implementation. Keep template source trusted and pass
invoice values as structured data, not executable markup. Rendering remains a
presentation step over authoritative billing facts, not another pricing engine.

### Remaining billing work

The [consolidated review](OPEN_QUESTIONS.md) separates the few grouped policy
topics from technical design and deployment checks. Combined billing, allocation
into accounting categories, independent line fulfilment, proration, rollover and
no-debt behaviour are not wholesale open questions. E-invoicing remains a possible
later feature.

## 6. Phase 3: Communications

**Confirmed requirements**

- Member email preferences and unsubscribe support for optional communications.
- Newsletters are opt-out: new members receive them by default and can unsubscribe
  explicitly. Newsletter preferences are separate from operational-message rules.
- The default newsletter audience is all current members, including inactive
  members, excluding unsubscribed people and accounts pending deletion.
- Members can disable routine invoice emails independently of their newsletter
  preference. Invoices remain available in the portal.
- Disabling invoice emails or newsletters does not suppress grace-period warnings,
  membership notices, password/security messages or deletion notices.
- Administrators can send official member notices, such as general-meeting
  invitations, separately from newsletters. These reach current members regardless
  of newsletter preferences.
- Practical tools for mass email and a monthly newsletter are included in the
  initial product for full administrators.
- Richer messages than plaintext alone.
- Templates editable through the system rather than deployment or repository
  file changes.
- Invoice emails and relevant membership/payment notifications.
- A welcome message including a set-password link, and an initial membership
  invoice on approval.
- Ordinary password-reset emails.
- A rejection email with an optional administrator explanation, sent before
  immediate permanent deletion of the applicant data.
- An application email-verification link before the application enters the
  administrator approval list.
- Renewal notices at invoice issuance and the beginning/end of an unpaid
  membership line's grace, as described in phase 1. Initial-grace
  notice wording/delivery remains implementation follow-up, not another grace policy.
- A final warning when an account is marked for deletion, including approximate
  deletion timing. No message is sent upon actual deletion.

### Rich-text authoring and safety

**Proposed UX:** provide a rich-text/WYSIWYG email editor rather than requiring
administrators to author HTML. The editor, stored content format and corresponding
sanitisation library are deferred implementation choices; no particular editor or
sanitisation dependency is selected yet. HTML email content and browser previews still need
server-side sanitisation, and editable templates use restricted placeholders rather
than arbitrary template execution. Client-side editor restrictions alone are not a
security boundary. This does not defer essential operational mail from S1 to S3.

### Message localization

**Confirmed**

- Send a localized version using the recipient's preferred language, rather than
  one bilingual message to everyone. Support all languages available in the system;
  do not hard-code a Finnish/English-only model.
- Swedish is a possible additional language, including for associations such as
  Vaasa's hacklab. Its inclusion in the initial translation set is not yet decided.
- Support controls that explicitly allow sending a message with missing language
  variants. It must be clear what affected recipients actually receive; missing
  translations must not cause an undocumented fallback or silently omit recipients.
- Apply localization to the message content, including subject and body.

**Confirmed missing-translation workflow**

- For manually sent mail, check the selected recipients' languages and show which
  variants are missing. By default, do not send to an affected language group until
  the sender explicitly chooses its fallback and confirms the outgoing content.
- Show each affected group's language, recipient count, chosen outgoing language
  and preview of what it will receive. Missing a variant with no selected
  recipients need not block the send.
- Automated notices cannot wait for a sender's decision on each run. Require an
  explicitly configured fallback policy before activating their templates or a
  newly supported language, and make that policy visible to administrators.
- Necessary operational notices must not be silently dropped because their
  preferred-language variant is missing.

### Confirmed authoring and sending workflow

- Save message drafts with localized subjects and bodies.
- Preview the rendered content and intended recipient groups, including any
  explicitly selected language fallbacks.
- Send a test message to a chosen address before sending to the audience.
- Require explicit confirmation before a bulk send; no second administrator's
  approval is required for the initial product.
- Queue sending and show per-recipient/overall progress and failures. Retrying
  failed recipients must not deliberately resend to recipients already completed.
- Respect newsletter opt-outs and pending-deletion exclusions at dispatch, not
  only when the draft's initial recipient list was calculated.
- The initial product supports "send now", which submits the message to the queue.
  Administrator-selected future send times are not needed initially. This does
  not remove automatic invoice/reminder timing or rate-limit delays in the queue.

### Background queue and delivery providers

**Confirmed**

- Email sending must use a background queue, including support for bulk mail and
  automated operational notices. Pending work must survive application restarts.
- Use Quartz.NET 4 with its PostgreSQL persistent job store, a separate worker
  process and a small configurable execution pool. Scheduling business-related work
  must enlist the actual EF Core connection/transaction, not merely use the same
  database or enqueue after commit. Prove rollback/commit visibility, restart,
  worker-death and shutdown recovery, bounded retries, cancellation/overlap and
  duplicate-safe effects in S0; no custom queue/lease framework is prescribed.
  Only registered job types with minimal identifiers/string job data are allowed;
  requests and editable templates cannot supply arbitrary job types or executable
  payloads. See [durable work](ARCHITECTURE.md#7-durable-work-email-and-scheduled-maintenance).
- The initial association will use Scaleway's TEM API. Do not make the queue or
  communications business rules specific to that transport; other associations
  must be able to use other delivery backends.
- Support provider-specific rate limits and throttling. Delay queued work rather
  than discard messages or repeatedly exceed a provider's limits.
- Keep queue progress, attempts and failures visible to administrators, and retain
  the agreed ability to retry failed recipients without deliberately resending
  completed ones.

**Proposed:** prioritise time-sensitive operational messages, such as verification
and password-reset emails, over bulk newsletters while respecting provider limits.
Exact retry/backoff settings, supported additional backends and provider-status
handling remain implementation-planning decisions.

**Deferred**

- A delegated newsletter-only permission for non-administrator volunteers.
  Transactional templates and other operational email administration remain
  restricted to full administrators.
- Administrator-selected scheduled sending of manually authored messages.

**Implementation follow-up:** message/template safeguards, initial translations,
queue priorities and retries are grouped under [technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
The required message categories above are established; further categories can be
added when a concrete need appears. Routine opt-outs must preserve required notices.

## 7. Phase 4: Door and machine access

**Confirmed requirements**

- A single access-control component for doors and machines, with distinct
  user-facing treatment for the two use cases.
- Support more than one door; front-door access is not a hard-coded special case.
- Include an administrator-controlled enabled/unavailable flag for each door and
  machine in the initial product. An unavailable resource denies new live
  authorisations regardless of the member's ordinary eligibility, until re-enabled.
- Reflect availability changes in live checks and newly obtained cache data.
  Offline clients holding older grants remain subject to the agreed caching
  limitations; this flag is not a physical safety lockout or a guarantee that
  already-running equipment is stopped.
- NFC fobs registered to people and an API for access-control hardware.
- Machine access based on the required training/qualifications and, where
  applicable, a specific paid service.
- Qualifications are independent of individual machines. A qualification can be
  associated with one or several machines; completing training grants the member
  the qualification rather than a separate copy of training for each machine.
- Door and machine access require active association membership, including during
  grace periods. This is part of the shared requirement that all member services
  require active membership, not a door-only special case.
- Each member service has an administrator-configurable self-activation checkbox.
  If self-activation is disabled, an administrator manually adds the service to the
  member; no service application/request-and-approval system is needed initially.
  The same checkbox governs resubscription after removal. In this association,
  24/7 door access is added by administrators on both first subscription and return.
- Administrator tools for credentials, permissions, training and trainer roles.
- When a service's own nonpayment threshold is reached, remove that service from
  the member instead of making it inactive. Returning means subscribing again;
  there is no independent service-reactivation setting. A payment alone does not
  re-add a removed service. Preserve financial facts, not an inactive subscription.

**Architecture proposal:** [SERVICE_DESIGN.md](SERVICE_DESIGN.md) separates service
enrolment/authorisation from billing instructions, dated coverage and final access
checks. Membership-only inactivity is now settled: keep the
subscription/coverage clock, stop new invoices and block use, without reapproval
if it is still valid on return. This supersedes the earlier broad door-reapproval
wording. A service actually removed for nonpayment instead needs ordinary
resubscription. Voluntary stop renewal is also settled: preserve the remaining
covered time rather than remove access immediately.

### NFC credentials and member registration

**Confirmed**

- Preserve the simple scan-and-claim workflow: unrecognised fob IDs observed by
  readers appear in a list that authenticated members can use to register their
  own fobs. Members can manage/revoke their own fobs; administrators can manage
  credentials and associations as well.
- Revocation removes the current registration and frees its UID for ordinary
  reuse/scan-and-claim, without administrator reassignment. Do not retain a revoked
  ownership row/tombstone that blocks claiming. Historical events/audit facts remain
  separate from current registration and must not be reassigned when a UID is reused.
- Keep a minimal ownership history for each fob UID so historical/cached requests
  can be traced to the relevant past assignment despite revocation or reuse. Ending
  an assignment preserves this separate audit history, not a current registration
  or access permission. History must not prevent ordinary claiming of the released UID.
- Use the fob's ordinary identifier; a challenge-response fob protocol is not an
  initial requirement.
- A registered credential denied access for some other reason is not an
  unrecognised/unclaimed credential. It must not become claimable by another
  member merely because its owner currently lacks access.
- The claiming list shows only unidentified IDs observed within approximately the
  past hour, not scans from previous days. This is a rolling recency window for
  claiming; retention of any separate access/audit log is a different decision.
- Uploading an older offline observation must not make it appear newly scanned
  in the claiming list. Use the observation time, not just upload time, for recency.

Revocation/reuse policy is settled; claiming-list presentation, historical
attribution and timestamp validation are technical follow-up.

**Proposed ownership-history and cache-attribution design**

- Use ordinary EF Core ownership-history entities with a stable assignment ID, UID,
  minimal historical member identifier and assignment start/end/revocation times.
  Create/end these transactionally with current registration changes. Current UID
  uniqueness must not forbid several historical assignments of that UID.
- Include an opaque assignment/grant version in cache entries and offline reports,
  along with cache-generation and event times. Resolve that version against history
  on the server rather than send member profiles to readers. Exact ORM fields and
  protocol representation remain engineering proposals.
- UID plus event time alone is insufficient when an old cached permission is used
  after revocation/reclaiming. For example, Alice's assignment is cached at 10:00,
  revoked at 10:05 and Bob claims the UID at 10:06. An offline event at 10:07 using
  Alice's cached grant must retain Alice's historical authorisation context, not be
  attributed to Bob merely because he owns the UID at event/upload time. This does
  not prove Alice physically presented the fob or turn the old grant into a live allow.
- Retain the reported cached decision and its context; history is for attribution,
  not retroactive authorisation or overriding a live denial. If context cannot be
  resolved reliably, mark attribution unresolved rather than guess today's owner.
- Do not copy names/contact details or retain a deleted account/profile for this
  purpose. Ownership-history link retention/erasure is part of the access-audit
  privacy review below; no indefinite identifiable owner-history period is inferred.

### Live checks and optional client caching

**Confirmed**

- Provide live API authorisation checks so basic clients can rely exclusively on
  the server without implementing local permission caching.
- Also provide permission-cache support. Whether a controller implements and uses
  caching is a client decision, not a mandatory part of operating a reader.
- All checks are **live-first**, including on cache-capable clients. Use cached
  permissions only as fallback when the remote server is unavailable, unreachable
  or times out, not as the normal first lookup.
- An explicit live denial must never be overridden by an older cached grant.
  Invalidate that cached grant when the server denies access; inability to verify
  is different from a negative authorisation response.
- Cache data should be minimal, principally credential identifiers and validity
  timestamps, not member profiles, contact details or financial/training records.
- Clients must be able to obtain the resolved permissions they need rather than
  reimplement membership, billing and qualification rules themselves.
- High availability is a design goal. Publish a cache TTL with a default of
  24 hours, configurable by administrators per door/machine. Clients can refresh
  sooner or choose live-only operation.
- A missing or expired cache entry cannot bypass live verification. In an outage,
  it supplies no valid cached authorisation; client-side outage handling must not
  be represented as a successful server check.
- Door revocations must take effect as immediately as possible on connected
  readers: the next live check must reflect a committed revocation. Do not reuse
  a stale server-side allowlist for that check without invalidating it.
- Delayed machine revocation propagation is acceptable for this association's rare
  use cases. Live-first checks can still make it immediate when connected.
- TTL/validity information is a server-provided contract, not something the server
  can enforce on a disconnected controller. Actual cache implementation and respect
  for its TTL are ultimately the client's responsibility.

**Proposed basic security contract**

- Use TLS and revocable per-device credentials, and scope a device's permission
  export to its authorised doors/machines rather than expose a public allowlist.
- Include generation/validity timestamps and known permission end times. A cache
  freshness TTL is distinct from a permission's known end time; it does not itself
  grant access beyond an earlier eligibility expiry.
- Revocation is not instantaneous for disconnected clients holding cached data.
  Make the staleness trade-off explicit: a client following the 24-hour TTL may
  continue using a cached grant until refresh or expiry, unless an earlier known
  permission end time applies. Do not claim immediate offline revocation.
- Hashing identifiers may reduce raw-ID leakage from cache exports, but ordinary
  UID-based fobs remain low-assurance identifiers. Hashing is not authentication
  of the physical fob and cannot prevent cloning if the UID is obtained elsewhere.

**Technical/security review:** authentication/scoping, identifier representation
and API formats are grouped under [technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
Outage handling without a usable cache is a client decision; the server must not
pretend it has verified an unavailable check.

### Access events and offline reporting

**Confirmed**

- Retain granted and denied authorisation checks, not claims that a door physically
  opened or a machine was actually used.
- Provide an endpoint for cache-using clients to report local decisions made while
  the server could not receive them. Clients buffer unreported events and upload
  them as soon as the server becomes available again.
- Retained history belongs on the server. A client's event buffer is a temporary
  outbox, not an indefinite local access-history database; remove events once the
  server acknowledges them. Uploads must be safe to retry without duplicating events.
- Preserve the original event time, relevant device/resource, reported decision
  and whether it was made offline, using member IDs rather than copied names or
  contact details in log entries. Unknown credentials may lack a member association.
- Events primarily identify the user/member associated with the original decision;
  fob UID is supporting context, not a live join that can change the subject. UID
  revocation/reuse must never reattribute old events to a later owner. Delayed offline
  uploads need original assignment/cache-decision context and ownership history,
  not a lookup of today's UID owner. Exact protocol representation remains
  engineering work.
- After account deletion, render the subject as a deleted member rather than retain
  or reconstruct a full member profile for the log.

**Requested retention default; privacy justification still open**

- The requested server-side default is indefinite retention, not a 30-day expiry.
  This records the product preference, not a determination of GDPR compliance.
- Member-ID-only records are pseudonymised, not necessarily anonymised. While a
  member exists the connection is direct; after deletion, financial records or
  other reasonably available information may still identify the subject.
- Document a purpose, lawful basis and necessity assessment for the retention
  period, access controls and periodic review. Indefinite storage is not justified
  solely by omitting names or displaying "deleted member".
- Apply minimisation and justified retention/erasure to fob-ownership history too.
  It must support delayed cached-event attribution within the supported reporting/
  retention period without keeping a restorable deleted member or relinking a new
  account. A historical member identifier can still be personal data; the requested
  access-event retention default does not automatically justify keeping identifiable
  ownership history forever.
- Keep this policy question visible for resolution before deployment; do not
  silently classify the logs as non-personal data after account deletion.

**Remaining:** event fields, timestamps and upload protocol are technical design;
visibility/retention validation is [pre-deployment work](OPEN_QUESTIONS.md#before-deployment-not-reasons-for-a-universal-billing-engine).

### Qualifications and trainers

**Confirmed**

- Manage qualification definitions separately from machine records, and associate
  machines with their required qualifications.
- Holding a qualification satisfies the relevant training prerequisite, not all
  access conditions: active membership and any required service remain necessary.
- Non-administrator trainers are required. Administrators authorise trainers per
  qualification; trainers can grant and revoke only their assigned qualifications
  without receiving general administrator privileges.
- Administrators retain full qualification management and trainer-assignment
  authority. Trainer authority alone does not permit assigning other trainers.
- Qualifications remain valid until revoked. Automatic expiry and periodic
  recertification are not required initially.
- Trainers can look up members using name, nickname and member ID, and see relevant
  qualification status. Do not expose contact details, birth dates or financial
  information through this trainer lookup. This is an authorised operational view,
  not an ordinary-member directory.
- Membership inactivity does not erase qualifications. The membership eligibility
  gate blocks service use; on reactivation the retained qualifications can satisfy
  training requirements again, subject to all other access conditions.
- Final account deletion removes the person's qualification records. Qualification
  definitions and their associations with machines are not deleted with a member.

**Remaining:** audit fields are technical design; retention/visibility validation
is [pre-deployment work](OPEN_QUESTIONS.md#before-deployment-not-reasons-for-a-universal-billing-engine).

### Initial free service coverage and invoicing

**Confirmed; replaces optional services' initial access grace**

- Configure initial free days per service; this association's door service grants
  14 days to first-time subscribers. Represent them as dated granted `Coverage`,
  not a special service state, a payment or an invoice that is later forgiven.
  An explicit source such as `initial_free_period` identifies the grant.
- Automatically grant only once per member and stable service identity, not per subscription
  episode. Cancellation, nonpayment removal, resubscription, membership inactivity
  and billing-period changes do not reset eligibility. Retain enough prior-service/
  grant history to enforce this across episodes, including under command retries.
- The intent is an introduction to an ongoing service, not a recurring free trial
  for short-period returns (the association's "HaaS" pattern). This does not impose
  a new minimum term, cooldown or ban on resubscribing.
- **Administrator exception:** when adding the service to a repeat subscriber,
  provide an admin-only checkbox to grant its configured free days again (14 for
  this door service). Default it off for repeat subscribers. Record the selected
  option, administrator, subscription/action, configured duration and actual grant
  dates. This creates explicit administrator-granted coverage, not renewed automatic
  eligibility. Ordinary members cannot request the exception. Retrying the same
  addition cannot duplicate its grant; a later deliberate admin exception is a
  separate action. No additional approval workflow is needed.
- Free days are genuinely free even if the member pays immediately: never bill
  them later, backdate a paid charge over them or turn nonpayment into debt. No
  receipt, money movement or fake zero-value payment is created for the grant.
- Access still requires active membership, a current subscription and any resource/
  qualification requirements. Membership inactivity does not freeze the free clock.
- This replaces only optional-service initial access grace. Membership's initial
  and renewal grace are unchanged. Service renewal grace is configurable per service,
  including zero, with a 7-day door default. It applies only to established paid
  renewals, not to unpaid introductory days or the first-payment window. Preserve
  paid/granted coverage independently of invoice issuance.

**Confirmed initial invoicing and user-facing status**

- Issue/update the group's initial payment request when adding the service, for
  its uncovered paid period. An initial grant may make that period start in the
  future. Add to an existing open invoice rather than create a second one; retain
  other lines' deadlines/funding. Subsequent periods follow the shared schedule.
- Before the initial line is fully funded, derive the user-facing description as
  **awaiting payment — active** when free coverage is current, or **awaiting payment
  — inactive** without current coverage while the payment window is still valid.
  These are display descriptions, not a persistent service-inactive lifecycle.
  Other eligibility gates can still deny use; partial funding creates no paid days.
- A fully discounted eligible period is covered without a payment and is not
  awaiting payment. Reuse ordinary discount/coverage processing; no fictitious
  receipt, zero-value movement or separate exemption feature is needed.
- Full funding creates ordinary paid coverage and normal active service status,
  with the paid period visible even if it begins after the free grant. It does not
  cancel unused free days or require another administrative approval.
- Initial free coverage expiry and the first-payment deadline are separate. With
  no grant, a subscription can await payment without access from the outset.
  When a grant ends before the deadline, deny unfunded use but retain the valid
  awaiting-payment subscription. The deadline does not itself grant access.

**Confirmed initial payment pricing**

- Paid coverage starts on the later of the first uncovered day and the line's
  funding effective date, up to the request's aligned end: normally the next shared
  boundary, or the following boundary when a short bridge was combined at issuance.
  Proposed execution interpretation: later receipt-date proration shrinking an
  existing request below a week does not itself append a new full period. Normal
  rollover of elapsed unfunded periods remains. A grant spanning a boundary is not
  shortened; skip covered days without shifting the schedule or creating zero-length
  charges. Merging does not make the free grant chargeable.
- The funding date remains the agreed last required funding event: bank receipt
  date, not import time, for new transfers; application effective date for existing
  billing credit. It can precede the paid coverage start when payment arrives
  during free coverage. Without granted coverage the existing receipt-date initial
  proration is unchanged. No amount-based duration or daily repricing is introduced.
- Example: first subscription on 10 March grants 10 March–24 March (end exclusive).
  With a 1 April boundary, request payment for 24 March–1 April. Full funding on
  15 March creates that future paid period; the 14 free days stay free. With an
  association-configured 18-day window for this illustration (default 14), the
  request remains valid until 28 March: qualifying funding on 26 March buys only
  26 March–1 April, not the unfunded two-day gap.
- Reconcile a changed initial charge with traceable pricing/period adjustments;
  retaining every PDF/full snapshot is unnecessary. Surplus becomes billing credit,
  never an automatic bank refund or purchase-balance deposit. Insufficient line
  funding remains held; other fully funded lines fulfil independently.

**Confirmed billing-boundary rollover**

- While still awaiting initial payment, refresh the group's single outstanding
  request. Replace elapsed unfunded service periods, but charge only uncovered
  days in the current/future aligned period. Preserve already-granted free days;
  neither accumulate unused periods as debt nor create a second open invoice.
- Preserve line funding and the original payment deadline. Rollover neither
  shortens nor restarts that window, and never grants another initial free period.
  Release repricing surplus to billing credit without sweeping purchase balance.
  Short-period merging does not amend deadlines; C1 confirms using the earliest
  relevant unfinished-charge date as the invoice's single visible due date.
- Example without a grant: add the service on 25 March, monthly boundary 1 April,
  first-payment deadline 8 April. Still awaiting payment on 1 April, replace the
  25 March–1 April charge with 1 April–1 May while retaining funds and the deadline.
  Reprice from the qualifying funding date on settlement.
- Example with free days: a 25 March subscription grants coverage to 8 April.
  Request 8 April–1 May; passing the 1 April boundary does not turn the free days
  into a 1 April–1 May charge. The shared schedule remains on the 1st.
- Retain charge facts and adjustments for timely delayed imports predating a
  rollover/removal. Correction is not routine repricing of completed purchases or
  automatic resubscription after a genuinely late payment.

**Confirmed first-payment window: independent of the free period**

- Default to 14 calendar days from issuing the service's initial payment request,
  configurable at association level. Adding an initial line to an already-open
  invoice starts that service's window then, not at the older invoice's issue date.
  The association's 14 free door days are a separate setting even though the default
  durations coincide. Resubscribers receiving no grant still get time to pay.
- Do not shorten the window to the next billing boundary. Members with zero free
  days also need a reasonable opportunity to pay. Crossing a boundary can require
  additional funding rather than always generate surplus credit.
- At the deadline, expire an unfinished initial line and release its unused funds.
  Remove the subscription once neither remaining coverage nor a valid initial
  payment window supports it. An unpaid first request receives no renewal grace.
  Request expiry does not by itself revoke remaining granted coverage, terminate
  membership or forfeit money. It also does not extend the grant when the window runs longer.
- Genuinely late payments use normal correction/review, not settlement of an
  expired request. After subscription removal the member/admin must subscribe
  again under the usual permission. There are no automatic repeat initial free days;
  the administrator's explicit configured-days checkbox remains the exception.

**Confirmed service renewal grace**

- Configure grace per service, allowing zero. This association's door service
  defaults to 7 calendar days after the paid term ends. Zero means no unpaid
  renewal access after that end; this does not shorten previously paid/granted time.
- An established paid subscription remains usable during its applicable renewal
  grace, subject to active membership, current permission and resource requirements.
  Grace is derived from the renewal request/dates, not recorded as paid coverage.
- Full funding during grace pays the original renewal period, including grace days.
  Do not reprice it as a new partial period beginning on the funding/receipt date
  or reset the shared calendar. This mirrors the agreed membership-renewal treatment.
- At unpaid grace expiry, remove the subscription and expire its unfinished lines,
  returning only unused held funds to billing credit. Preserve completed facts;
  create no debt or automatic refund. Returning requires normal self/admin
  resubscription, not settlement of old unfunded periods.
- Never stack renewal grace onto an unpaid initial subscription's free period or
  first-payment window, including an unfunded returning subscription with an admin
  free-days grant. Membership return, job retries and missing invoices do not create
  extra grace. Previously issued valid requests retain their recorded normal rules.
- Use the line's qualifying funding effective date for timeliness; bank receipts
  use the bank receipt date, not import time. Timely delayed imports may correct
  mistaken automatic removal without undoing an explicit stop/removal.

**Confirmed:** C1–C2 in [POLICY_REVIEW.md](POLICY_REVIEW.md) settle the single visible
invoice due date and original-anniversary phase for newly used intervals. Calendar
rules and service timing defaults are not proposals to approve again.
Stopping a service preserves remaining time. Automatic free days are once-only,
with the explicit admin checkbox exception; grant-aware initial pricing and the
independent payment deadline are confirmed. Resignation needs no automatic refund
for unused purchased service time; retain audit evidence and unspent-money claims
as described in [administrative pricing and service changes](OPEN_QUESTIONS.md#administrative-pricing-and-service-changes).
Security/API detail and retention validation remain
separate follow-up; the basic resource enabled/unavailable control is already included.

## 8. Phase 5: Storage

**Confirmed requirements**

- Administrators can define storage types and rooms/areas at runtime, without
  code changes. Examples include pallet, shelf and locker storage.
- Each individually identified rentable position corresponds to a specific
  physical location, grouped by the applicable type/room. Show its availability.
- A storage group is a pool of physical positions, normally containing multiple
  units (for example, a set of shelves or pallet positions), not another name for
  one position. Maintain a first-come-first-served queue per group.
- Give members a way to queue, reserve and pay for storage.
- Initially, a member may hold at most one storage position per group. They may
  hold positions in different groups; there is no initial cross-group limit.
- A current renter cannot join the same group's queue for another turn. They
  must wait until their existing allocation ends, then may explicitly join at
  the back of the queue; do not pre-queue them or carry over their former priority.
- Storage is a member service: the right to use it requires active membership,
  including grace. Inactive members cannot purchase storage or renew a rental.
- A membership lapse does not erase remaining purchased storage days or change
  the rental's paid-through date. Nevertheless, association rules do not permit
  an inactive member to use storage, even while that purchased term remains.
  This rare edge case is handled manually if necessary; do not build automatic
  cancellation, clearance/handover or refund logic triggered by a membership lapse.
  Storage terms remain independent of membership billing, and checkout does not
  require settling other invoices while membership is active.
- Use a next-available allocation model, not calendar-style future-date bookings.
- Administrators configure maximum continuous occupancy, the clearance period and
  the queue-offer acceptance/payment window separately for each storage group.
  These settings need not be the same across groups.
- A maximum rental period is enforced when there is a queue for the relevant
  storage. The maximum is not, by itself, a requirement to vacate uncontested
  storage when nobody is waiting.
- Measure the maximum from the start of continuous occupancy. Renewing the paid
  term must not reset this clock.
- When a queue appears, honour existing paid terms even for renters already beyond
  the maximum. Block further renewal once the maximum has been reached while
  people are waiting; do not shorten an already-paid term retroactively.
- While a queue exists, a newly purchased renewal must not extend continuous
  occupancy beyond the maximum; existing paid terms remain protected as above.
- After the last paid term ends, give the departing renter a short configurable
  clearance period, for example seven days, to remove their belongings before
  handing the position to the next queued member. Seven days is illustrative,
  not an agreed fixed duration or default.
- Do not support voluntary early release of a purchased rental. The allocation
  lasts until its paid term ends even if the renter removes their belongings
  sooner; do not implement an early-release cancellation/refund workflow.
- Once the paid term has ended, the departing member can mark the position cleared
  and release it for handover during the clearance period; no administrator
  verification is required. This does not shorten a paid rental term.
- If not marked cleared by the end of clearance, automatically make the position
  available for handover to the next queued member, flagging that it was not
  confirmed cleared. Do not block the queue awaiting administrator intervention.
- The association's intended rental rule authorises the incoming renter to move
  remaining items to a designated junk pile/clear-out area when taking over.
  Explain this rule in the rental conditions and relevant handover notices; the
  software must not pretend that the position is physically empty merely because
  its allocation changed.
- When a position becomes available, automatically email an offer to the next
  queued member and hold it for a configurable acceptance/payment window. If the
  offer is unused when the window expires, pass it to the next member. No routine
  administrator allocation or approval step is required.
- Declining an offer or letting it expire removes the member's queue entry; do not
  retain their place or automatically move them to the back of the queue. They
  may explicitly rejoin at the back, subject to the normal eligibility rules.
- Membership inactivity alone does not remove a waiting queue entry. Check
  membership when the entry reaches the front: if inactive then, remove the entry
  and immediately consider the next person, without issuing an offer or waiting
  through an acceptance window. Grace counts as active membership. Do not retain
  a skipped inactive member's place for later reactivation.
- Support fixed-term rentals rather than model every space as an indefinite
  generic service subscription.
- Administrators configure allowed rental durations and their prices per storage
  group; members choose from those options.
- Each rental purchase or renewal is an explicit one-time purchase. Renewal is
  manual: do not automatically generate renewal invoices or extend the rental.
- Storage payments are independent of recurring membership/service billing cycles;
  do not align rental terms with those schedules.
- Let administrators define spaces and see who holds each space and for how long.
- Address the problem of temporary storage being occupied indefinitely.

**Confirmed payment flow:** use available purchase balance for initial checkout, as
described under "Funded checkout and delayed payment requests". SEPA deposits use
the user's permanent credit reference; no direct SEPA reference is generated for
the storage purchase. Purchase balance must be enabled unless an alternate
instantly confirmed payment method is supported; billing credit alone does not
enable this feature, and there is no delayed-payment alternative.

**Possible later policy:** introduce a global, cross-group storage limit if the
per-group limit proves insufficient to prevent storage hogging. This is not an
initial requirement.

**Implementation follow-up:** choose initial configurable defaults and payment
integration delivery milestones; see [technical follow-up](OPEN_QUESTIONS.md#technical-design-and-implementation-follow-up).
Membership-lapse edge cases are manual, not a further automated refund/handover
policy to design. Voluntary early release is not supported; extensions use manual
renewal purchases.

## 9. Migration obligations

**Confirmed direction**

- Switch this association's operational system in one full-replacement cutover;
  do not plan parallel operation with payments split between the old and new
  systems. Reference-number and payment-format compatibility are central cutover
  concerns, not reasons to maintain two operational registries.
- Migrate existing data as well as the available source records permit. Some loss
  of historical detail or imperfect mapping is unavoidable and accepted; exact
  reproduction of the legacy model is not a migration requirement.
- Older history may remain in a read-only legacy archive rather than all being
  imported into the operational model. The exact import/archive boundary and
  retention arrangements still need specifying.
- Import existing paid-through dates as opening coverage, with discrepancies
  reviewed manually. Do not reconstruct those entitlements by replaying historical
  payments through the new billing rules. Opening coverage is a migration record,
  not a new payment receipt or a reason to recognise the same income again.
- Payment references change at cutover. Do not preserve legacy references as
  valid payment routes or build a legacy-reference compatibility layer.
- Notify all existing members of the change and the need to replace saved bank
  payment details/templates with the new references. Some transition confusion is
  expected and accepted; it is not a reason to retain the old reference scheme.
- Clear PDF invoices, which the old system does not provide, give members the new
  payment instructions and should help explain the transition.
- Money received with an obsolete reference still needs normal unmatched-payment/
  correction handling; this does not make the old reference valid in the new system.
- SSO is currently handled through Keycloak. Matrix and Nextcloud are the most
  important dependent services; continuity of existing accounts is a migration
  priority.
- Keep Keycloak as the SSO intermediary after replacement. Preserve existing
  Keycloak accounts and their associations with Matrix, Nextcloud and other
  dependent services rather than replace the identity intermediary at cutover.
- A one-time password reset is acceptable if needed for migration; preserving
  existing passwords is not a cutover requirement. A reset must retain the
  person's migrated account and SSO associations, not create a replacement identity.

### Proposed SSO identity compatibility

**Proposed migration approach; validate against the deployed configuration**

- Preserve each legacy user's existing upstream OIDC subject as a fixed
  compatibility identifier. The suggested value is their original email-based
  subject, not a live copy of their editable contact email. Import the actual
  identifier already used for the account rather than guess it from an email
  field if the two could differ.
- Use an opaque stable identifier, such as a UUID, as the OIDC subject for newly
  created users. Legacy subject strings do not need to dictate internal database
  identifiers or prevent new users from having sensible identifiers.
- Subsequent email changes must not alter either kind of subject or disconnect
  the user's existing SSO accounts.
- The goal is to avoid requiring account-identity migrations in Matrix, Nextcloud
  and every other dependent application merely to replace the registry.
- Do not assume Keycloak propagates the upstream subject unchanged. Check the
  actual identity-provider/broker links, issuer/provider configuration and subjects
  issued to applications. Keycloak's application-facing subject can differ from
  the registry's upstream subject; preserve existing links and application account
  associations, not just one token field.

**Confirmed development approach:** S0 automatically tests the replacement with
local Keycloak, synthetic users and a small downstream OIDC client: protocol/signing,
PKCE, email-change identity stability, disabled-login and existing-session boundaries.
It does not require the legacy registry, migration mapping or production access.
Password setup/reset and real admission/deletion checks belong to S1. S6 rehearses
the legacy switch with retained broker associations, frozen subjects, unchanged
Keycloak/downstream identities and validated claims/client settings. See
[the architecture rehearsal](ARCHITECTURE.md#local-oidc-integration-and-later-continuity-rehearsal).

**Deployment validation:** a local rehearsal cannot prove unknown production mappings.
Before cutover, the operator must confirm actual broker links, alias/external IDs,
issuer/client configuration, claim mappers and dependent account associations, ideally
using a redacted configuration export. This is not a requirement to experiment on
production or a blocker to implementation; see
[pre-deployment work](OPEN_QUESTIONS.md#before-deployment-not-reasons-for-a-universal-billing-engine).

### Remaining migration details

The [pre-deployment review](OPEN_QUESTIONS.md#before-deployment-not-reasons-for-a-universal-billing-engine)
consolidates source-data mapping, archive boundaries, opening coverage/credit,
integration inventory, imperfect historical data, cutover rehearsal and member
instructions. Migration implementation depends on those concrete inputs; the
entire billing architecture need not wait for every deployment detail.

**Existing-code reference:** users already have numeric database IDs, but email
is the login identifier (`../../mulysa/users/models/custom_user.py`) and is explicitly used
as the OIDC subject (`../../mulysa/api/mulysaoauthvalidator.py`). Migrating SSO requires more
than merely adding a new primary key. This code establishes the legacy upstream
behaviour, not how the deployed Keycloak configuration identifies users downstream.

## 10. From specification to implementation

- Review the intended system broadly enough to identify cross-phase dependencies.
- Resolve behaviour through worked examples, starting with membership and its
  connection to payments. Use these examples as implementation acceptance checks.
- Select architecture and divide implementation work only after the relevant
  concepts and rules are sufficiently clear.
- Keep implementation tasks bounded and tied to agreed rules; document any
  assumptions instead of allowing generated code to decide policy.
- Work can proceed incrementally. Not every later-phase detail has to be settled
  before earlier-phase implementation, but foundational dependencies must be.
- Use the consolidated [review index](OPEN_QUESTIONS.md) to distinguish policy
  choices from engineer-owned details and deployment checks. Review proposed
  defaults in short batches instead of treating every field/timer as another
  member-policy interview. Keep the ordinary bill/pay/coverage path visible;
  rare exceptions do not by themselves justify a general billing framework.
