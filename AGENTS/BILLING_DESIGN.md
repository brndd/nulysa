# Billing design

**Implemented in S2; this document records the reviewed design rationale.**
Current behaviour is specified in [SPEC.md](SPEC.md) and documented operationally
in [docs/billing.md](../docs/billing.md). The implementation in `src/Nulysa/Billing`
is authoritative where these conceptual fields differ.

The journal-only foundation, commands and execution traces below were reviewed as
workable. Independent fulfilment of fully funded lines replaced the earlier
whole-invoice requirement. Billing-credit ordering, effective dates and
expired-line removal remain agreed.

The [system architecture](ARCHITECTURE.md) reuses this foundation with EF Core,
PostgreSQL for all database profiles and Quartz.NET persistent background work.
The ASP.NET Core S0 must prove transactional scheduling/recovery with synthetic
effects; the Django implementation's tests are references, not proof of this stack
or billing correctness. This adaptation does not replace these models or
automatically approve their field-level details.

The ordinary path is: **prepare a combined bill -> allocate funding to its lines
-> fulfil each fully funded purchase -> repeat**. Annual payments, standing orders
and manually paid PDFs use that same path, not three payment engines.

The [service architecture](SERVICE_DESIGN.md) proposes the domain on the other
side of these billing instructions. Its owner/benefit/authorisation refinements
are incorporated below as proposals. Confirmed service policy now uses one
self-subscription setting for joining/returning and removes unpaid subscriptions
instead of retaining inactive services; model details remain proposals.
Membership-only inactivity blocks service use and new service invoices, not the
coverage clock or current subscription; no reapproval is needed while it remains valid.
Initial service access uses normally once-only free `Coverage`, with an explicit
admin checkbox exception for returning subscribers, not initial grace. Stop renewal
preserves remaining covered time while ending future charges. Recurring
charges are described as invoices/payment requests, not a separate purchase-offer
workflow; storage queue offers retain their ordinary meaning.
Service timing is confirmed: an association-wide configurable 14-calendar-day
first-payment window, and per-service renewal grace including zero, with a 7-day
door default. Renewal grace does not extend an unpaid initial subscription.
Positive initial/alignment periods shorter than a week merge with the following
full period, including billing-period-change bridges. When purchase balance is
enabled, members may explicitly transfer available funds between both balances.
The latest review also confirms one public earliest invoice due date, new intervals
phased from the joining anniversary, future-discount credits, grant-driven charge
reductions and ordinary 100% discounts for honorary membership. See
[POLICY_REVIEW.md](POLICY_REVIEW.md) for examples; representation remains design work.

## 1. EF Core entities and ownership of facts

Use ordinary entities, foreign keys, explicit EF Core mappings and migrations.
Parameterized PostgreSQL SQL is appropriate for row locks and queries EF cannot
express clearly, not a parallel hand-maintained application schema. No triggers,
generic workflow framework or application-wide event sourcing is proposed.

| Proposed model | In plain language |
| --- | --- |
| `BillingAccount` | Whose money and financial history is this? |
| `BillingGroup` | Which recurring fees share a calendar and payment reference? |
| `RecurringItem` | How is this member's membership or enrolled service billed? |
| `Invoice` | Which purchases are grouped into this bill and payment reference? |
| `InvoiceLine` | Which dated purchase is requested, funded and independently fulfilled? |
| `Receipt` | What money actually arrived, independent of how it is used? |
| `MoneyMovement` | Where did that money go, including later internal transfers? |
| `Coverage` | Which dated membership/service benefits were actually bought or granted? |
| `BillingOperation` | Which action produced these changes, and has this command already run? |

Representative fields below are conceptual property names, not final C# entity
definitions. Use C# `decimal` mapped to PostgreSQL `numeric` with explicit precision
and scale (two decimal places for stored charges), never `float`/`double`. Periods
and bank receipt dates use `DateOnly`/PostgreSQL `date`; creation/import/processing
timestamps use UTC `DateTimeOffset`/`timestamptz` (offset zero). Payment references route receipts to stored
records; relationships do not depend on decoding business fields from those strings.

### BillingAccount: one financial owner

One per member, owning both balances and that person's invoice-line funding. It is
**not** an organisation bank account, a general-ledger account, or another wallet.

- `member`: nullable foreign key/navigation to the live member/account, with a
  unique non-null member key and explicit `DeleteBehavior.SetNull`.
- `purchase_deposit_reference`: permanent unique reference for deliberate deposits.
- Stable primary key and creation timestamp; no authoritative editable balance fields.

Groups, movements and coverage refer to this account. After final member deletion,
necessary financial records/claims can remain separately without retaining a full
member profile. Their cleanup still follows the agreed retention policy. The row
also gives us a stable object to lock while changing this person's money.
This is a separate financial record, not a retained login account or shadow profile.
Rejoining after final deletion creates a fresh approved account; do not silently
relink old financial claims/history by matching an email. Manual claim handling
does not reconstruct deleted personal records.

### BillingGroup: a persistent calendar, not a particular bill

Example: Alice's monthly group is different from Alice's quarterly group, but her
monthly membership and monthly door fee share the same group.

- `account`: foreign key/navigation to `BillingAccount`.
- `interval_months`, `anchor_date`: preserve the intended day and calendar phase.
- `payment_reference`: unique and stable across successive invoices in this group.

`(account, interval_months)` is unique. A group can have many historical invoices
but at most one open invoice. An empty group can remain to preserve references;
removing a recurring fee must not erase it. No separate calendar per fee is needed.
**Confirmed phase:** derive newly used intervals from the original joining/approval
anniversary. Joining 12 March, monthly coverage through 12 July, then first choosing
quarterly billing bridges to 12 September and follows March/June/September/December.
Preserve an existing target group's calendar and already-issued/purchased periods.

Short bridges of fewer than seven calendar days are combined with the following
full target-group period and billed immediately. This applies to initial service
charges and billing-period changes, without moving anchors or rebilling covered
days. This combines the accepted merge rule with C2's confirmed anniversary phase;
see [POLICY_REVIEW.md](POLICY_REVIEW.md).

### RecurringItem: the member's billing instruction

For example, "bill Alice for door access every three months". This row does not
mean that access has been paid for, and it does not store a paid-through counter.

- `group`: foreign key/navigation to `BillingGroup` selected for future, not-yet-issued billing.
- `fee`: reference to the membership/service fee definition.
- Start/end of the billing instruction.
- Proposed typed owner: membership or one `ServiceEnrollment`, exactly one, with
  at most one live instruction per owner. Use ordinary typed relations and database
  constraints; commands also validate account/member/benefit consistency.

The owner authorises purchases and owns lifecycle changes; do not duplicate its
subscription state here. Billing interval/group selection belongs here, not in
the enrolment. Removing a live owner/instruction must not cascade into historical
lines, money or coverage.

Fee definitions supply labels, prices and service identity; their supporting
catalog/pricing models are not fully designed in this draft. The service proposal
uses an optional one-to-one link from a fee to `ServiceDefinition`; the membership
fee has no such link. These are shared configuration, whereas `RecurringItem` is
a particular member's choice. Changes
to that choice must respect already-issued periods before creating new charges.
Cancelling future billing is not itself a refund or erasure of purchased coverage.

### Invoice: the request and its lifecycle

One dated purchase request in a group, not the PDF file and not collectible debt.

- `group`: foreign key/navigation to `BillingGroup`; issue timestamp and stable internal ID.
- Proposed `state`: `open` or `closed`, with closure time/reason. Open means at
  least one current line is unresolved; closing the bill does not undo paid lines.
- Funding/fulfilment dates belong to lines, which can complete on different dates;
  there is no longer one invoice-wide purchase effective date.
- One public `due_on`, derived from the earliest relevant date among unfinished
  charges, including an earlier first-payment deadline. It is not a separately
  editable calendar or an invoice-wide expiry trigger. Recompute as charges are
  added, fulfilled, reduced/cancelled or expired; no public per-line due dates.

It has related lines and their money movements. Total, paid amount, held funds and
remainder are calculated, not independently editable. A bill can contain a paid
membership line and an unpaid door line; no extra partially-paid invoice flag is
needed. A paid line is not repriced just because its invoice remains open.
Internal charge/renewal-grace/payment-window facts remain charge-specific because
a bill can combine different lifecycle timings. The one public due date summarises
them; it does not shorten or reset another line's window/grace. Expired unfinished
lines are removed and the bill revised without resetting surviving deadlines.

### InvoiceLine: one quoted purchase, not a live fee lookup

For example, "Door access, 12 June–12 July, EUR 30".

- `invoice`: foreign key/navigation to `Invoice`; nullable historical origin link to
  the recurring item, explicitly using `DeleteBehavior.SetNull` if it is removed.
- Fee identity/description, `period_start`, `period_end` (end exclusive).
- Proposed stable benefit target, purpose (initial/renewal/resumption/alignment),
  owner authorisation context and logical purchase key. For a service, context is
  its subscription episode; resubscription is a new episode, not an inactive row
  being reactivated. These distinguish a valid payment request from a stale job
  and a new service from a duplicate cycle charge.
- `amount` and the pricing inputs used: base price/period, relevant discount
  segments and proration basis. Only relevant inputs, not a whole object snapshot.
- Internal charge/renewal boundary, `grace_until`, `payment_window_ends_on` as
  applicable; not another public/editable line due date. Initial free
  coverage dates belong to the grant, not an initial-grace field on this line.
  The initial window runs from issuance of this service's request/added line,
  not an older parent invoice; renewal grace runs from the paid term's end.
- Proposed `state`: `open`, `fulfilled`, `cancelled` or `expired`.
- `fulfilled_at`, `effective_on`: processing time and the effective funding date
  used for this line's price/coverage, kept separate for delayed bank imports.
  A zero-price fulfilment has no funding event to fabricate; proposed representation
  leaves that funding date null and records the actual discount/coverage basis.

It records the current quote. A later change to a fee definition or the recurring
item's selected group cannot silently rewrite what the line says. An explicit
repricing command can change an open line with recorded adjustment facts. Its held
funds and remainder come from linked movements, not an editable `paid_amount`.
Full funding fulfils that line and creates its coverage immediately. Incomplete
funding holds money without buying amount-proportional days. Completed lines and
their existing coverage remain intact when other lines change or expire.

### Receipt: an external fact, before matching

For example, "EUR 35 reached the bank on 11 June using reference X; imported on
12 June". It does not mean EUR 35 of services have been purchased.

- Source/provider, source bank/payment account and external transaction ID: a
  composite unique identity, not a key based on receipt date or payer name.
- `amount`, `received_on`, `imported_at`, original reference and required source
  details such as message/counterparty information.
- Nullable matched financial owner/routing information and review disposition.

A receipt can be unmatched or ignored as a donation, with no fabricated member
allocation. A matched receipt can produce several movements, such as EUR 30 into
a door line's funding and EUR 5 excess into billing credit. Reassigning a posted
receipt needs correction movements, not editing the bank fact or importing it again.
**Confirmed pending-deletion exception:** even a formerly recognised reference
fails automatic matching while its account is pending deletion. Show the source
receipt in the treasurer's failed-match queue, without allocation or coverage.
Keep this review disposition across repeat imports and administrator restoration;
manual repair after cancellation is acceptable. Do not silently rematch these
receipts, rescue the account or postpone final deletion.

### MoneyMovement: funding and balances share one source of truth

This draft represents receipt allocations and credit reservations in the movement
journal. A separate `InvoicePayment` table merely repeating allocated amounts
would duplicate that truth. The agreed journal-only approach now uses line-scoped
holds without an extra reservation model: the invoice line supplies identity,
purpose and lifecycle; its linked movements supply amounts and history. The
invoice view aggregates them.

- `account`: foreign key/navigation to `BillingAccount`; positive `amount`, fixed `kind`.
- `operation`: foreign key/navigation to `BillingOperation` and ordering within it.
- Receipt/invoice-line/rental references required by that kind; correction entries
  reference the movement they reverse.

The kind determines the source/destination below, rather than arbitrary user-
defined ledger accounts. A receipt-backed movement links its actual receipt;
spending pooled credit links the pool and purchase, not a fictitious new receipt.
Receipt history remains traceable without introducing deposit-lot accounting.
Balances and funding are aggregates over these movements; corrections are linked
reversals/transfers. P1's reviewed credit for discounted future purchases must link
to the original completed purchase and adjustment, not invent a receipt or release
an already-spent hold. Preserve original purchase/coverage facts and export the
correction separately; exact movement kind/accounting mapping remains design work.
Exceptional reversals and manual refunds require explicit treasurer handling.

**Reservation semantics, not physical money transfers:** allocating billing
credit to a line earmarks it. It decreases available billing credit and increases
that line's held funds, without a bank transfer or purchase. Held funds remain
member advances until that line is fully funded. Use three explicit operations:

- **Reserve:** commit credit to one line so another purchase cannot use it.
- **Release:** return unused held funds to available billing credit on cancellation,
  expiry or reduction of the request.
- **Commit:** consume the line's held funds only when that purchase can be fulfilled.

For EUR 10 billing credit against one EUR 40 line, reservation leaves EUR 0 available,
EUR 10 held and EUR 30 still required. Cancelling that unfulfilled line releases
EUR 10; receiving another EUR 30 allows its EUR 40 purchase to complete. Purchase
balance is unaffected. Movements reference the line, from which the invoice is
known; neither owns another independently editable balance.
Safety comes from atomic reserve/commit/release and no double spending, not from
copying debit-card network expiry or settlement machinery.

### Inspecting and repairing invoice holds without another model

The admin UI should present a first-class funding view even without a reservation
table: invoice/line ID, status, amount, held funds, remainder and source history.
Show paid lines separately from held money on unfinished lines. Follow receipt ->
movements -> line -> invoice; no separate reservation identity is needed.

Release/cancellation is a financial command, not deleting or editing old movements.
Record actor/reason and atomically change unfinished lines and append releases.
Completed/cancelled/expired lines should have zero held funds. Cancelling a bill
only closes unfinished lines; it does not refund completed purchases or revoke
their coverage. Reconciliation flags inconsistencies for investigation rather
than silently changing balances. Locking and idempotency
provide fault tolerance regardless of whether a reservation model exists.

**Important implementation constraint:** releasing a line's hold while leaving it
automatically fundable could reserve the money again immediately. Cancel/requote
the relevant unfinished line and release its hold together. Keeping it open but
deliberately unfunded would need an explicit review/pause policy; do not add that
control silently.

A queue entry or time-limited storage offer does not require holding purchase
funds. Checkout can atomically debit purchase balance and allocate the position.
There is therefore no current second durable money-hold purpose to justify
`FundsReservation`. Revisit that model only if an actual independently managed
hold is required later, rather than generalising for a hypothetical queue need.

### Coverage: a completed or granted benefit, not an access decision

This applies to **basic membership as well as optional recurring services**. For
example, "Alice has paid membership coverage for 12 June–12 July" and a separate
record for paid door coverage over the same dates.

- `account`, `starts_on`, `ends_on` (end exclusive).
- Proposed explicit benefit target: basic membership or one stable
  `ServiceDefinition`. Fee identity remains a source/pricing fact, not the only
  eligibility key; neither a price change nor a new subscription episode
  invalidates paid time. Coverage itself does not restore current permission.
- Source kind: completed invoice line, initial free period, administrator grant or
  migration record. The proposed automatic `initial_free_period` kind has a once-only
  grant key/constraint per account and stable service, not per subscription episode.
- An administrator ticking **Grant configured free days** on a returning subscription
  creates an `administrator_grant`, with the introductory-days reason, actor,
  configured duration/dates and a per-command retry key. It does not reset automatic
  eligibility or bypass the automatic grant's uniqueness constraint. A later,
  explicitly selected admin exception is distinct from retrying the same grant.
- Source reference/key unique enough to prevent a retry granting it twice.

Completing a line creates its coverage in the same transaction, without waiting
for other lines on the bill. Initial free days, administrator grants and migration
create coverage without fake receipts or money movements. Once-only initial-grant
eligibility survives subscription removal/resubscription and billing-period changes;
retain the necessary prior-subscription/grant fact while the account exists. A
retry-safe grant key alone does not make a returning subscriber eligible again.
Returning members get no automatic repeat; only the explicit admin-only checkbox
can grant the configured introductory days again. Use the same coverage/pricing
rules for this audited exception, with no new balance, receipt or money movement.

Membership and service renewal grace are computed from lifecycle/deadline rules,
not misrepresented as paid coverage. Initial service access has no grace branch:
it uses granted coverage. A door check combines coverage with active membership
and any approval/qualifications.
Membership coverage does not establish the person's association membership:
approval does that. A member can remain active during grace without paid coverage
for the current period, and remains a member when inactivity restricts benefits.
Coverage queries respect gaps: taking the greatest end date would incorrectly
grant access through an intervening inactive month. Storage keeps its own rental
record rather than being forced into this recurring-coverage model.

**Confirmed initial free-day billing:** do not later charge days granted for free,
including when the first payment arrives before the grant ends. For the initial
charge, paid coverage starts at the later of the first uncovered day and that
line's funding effective date, ending at the request's aligned boundary. A new
positive initial/alignment request shorter than a week includes the following
full period under the confirmed merge rule. Proposed representation:
one extended line with dated pricing segments; other services still fulfil
independently. Merging must not reset deadlines or continually append another full
period on every refresh. The earlier-initial-deadline case is C1 in the policy review.
It now uses one invoice-visible due date: 28 March rather than 1 April when the
initial deadline is earlier, without changing any internal window or free days.
Assess merging when preparing that new bridge. Proposed execution interpretation:
receipt-date repricing reducing an existing eight-day request to six days does
not itself add a full month. Its aligned end stays unchanged unless ordinary
rollover requires replacing an entirely elapsed unfunded period.
The funding date itself is still the last required receipt/credit-application date;
it can precede a future paid coverage start. Cross-boundary grants neither shift
the shared schedule nor produce charges for free-covered days. A payment after the
free period, while the request remains valid, buys from its effective date without
billing the unfunded gap. Partial funding still buys no proportional days.

The first-payment deadline is separate from free coverage expiry: an initial
subscription can remain awaiting payment without use after the grant ends. Request
expiry must not revoke remaining granted time; remove when no coverage or valid
initial-payment window supports it, without adding renewal grace. Membership's
initial grace and ordinary renewal-pricing rules are unchanged.

**Confirmed service timing:** the initial window defaults to 14 calendar days from
issuing the service's first payment request, configurable association-wide. At its
deadline, expire the unfinished line and release unused funds; retain any remaining
free coverage, then remove when no coverage/window supports the subscription. Do
not stack renewal grace onto an unpaid initial request or introductory grant.

For established paid subscriptions, renewal grace is configurable per service,
including zero; this association's door default is 7 calendar days after the paid
term ends. A line fully funded during that grace settles its original renewal
period, including grace days, without receipt-date proration of those days. Unpaid
grace expiry removes the subscription and releases only unfinished holds. Zero
grace allows no unpaid use after the term ends. Bank receipt dates, not import
times, determine timeliness; this does not turn grace into paid coverage or debt.

### Confirmed discounts, grants and zero-price periods

- **P1:** apply a new discount to unused future time equally for unpaid/prepaid
  members. Reprice unfinished lines and release surplus, preserving their timing.
  Completed future purchases use administrator-reviewed, audited credits linked to
  the original purchase. Do not rewrite original money/coverage facts or routinely
  replay completed purchases; no automatic bank refund is implied.
- **P2:** a coverage grant cancels/reduces now-covered unfinished charges and returns
  their unused/surplus funding to billing credit. Preserve the group calendar and
  independent service lines; ordinary allocation can use the released money.
- **P3:** honorary membership is an ordinary 100% discount with a distant explicit
  expiry (e.g. approximately 1,000 years), not a new exemption model. Establish
  eligible zero-price periods as covered with no external payment or fictitious
  zero-value receipt/movement; release any real prior holds normally. Do not leave
  them awaiting payment or trigger inactivity for nonpayment of zero.

Proposed representation: allow nonnegative line amounts and fulfil an authorised
zero-price line immediately, creating retry-safe `Coverage` with its discount/
pricing basis and no spending movement. A request containing only resolved zero
lines need not remain open or demand a payment. Exact presentation/storage is
engineering work; keep positive-only money movements. Renew on the ordinary
calendar, split at discount expiry, and respect admission, explicit subscription
removal and pending deletion. Do not grant a millennium of coverage merely because
the discount lasts that long, or build a separate fee-exemption subsystem.

### BillingOperation: one action can change several records

Examples are processing a receipt, generating a cycle's bill, cancelling a request
or recording an administrator adjustment.

- `key`: unique stable retry/idempotency key; `kind` and processing timestamp.
- Actor/reason where applicable, affected records/result and relevant adjustment
  facts, such as old/new line amounts and periods.

The operation groups its money movements and other changes. Retrying a bank job
or bill-generation job finds the previous result instead of doing it again. It
also records actions with no money movement, such as changing a quote. Internal
helper calls need not each create another operation. This is not an execution
queue, serialized executable call or full historical version of every object.

**Derived, not independently editable:** use these aggregates, not three editable
money counters on a bill:

- Current bill total: amounts of fulfilled and still-open lines; expired/cancelled
  requests are no longer payable. Their original amounts/change history remain traceable.
- Paid amount: completed line purchase amounts, not currently held money.
- Held amount: sum of open lines' funding movements less returns.
- Remaining amount: sum of open lines' prices minus their held amounts.

Each available balance is derived from movements too. Completed line funding has
been spent and is not held liability. A partially paid bill therefore does not
mistake EUR 10 already spent on membership for EUR 10 still available to its door
line. Any future cached totals must reconcile to these facts.

Use a small fixed set of movement kinds:

```text
receipt -> line funding          receipt -> billing credit (excess)
receipt -> purchase balance      billing credit -> line funding
line funding -> billing credit (return/surplus)
line funding -> completed line purchase
purchase balance -> completed storage purchase
billing credit <-> purchase balance (explicit member transfer; feature enabled)
```

For example, billing credit is receipts/returns plus purchase-to-billing transfers,
minus line funding and billing-to-purchase transfers. Purchase balance is deposits
plus billing-to-purchase transfers, minus reverse transfers and purchase spending. Line funding
is funding in minus returns and fulfilment spending, with linked reversals included.
After fulfilment held funds are zero; its state and fulfilment movement explain
that the line was paid rather than making it appear unpaid again.

An internal transfer is one movement, not another bank receipt. Both balances and
held funding are advances until spent; preserve receipt provenance and split
completed purchases by line/category in exports. Import adapters retain outgoing
bank data too, but do not turn it into incoming receipts or manufacture member
allocations for ignored donations. A provider payout into the bank reconciles an
existing provider receipt; it must not fund the member a second time.

### How the records connect: one EUR 40 bill

```text
BillingAccount A (Alice)
  BillingGroup G (monthly, anchored to the 12th, reference X)
    RecurringItem R1 (membership)   RecurringItem R2 (door)
    Invoice I
      Line L1 -> R1: 12 June–12 July, EUR 10
      Line L2 -> R2: 12 June–12 July, EUR 30
```

These are ordinary renewal charges paid within their payment/grace windows, not
receipt-date initial-activation quotes. With no other open bills or starting balances:

1. Receipt P1 is EUR 10. Operation O1 records M1: `P1 -> L1`, EUR 10, and M2:
   `L1 -> completed purchase`, EUR 10. Create C1 membership coverage immediately.
   L1 is fulfilled, L2 remains open; the bill has EUR 10 paid and EUR 30 remaining.
2. Receipt P2 is EUR 35. Operation O2 records M3: `P2 -> L2`, EUR 30; M4:
   `P2 -> billing credit`, EUR 5; M5: `L2 -> completed purchase`, EUR 30.
3. O2 creates C2 door coverage and closes I because no unresolved lines remain.
   Both lines have zero held funding; billing credit is EUR 5 and purchase balance zero.

The next month normally reuses A, G, R1 and R2, but creates another invoice/lines,
receipts/movements and completed coverage. That is the distinction between the
instruction to bill, this bill's quote, actual incoming funds, and purchased time.

## 2. Invariants and state

- Each posted member euro is in exactly one place: billing credit, purchase
  balance, held by an open line, spent, or refunded. No negative balances.
  Receipt-backed inflows cannot exceed their verified source amount.
- Allocate to unfulfilled lines, membership first. A fully funded line fulfils
  independently; an incomplete line buys no amount-proportional days.
- Reprice only unfinished lines, preserve funding and return any surplus to billing
  credit. Expiry/cancellation returns only the affected line's unused held funds.
- A reviewed future-discount credit is an explicit correction of a completed
  purchase, preserving its original facts; it is not routine line repricing.
  Eligible zero-price periods complete without money, never phantom receipts.
- Purchased and granted coverage survives an unrelated charge being overdue.
  Access still checks active membership, service approval and qualifications.
- Renewal due/grace, first-payment deadline and initial free coverage expiry are
  different concepts. Expiry creates no debt and cannot manufacture a new request,
  automatically repeat initial free days or restart renewal grace. Explicit admin
  grants are separate, audited commands.
- Stopping service renewal cancels only its unfinished charges and releases their
  unused funding. Remaining paid/granted coverage and already-completed future
  periods stay intact; do not invoice new renewals, grant fresh unpaid grace or
  automatically refund/revoke completed purchases. Other bill lines are unaffected.
- Resignation may drop unusable services without automatically refunding/crediting
  unused purchased time. Keep purchase/period/removal evidence for manual handling;
  do not confuse it with available balances/unfinished holds, whose financial claim
  survives deletion. This is automation scope, not a legal waiver of refund rights.
- Receipt-date repricing determines initial/resumption pricing at completion;
  renewals fully funded during grace keep the original period, including grace days.
  Ordinary later imports do not keep moving its start date or refund another day's
  price. For example, EUR 10 bought coverage from 6 June to 1 July; importing again
  on 9 June does not move that start date to 9 June or generate another credit.
  An unfulfilled line may still be repriced with a recorded reason. Corrections
  remain traceable operations, not silent rewrites; retaining every PDF is unnecessary.
- Purchase balance is configured in deployment files, not toggled in the admin UI.
  Recurring processing never sweeps it; disabling it after use has no automatic
  migration workflow. With it enabled, members may explicitly transfer available
  funds in either direction. A transfer never releases line holds or reverses a
  purchase; money moved into billing credit becomes automatically fundable.

Express row-local checks and uniqueness through EF Core mappings/migrations to
PostgreSQL check constraints and unique/partial unique indexes: positive movement
amounts, nonnegative line prices, valid coverage intervals, unique receipt source
keys/operation keys, and one open invoice per group. Cross-row money and eligibility
rules also require transactions; completed coverage has a unique source so retries
cannot duplicate it. Model validation and EF change tracking cannot prevent a race.
Configure deletion behaviour explicitly: required relationships must not inherit
cascade deletion that destroys retained financial history. Verify constraints,
exact decimal aggregates and competing commands against real PostgreSQL, not EF's
in-memory provider or a substitute SQLite database.

## 3. Commands, not hidden model side effects

Use explicit C# command methods rather than financial work in ORM interceptors,
entity setters, implicit event handlers, `SaveChanges` overrides, template rendering
or permission checks. Commands are ordinary direct calls, not a mediator pipeline.

| Command | Changes in one committed operation |
| --- | --- |
| Prepare/refresh bill | Ask each owner whether this charge may be issued; exclude granted/paid days and price the uncovered period. Fulfil authorised zero-price lines without money; derive one public earliest due date for remaining charges. Do not reset timing/funding or recreate expired requests. Deduplicate logical purchases, not just group/cycle: a new service can need a bill after that cycle's earlier bill was paid. |
| Grant initial free days | Within the subscription command, grant automatically only to first-time eligible subscribers, or record an administrator's explicit repeat-subscriber checkbox exception as a separate admin grant. Snapshot configured days/dates and deduplicate by the appropriate automatic/per-command key. No receipt or money movement; bill only later uncovered dates. |
| Receive/match payment | Deduplicate the receipt; route valid references to their group/deposit purpose. Pending-deletion references fail matching and remain in treasurer review, even after restoration/reimports unless manually repaired. Allocate other valid receipts to open lines in priority order; excess goes to billing credit. Unknown/ambiguous receipts stay in review; ignore fabricates no allocation. |
| Apply billing credit | Choose valid open bills by the agreed order, then fund their unfinished lines in priority order; complete funded lines. Never take purchase balance. |
| Transfer between balances | Require purchase balance enabled, ownership and sufficient available source funds. Append an audited internal movement under the account lock; no receipt/income or admin approval. Purchase-to-billing transfers then invoke ordinary credit allocation in the same transaction. Held/spent funds cannot be moved; application date, not original deposit date, governs newly applied credit. |
| Complete line | Apply initial/resumption receipt-date pricing or original-period renewal-grace settlement as appropriate. Verify funding/owner authorisation, spend held funds and create coverage atomically. Close the invoice when all lines are resolved. Do not call the resource-use predicate or silently resubscribe a removed service; buying membership does not require active paid membership. |
| Revise/cancel/expire | Apply the initial deadline or established-service renewal grace end to unfinished lines; return surplus/unused funds to billing credit. Coordinate owner removal without stacking grace onto initial requests. Never erase/refund fulfilled lines, revoke remaining free coverage solely for request expiry or recreate an expired request. |
| Adjust discount/grant | Reprice/cancel/reduce unfinished charges from explicit effective dates or newly granted coverage, preserving timing/other lines and releasing surplus. For discounted completed future purchases, require administrator review and append a linked credit correction; retain originals. Re-derive the invoice due date. |
| Stop service renewal | Record stop intent and cease new charges; cancel only this service's unfinished lines and release their holds. Preserve the subscription for remaining paid/granted use, including completed future periods; close it at the covered end. Do not cancel other lines, create new unpaid grace or automatically refund. |
| Resume membership | Preserve its schedule and prepare the prorated return request; use the same funding/completion path. Eligible incoming payments may trigger this when automatic membership reactivation is configured. Pending deletion cannot be rescued by payment; removed services need explicit resubscription under their usual self-subscription/admin rule. |
| Buy storage | Check active membership, offer/position, term and purchase balance; debit and allocate/extend the rental together. Failure commits neither. No recurring invoice or unit-specific SEPA reference is needed. |

**Proposed concurrency approach:** commands changing a member's money open an
explicit EF Core transaction and lock the `BillingAccount` with parameterized
PostgreSQL `SELECT ... FOR UPDATE` before loading authoritative mutable facts.
EF Core has no assumed portable row-lock LINQ operator; use the actual transaction's
connection for lock SQL and avoid stale tracked entities. Unmatched source imports
have no member money to lock; receipt identity still prevents duplicate import.
All financial entry points use that lock order. Storage also locks its group and
offer/position consistently and enforces a unique live allocation: a member lock
alone cannot stop two members buying the same position. Test races with independent
DbContexts/connections and real commits/rollbacks on isolated PostgreSQL databases.

Save the unique operation/result and any durable email/job requests in the same
transaction. Enable Quartz's `AcceptEnlistedTransactions`, enlist the actual EF
transaction with `scheduler.EnlistTransaction(tx.GetDbTransaction())` around awaited
scheduling calls and commit before disposing the enlistment. A separate connection,
ambient transaction alone or after-commit enqueue is not equivalent. Roll back the
whole command on scheduling failure. Do bank/provider HTTP calls outside money
locks. Retries return an existing result rather than repeat movements, coverage
or rentals; retry a failed database transaction as a whole with a fresh context.

The proposed service contract extends this same lock/transaction boundary to
membership/authorisation commands that change purchase eligibility. A revocation
must not race line completion through a separate uncoordinated write path.

## 4. Execution traces

Examples are independent unless stated; assume no other open bills to absorb
billing credit. Amounts and dates are illustrative, not deployment defaults.

### All three payment habits use the same records

Membership costs EUR 10/month and door access EUR 30/month:

| Member habit | Bill and receipt | Result |
| --- | --- | --- |
| Manually pays monthly | Two lines total EUR 40 for 12 March–12 April; receives EUR 40. | Both lines fulfilled; bill closed; two coverage records. |
| Standing order | Same monthly group/reference; the next EUR 40 funds the next bill. | Same operation, not a separate subscription/payment-counter mechanism. |
| Pays three months upfront | Chooses three months for both items; EUR 30 + EUR 90 on one bill for 12 March–12 June; receives EUR 120. | Same operation, with longer dated coverage. No compulsory wallet. |

### Partial payment, completion, then reuse of excess

Start with a EUR 40 renewal bill, no billing credit, EUR 50 purchase balance:

| Event | Invoice funding/state | Billing credit | Purchase balance |
| --- | --- | --- | --- |
| Receive EUR 10 on its invoice reference | Membership line fulfils immediately; EUR 10 paid, nothing held, EUR 30 door charge remains. | EUR 0 | EUR 50 |
| Receive another EUR 35 | Door line fulfils for EUR 30; invoice closes. Membership is not bought again. | EUR 5 excess | EUR 50 |
| Next EUR 40 bill is prepared | EUR 5 held on its EUR 10 membership line; no new coverage yet; EUR 35 remains overall. | EUR 0 | EUR 50 |

If the remaining door line is cancelled after the first receipt, membership
coverage and its spent EUR 10 remain intact. There is no unused door funding to
return. Alternatively, if EUR 15 had arrived first, membership would fulfil for
EUR 10 and EUR 5 would be held on door access; cancelling that line returns only
the EUR 5 to billing credit. Purchase balance remains EUR 50 in both cases.

### Receipt-date repricing, not PDF-date pricing

A service without an initial free grant requests EUR 12 for 1 June–1 July (30 days).
Bank receipt is EUR 12 on 6 June, imported on 8 June while the request is valid.
Reprice to 6 June–1 July: EUR 12 x 25/30 = EUR 10. Record the price adjustment,
return EUR 2 surplus to billing credit, consume EUR 10 line funding and create
coverage from 6 June. Access could only respond after import; no new bank receipt
or purchase-balance deposit is invented. Partial-payment date selection is below.

### Once-only free days followed by paid coverage

An illustrative service costs EUR 31/month on a 1st-of-month schedule. Its first
subscription on 10 March grants 14 free days, 10 March–24 March (end exclusive).
Request EUR 8 for 24 March–1 April: EUR 31 x 8/31. Full funding on 15 March records
that receipt date but creates paid coverage starting 24 March. The free grant is
neither billed retrospectively nor replaced by overlapping paid days.

If no payment has arrived by 24 March, access ends but a separately valid first-
payment window can keep the subscription awaiting payment. With the association
explicitly configured to an 18-day window for this example (default 14), the deadline
is 28 March. Full qualifying funding on 26 March prices 26 March–1 April
at EUR 6, with no debt for the two-day gap. Partial funding is still held until
the line is fully funded. Later resubscription does not automatically grant another
free period; an admin must explicitly select the configured-free-days checkbox.
If instead the original grant ran 25 March–8 April, the first charge would cover
8 April–1 May; neither the grant nor the shared schedule is truncated/reset.

### Return after inactivity and one-time checkout

- Monthly membership remains anchored to the 12th. A valid return request is
  priced on 27 March: EUR 10 x 16/31 = EUR 5.16 to 12 April. EUR 10 received on
  that date funds it and leaves EUR 4.84 billing credit; the unpaid inactive months
  are not charged. The next boundary remains 12 April. A still-current paid door
  subscription needs no reapproval; a removed one needs normal resubscription.
- A EUR 60 payment on the purchase-deposit reference makes purchase balance EUR 60,
  not invoice funding. A permitted EUR 20 storage checkout creates its rental and
  leaves EUR 40. Failed availability/funds checks change neither. Billing credit
  and existing purchased coverage are unaffected by this checkout.

### Explicit transfer and short-period merge examples

- Available billing credit EUR 8 and purchase balance EUR 20: transfer EUR 5 to
  purchase balance, leaving EUR 3 and EUR 25. No purchase, income or bank movement.
- Purchase balance EUR 50 and open EUR 15 membership/EUR 10 door lines: transfer
  EUR 20 into billing credit. Membership fulfils for EUR 15; EUR 5 is held on door;
  purchase balance is EUR 30 and available billing credit zero. That EUR 20 is not
  still freely transferable back. No second receipt or mandatory new balance exists.
- With a EUR 30 monthly fee, no grant and a boundary on the 1st, an initial charge
  starting 28 March merges EUR 3.87 for the four-day bridge with EUR 30 for April:
  EUR 33.87 requested immediately through 1 May. A qualifying receipt of EUR 33.87
  on 3 April reprices the unfinished initial period to EUR 28 for 3 April–1 May,
  leaving EUR 5.87 billing credit, assuming no other requests consume it. The
  14-day initial window still runs from 28 March; grants/deadlines are not extended.

### Confirmed future adjustments and honorary membership

- July service costs EUR 30; a 50% discount effective 1 July makes the unfinished
  charge EUR 15. EUR 10 held leaves EUR 5 required. If July was already purchased,
  an administrator reviews a EUR 15 billing-credit correction; retain the original
  EUR 30 purchase and coverage, with the correction linked separately.
- Grant July membership when its EUR 10 line has EUR 5 held: cancel that unfinished
  charge and release EUR 5 to billing credit. July is covered by the grant, not a
  fabricated payment. Its independent door line/calendar are unchanged.
- Approved honorary member, 100% membership discount ending roughly 1,000 years
  later: establish eligible periods on the usual calendar without money. No receipt,
  zero-value movement, awaiting-payment status or dedicated exemption model.

## 5. Review outcomes

### Confirmed billing-credit priority

Automatically allocate to the earliest payment due date, then issue order/ID,
across valid open groups. Use the bill's single public due date, derived from the
earliest applicable unfinished-charge date. A directly referenced receipt still
targets its own group. Within the chosen bill, allocate new money to membership
first, then the configured optional-service order. Do not reshuffle committed
funding or completed purchases when priorities/settings change. A deterministic
charge-date/line-ID tie-break within equal priority is an engineering proposal,
not a requirement to present or edit separate line due dates.

### Confirmed effective date with several funding events

Apply the agreed last-required-funding-event rule separately to each line: a new
bank receipt uses its receipt date, not import time; previously available credit
uses its application effective date. Do not use an old credit's original bank date for
newly bought coverage, or silently reprice daily until insufficient funds happen
to become sufficient. Processing timestamps remain separate. Out-of-order receipt
handling still needs engineering tests, not another policy choice about using
import time instead of bank receipt time.

### Confirmed: independently fulfil funded lines; remove expired unfinished lines

This can arise after adding a service mid-cycle with an independently timed first-
payment window. The association-wide default is now confirmed as 14 calendar days
from that service's initial request. Ordinary aligned renewals need not have this
mismatch. Illustrative combined bill:

| Charge | Amount | Timing |
| --- | --- | --- |
| Membership renewal | EUR 10 | Due 7 July, active during grace until 14 July. |
| Returning door subscription, admin grant box unchecked | EUR 30 | Its 14-day first-payment window ends on 8 July. |

The invoice shows one due date, **7 July** while both lines are unfinished; this
table explains internal timing, not a required public per-line-date layout. If the
membership line completes first, the remaining door charge gives a visible 8 July
due date without resetting its original window or membership's grace facts.

When EUR 10 arrives, fulfil membership immediately. On 8 July expire/remove only
the unfulfilled door request; there is no membership funding to refund and repurchase.
Membership's price, coverage and original grace facts remain intact. If the door
line had partial funding, return just that unspent amount to billing credit.

Updating the bill in place is sufficient; recreating its unresolved portion is
an engineering option, not a requirement. Completed purchases stay linked to
their original line/source and must not be copied as new payable charges. The
replacement must not re-add the same expired request or reset surviving deadlines.

This change is chosen to reduce timing/expiry coupling and fulfil useful coverage
sooner, not to introduce a separate pick-and-choose checkout feature. It supersedes
the earlier whole-invoice discipline. Funds still need to complete an individual
line; EUR 6 toward a EUR 10 line buys no fractional membership period.

**Confirmed optional-service nonpayment follow-through:** remove the subscription
when its own nonpayment threshold is reached, rather than retain an inactive
service or keep generating unwanted charges. End its instruction and unfinished
requests, releasing only their unused funding. Preserve completed purchases and
reference/financial history. Returning means the member/admin subscribes again
under the same permission as first subscription; no separate reactivation setting
or payment-triggered resubscription. The [service architecture](SERVICE_DESIGN.md)
proposes subscription-episode records. Timing is settled: default 14-day initial
window and per-service renewal grace (zero allowed; door default 7 days). Voluntary
stop renewal preserves remaining time rather than revoking it.
Removal does not terminate
association membership, create debt or erase qualifications.

Cross-balance transfers, merging, calendar phase/public due date, zero-price coverage
and P1–P4 adjustments/resignation scope are settled in
[POLICY_REVIEW.md](POLICY_REVIEW.md). Correction representation, exceptional manual
handling and privacy/deployment validation remain in [OPEN_QUESTIONS.md](OPEN_QUESTIONS.md).

**Assessment:** this is a conventional relational billing component with a small
money journal. The unavoidable work is dated pricing, line funding/fulfilment and
correct reversals, not multiple billing engines. A simple priority allocator is
now required, but no general ledger, PDF-version platform or universal subscription
state machine is needed. Operation/coverage records earn their place by preventing
duplicate purchases and preserving grants/migration facts without fake payments.

## 6. Proposed service integration: compatibility assessment

The service draft fits this billing foundation with four targeted refinements:

1. Typed membership/enrolment ownership of recurring instructions.
2. Stable membership/service benefit targets for coverage, not just fee identity.
3. Line purchase purpose/authorisation context, separate from live resource use.
4. Purchase-scoped generation keys: one open bill does not prohibit another bill
   for a newly added service after the earlier bill in that cycle was paid.

The approved initial free days reuse `Coverage` with an automatic once-only grant
source and first-time eligibility across episodes. The explicit admin checkbox
exception uses separately audited administrator coverage. Initial pricing
must skip those granted dates; funding effective dates can precede future paid
coverage. No additional model, special access state or money movement is needed.

Keep independent line fulfilment, balances, reference routing and journal-only
holds. Billing supplies calendar/pricing/funding functions; the owner determines
whether a charge may be issued/fulfilled and whether nonpayment changes its
lifecycle. Financial completion never unconditionally restores service permission.

Stop renewal ends new billing and unfinished lines, not remaining paid/granted use;
the subscription lasts until its retained coverage ends. Immediate administrator
removal is separate and ends the episode/instruction and unfinished lines together;
it does not delete paid coverage or financial facts. A new subscription has a new
context, reuses the existing group schedule/reference and purchases only uncovered
time. This simplifies the earlier proposed inactive-service/billing-pause flow.

Existing service requests retain normal expiry when membership becomes inactive;
stopping new invoices must not silently cancel them. Retain their normal funding/
authorisation handling without inventing pending-reactivation-approval holds or
using membership return as a new funding date. No new service requests are issued
while membership is inactive. Receipts can still arrive, but without a valid request
they cannot fabricate a renewal or extend coverage; normal credit/review handling
preserves the money. Ordinary resubscription is authorised before its new request.

The subscription and coverage are not paused: paid dates continue towards their
original expiry. If membership returns while the subscription and coverage remain
valid, service use can resume without reapproval. If the service expires and is
removed first, normal resubscription is required. Its expiry/removal command must
work even when no renewal invoice exists; absence of a bill cannot leave it
subscribed indefinitely or manufacture grace. Apply the confirmed initial/renewal
rules; membership lapse grants no extra service time or renewal request.

Delayed imports need both invoice-line history and command causes. A timely receipt may
correct a mistaken automatic subscription removal or use terms predating quote
rollover, without undoing an explicit stop-renewal/removal or replacing a newer
subscription. Do not implement this by routinely replaying/repricing completed
purchases. Reallocated money or ambiguous intervening commands use the existing
correction/review path. Test these cases alongside
partial combined bills, retained paid door time and concurrent service withdrawal;
there is no requirement for another engine, balance or reservation model.
