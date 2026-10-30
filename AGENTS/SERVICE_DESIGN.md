# Service architecture for review

**Foundation reviewed as workable; model details remain proposals. No service implementation yet.**
This builds on [SPEC.md](SPEC.md) and the reviewed [billing design](BILLING_DESIGN.md).
Confirmed: one self-subscription setting governs joining and returning; sufficiently
long service nonpayment removes the subscription, not a service-inactive state.
Membership-only inactivity blocks use without pausing coverage or requiring service
reapproval; new service invoices stop, and coverage continues towards normal expiry.
Initial service access uses genuine free coverage, normally once per member/service,
not initial grace; administrators can explicitly grant the configured days on a
returning subscription. This association grants 14 initial free door-access days.
Stopping renewal preserves remaining coverage/use and stops future charges. The
first-payment window defaults to 14 calendar days; door renewal grace defaults to
7 days. Single visible invoice due dates and original-anniversary interval phases
are also settled. Model fields remain proposals; section 8 lists engineering and
deployment follow-up, not more settled service-policy questions.

The first [system architecture draft](ARCHITECTURE.md) composes these services with
membership, billing, communications, access and storage in one ASP.NET Core modular
monolith. PostgreSQL is the only database target for development, production and
correctness CI; production deployment is containerized. EF Core mappings preserve
the reviewed ownership/constraint design. Quartz.NET persistent background work
must prove shared-transaction scheduling and recovery in the new S0; these are
engineering gates, not an additional service-policy review.

The ordinary path is: **subscribe -> grant initial free days if eligible -> request
payment for uncovered dates -> establish paid coverage -> renew or unsubscribe**.
Use always checks the current dated coverage and membership. Returning after
removal means subscribing again, not reactivating an inactive service.

Recurring-service terminology is **subscription, invoice/payment request, payment
deadline and coverage**. There is no separate "purchase offer" workflow. Storage's
queue offers remain a distinct, concrete allocation step.

## 1. Responsibilities, not separate servers

A service is not a payment counter. Keep these facts separate:

| Domain | Owns |
| --- | --- |
| Membership | Admission, active/grace/inactive standing, resignation and deletion. Basic membership is not an optional service enrolment. |
| Services | What optional services exist, each member's enrolment/authorisation, and when their purchase/renewal is allowed. |
| Billing | Fee prices, shared calendars, invoices, line funding, balances and dated purchased/granted `Coverage`, including basic membership coverage. |
| Access | Resources, credentials, qualifications and the final door/machine authorisation decision. |
| Storage | Positions, queues/offers, rentals, clearance and one-time checkout. Not a recurring-service enrolment merely because storage is a service in ordinary language. |

These are modules in one ASP.NET Core deployment with explicit C# query/command
methods, not microservices, a plugin system or configurable workflow engine.
One recurring service can enable several resources. A qualification-only machine
needs no enrolment, fee or invoice. Training qualifications remain independent of
paid services, and surviving inactivity does not make them financial records.

## 2. Proposed EF Core entities

### ServiceDefinition: what is offered

Representative fields: stable `code`, display name and `self_activation_allowed`
(the single self-subscription checkbox). **Confirmed:** the same setting governs
first subscription and resubscription after removal. If disabled, an administrator
must add the service every time; there is no separate reactivation setting or
payment-triggered resubscription. This association's 24/7 access is administrator-
added. These are not arbitrary permission expressions.

**Confirmed:** replace initial access grace with configurable initial free days;
proposed field `initial_free_days`. Zero grants no initial coverage, but does not
mean immediate subscription removal. Renewal grace is configurable per service,
including zero; proposed field `renewal_grace_days`. This association's door service
defaults to 7 calendar days. The independent first-payment window defaults to
14 calendar days and is configurable association-wide, not per service.

Billing's supporting `FeeDefinition` holds price/base period and accounting
category, with an optional one-to-one relation **from the fee to this service**.
The membership fee has no service definition. Start with one recurring fee per
service; discounts and price changes are not separate service identities. Issued
lines retain their applicable pricing facts. Definitions with history should be
retired rather than cascade-deleted; exact price-history fields remain engineering
work. An unpriced service, if needed, can use enrolment without fake receipts or
coverage. Distinguish that from a priced fee discounted to zero: ordinary 100%
discounts establish dated coverage without money, including honorary membership
with a distant explicit expiry (e.g. approximately 1,000 years). No separate
exemption feature or permanent awaiting-payment state is needed.

### Initial free days: an ordinary Coverage grant

Create dated `Coverage` with an explicit source such as `initial_free_period`,
linked to the subscription/grant action. It creates no receipt, money movement or
fake zero-value payment. Actual grant dates are recorded; later configuration
changes do not rewrite them. Access uses this coverage just like paid coverage,
subject to active membership, the subscription and other resource requirements.

**Confirmed automatic once-only eligibility:** first-time subscribers only, per
member and stable service identity, not per subscription episode. Removal, resubscription,
membership lapse or billing-period changes must not reset eligibility. Preserve
the prior-subscription/initial-grant fact across those operations. A proposed unique
initial-grant key/constraint on account and service also prevents duplicate grants
under retries/races; an episode-only key would be insufficient. Prior subscription
without a grant does not make a returning member a first-time subscriber.

**Confirmed administrator exception:** when adding the service to a returning
subscriber, show an admin-only checkbox, e.g. **Grant configured free days (14)**.
It is unchecked by default for repeat subscribers and uses the service's configured
day count, not a hard-coded 14. Checking it creates an explicit administrator grant;
ordinary members cannot request this exception through self-service or the API.
Record the administrator, subscription/action, configured days and actual dates.
This does not reset automatic first-time eligibility. Proposed source representation:
use `administrator_grant` with an introductory-days reason and a per-command retry
key, leaving the once-only automatic `initial_free_period` constraint intact.
Retries of the same addition must not grant the days twice.

Free days introduce an ongoing service, not a repeat free trial for short-period
returns (the association's "HaaS" pattern). Only explicit admin choice makes the
exception; no minimum term, cooldown or ban on resubscribing is inferred. Final
account deletion actually removes personal records; rejoining uses a fresh verified
application/approval and new account, without restoring subscriptions, qualifications
or fob registrations. Do not retain hidden profiles to recognise returning people.

Billing never charges these granted days later, even if payment arrives immediately.
The initial paid period begins at the later of the first uncovered day and the
line's funding effective date, ending at the request's aligned boundary. For a
new uncovered positive initial/alignment request shorter than a week, combine
it with the following full period, including applicable billing-period-change
bridges. Issue immediately; do not move the shared calendar or invoice covered days.
Proposed execution interpretation: repricing an already-issued request below a
week does not itself append another month; normal boundary rollover still applies.
Crossing a boundary during free coverage skips already-covered days; it neither
shortens the grant nor resets the billing schedule. See the examples below.

### Confirmed payment windows and renewal grace

- **First payment:** 14 calendar days by default from issuing that service's initial
  payment request, including adding its line to an existing open invoice. The older
  invoice's issue date must not shorten the new service's window. At the deadline,
  expire the unfinished line and release its unused funds. Remove the subscription
  once no remaining granted coverage supports it; preserve a longer free grant.
- **Renewal:** an established paid subscription remains usable for its configured
  grace after the paid term ends, subject to active membership/resource gates.
  Door grace defaults to 7 calendar days; zero means no unpaid renewal access.
  Full funding during grace buys the **original renewal period**, including grace
  days, not a newly prorated period starting at receipt. At unpaid grace expiry,
  remove the subscription, expire its unfinished lines and release their unused
  funding. Returning uses normal resubscription.
- Renewal grace never stacks onto an unpaid introductory free period or first-
  payment window. A returning subscriber still awaiting first funding cannot gain
  renewal grace merely because an older subscription was paid. Free coverage end
  and the initial deadline remain separate, even when both defaults are 14 days.
- Timeliness uses the line's qualifying funding effective date, including bank
  receipt rather than import time. A renewal request's recorded grace dates do
  not restart on rollover, retries or membership return. No new grace is invented
  merely because membership inactivity prevented a renewal invoice being issued.
- C1 confirms one public invoice due date: the earliest relevant unfinished-charge
  date, including an earlier initial deadline. Per-line due dates need not be shown.
  Internal windows/grace stay charge-specific and never reset when the invoice is
  revised. A fully discounted eligible period needs no payment/window expiry for
  nonpayment of zero; establish coverage under the ordinary authorisation gates.

### ServiceEnrollment: one subscription episode

This working model name means a subscription, not an inactive-service register.
Representative fields: `member`, `service`, `started_at`, nullable `ended_at`, an
end reason and proposed `renewal_stopped_at`. **Proposed history representation:**
remove the current relationship by ending its episode; resubscription creates a new
one. A PostgreSQL partial unique index, configured through EF Core migrations,
permits at most one unended episode per `(member, service)`.
The member's current service list contains only current subscriptions, not ended
episodes with an "inactive" label. No authorisation/renewal state enums are needed
for nonpayment and return.

Keeping an ended episode for audit is not keeping the service attached to the
member. End reasons explain automatic nonpayment removal, member cancellation or
administrator removal; they are historical causes, not reactivation settings.
Financial history survives regardless of whether an ended episode is later erased
under retention policy. Physical deletion plus separate audit could also represent
removal, but is not needed merely to remove the service from the current list.

Subscribing checks that single self-subscription permission, or administrator
authority. No application/approval inbox is needed. A current subscription with a
valid unfunded first invoice line is **awaiting payment**, not immediately removed
just because no initial free coverage was granted.

The episode ID supplies the purchase-authorisation context. Removal invalidates
that episode's unfinished invoice lines/jobs; a new subscription cannot accidentally
inherit them. Record causes so a mistaken automatic removal can be corrected for
a timely delayed bank import without undoing a later explicit removal. Quote
rollover does not create a new subscription or grant more free days. Paid/granted
coverage is keyed to the service, not the episode: resubscribing reuses retained
covered time, without another automatic initial grant. The admin checkbox is the
explicit exception, not a consequence of creating a new episode.

Membership-only inactivity is a separate use block, not a pause/removal of the
subscription or a reason for reapproval. Its paid time continues to expire normally.
**Confirmed stop-renewal behaviour:** record the request to stop future charges,
cancel this service's unfinished invoice lines and release only their unused funds.
Keep the episode usable for its remaining paid/granted coverage, at the original
dates, subject to the ordinary membership/resource gates. Preserve completed future
periods too; coverage gaps remain gaps. Close the episode when its retained covered
time has ended. Do not create new unpaid grace, an automatic refund or new charges.
`renewal_stopped_at` is proposed renewal intent, not a service-inactive state or
an instruction to revoke coverage immediately. An immediate administrator removal
remains a distinct action. Storage's separate no-early-release rule is unchanged.

### ServiceChange: a small audit and retry record

Record episode, subscribe/stop-renewal/remove/correct action, cause, actor/reason,
effective/processing times and a unique command key. Distinguish automatic nonpayment removal
from an explicit member/administrator removal. This is a normal audit record, not
an event-sourced application or job queue. A command with financial effects links
its change to `BillingOperation`; money still lives only in `MoneyMovement`.

Final member deletion removes personal enrolments under the deletion policy.
It must not cascade into necessary financial facts. Historical line origin links
can become null while retaining required purchase/benefit facts; configuration
definitions are not personal enrolments. Audit retention still needs the existing
privacy review, not an assumption that every old authorisation is kept forever.

## 3. Derived eligibility and what people see

The service-use query combines:

```text
current subscription exists
AND active association membership (including membership grace)
AND (current paid/granted coverage OR (renewal not stopped AND applicable renewal grace))
```

For an unpriced service omit the financial gate. A final resource check additionally
requires a valid credential, an available resource and its required qualifications.
Use explicit resource-to-service/qualification relations, not matching fee names
or embedding access expressions in billing. Door and machine views stay distinct.

Coverage queries respect gaps. Renewal grace uses the relevant issued
line/context, not fake paid coverage or a rolling timer reset by each new invoice.
There is no separate initial-grace access branch: initial free days are coverage.
Live checks evaluate actual dates even if a scheduled removal/notice job is late;
a missing timer run must not prolong access. Reads do not write financial facts.

The initial user-facing states are derived, not separate mutable lifecycle flags:

| Initial funding/coverage | User-facing description |
| --- | --- |
| Valid initial request, not yet funded; current free coverage | Awaiting payment — active |
| Valid initial request, not yet funded; no current coverage | Awaiting payment — inactive |
| Initial line fully funded | Normal active service, with its paid period visible even if it starts after the free period. |
| Eligible fully discounted period | Covered without payment; not awaiting payment. Ordinary membership/subscription/resource gates still apply. |

Membership and resource checks still apply to all of these descriptions. Also show
**usable until date**, **renewal grace until date** where applicable, **membership
inactive**, **renewal stopped — usable until date** and **not subscribed**. Free
coverage ending need not end the initial payment window: deny unfunded use, but keep
it awaiting payment until its separate deadline. A service with no free days gets that window too.
Removal history explains why a subscription ended. A removed admin-only service
says that an administrator must add it, not "inactive, awaiting reactivation".
Show the bill/line and remainder, not a separate service payment counter. Offer
subscribe/resubscribe only where permitted, interval choice and a clearly described
stop action. There is no service application inbox or ordinary-member directory.

Administrators see the same explanation plus grant/change history, paid periods,
open lines/held funds, the returning-subscriber free-days checkbox and explicit
subscribe/stop-renewal/remove/correction actions. Trainers
still see only their authorised qualification lookup, not this financial history.

Membership inactivity blocks use and pauses **new** service billing without
freezing coverage dates or erasing qualifications. Existing requests keep their
ordinary expiry. No subscription pause state, extension, refund or administrator
reapproval is caused by that membership lapse. If membership returns while the
service is still subscribed and covered, use can resume immediately under the
ordinary resource checks. If the service has meanwhile expired and been removed,
normal self/admin resubscription is required instead.

Stopping invoices is **not** freezing the subscription's clock: a paid service
must reach its normal expiry/removal even if no renewal invoice was generated.
Do not grant fresh service grace merely because membership returns or no bill
exists. Apply the confirmed first-payment/renewal rules above, not a new countdown.

Service-use denial is not a portal-wide login ban. Inactive members retain their
own profile and information needed to pay for membership, with convenient access
to the member-services page and existing resignation/privacy routes. Storage,
access-control tools and other members-only areas may be restricted. This neither
promises every tool to inactive members nor bypasses the service-use gate.

## 4. Contract with billing

### Typed owners, stable benefits and purchase context

Refine `RecurringItem` to have exactly one owner: membership or a
`ServiceEnrollment`. Suggested ORM shape: two nullable typed owner relations and
an exactly-one database check constraint, with at most one live instruction per owner.
Commands validate that owner, fee, group and financial account refer to the same
member/benefit. No polymorphic untyped foreign key or owner-name string determines permission.

For example, Alice's door enrolment owns its `RecurringItem`; a quoted door line
refers to that instruction and creates `Coverage` when fulfilled. Both the
enrolment and coverage identify the same stable door `ServiceDefinition`.

The current subscription decides whether a new invoice line may be issued;
`RecurringItem` stores the billing interval/group and instruction dates. Do not
duplicate independently editable subscription flags in both. Removing a service
ends its instruction; an old instruction alone cannot issue another request.

Each `InvoiceLine` needs its purpose (initial, renewal, resubscription or alignment),
stable benefit target and subscription-episode authorisation context. Price/calendar
functions stay in billing; services supply lifecycle facts, not their own proration
engine. Historical origin links must not let deleting a live instruction erase a line.
Newly used billing intervals derive phase from the original joining anniversary;
preserve existing target-group calendars and paid/issued periods. C2's example
switches monthly coverage ending 12 July to quarterly via a bridge to 12 September,
on a 12 March joining anniversary, not a new July/October phase.

`Coverage` needs an explicit target: basic membership or one stable
`ServiceDefinition`, as well as financial account, dates and source. Fee identity
remains a purchase/pricing fact, not the only key for eligibility. Changing price,
group or subscription episode cannot erase already-bought coverage. Absence of a
current subscription still blocks use even when historical coverage exists.

### Purchase authorisation is not live resource authorisation

Use separate queries for **may issue an invoice line**, **may fulfil this line at its
funding effective date**, and **may use this service/resource now**. They share
domain facts, not one universal `IsActive()` method.

The owner validates the line's subscription episode/request and intervening removals
at its funding effective date. Subscribing is the authorisation step, before a new
initial request is issued; there is no separate payment-pending-admin-approval
workflow. A missing machine qualification or disabled resource does not itself
cancel billing, refund coverage or deny every purchase of a shared service.

Membership inactivity prevents new service requests, not receipt ingestion or the
agreed handling of already-issued valid requests. Those retain their authorisation,
funding and ordinary expiry; do not invent a pending-reactivation-approval state or
use membership return as a new funding date. Without a valid request, an incoming
standing order cannot fabricate a service renewal or extend coverage: retain its
money under the normal credit/review rules. Ordinary resubscription is authorised
before its new request is issued. Buying membership itself must not require
already-active paid membership.

`FulfilLineAsync()` creates coverage transactionally but never silently subscribes a
member. Thus money cannot restore an explicitly removed service or rescue a
pending-deletion account. A correction of mistaken automatic removal is separate.
Pending-deletion receipts go to failed-match review even with old recognised
references; administrator restoration need not repair them automatically. Reviewed
future-discount credits and grant-driven reductions reuse billing's correction
commands; zero-price coverage must still respect service/admission/removal gates.

### Duplicate prevention must be purchase-scoped

One **open** invoice per group is not one invoice per group per cycle. After a bill
is paid, adding a service in that same cycle must issue its initial request, without
rebilling membership. Use logical purchase keys including owner/attempt and charge
period/purpose, plus coverage/replacement checks, not only a group/cycle job key.
Refreshing an awaiting-payment request across a boundary preserves its subscription,
funding and original deadline. Expired requests must not reappear on refresh. A new
subscription can produce a genuinely new request, but cannot rebill retained
coverage or reset automatic free-days eligibility. Only the explicit administrator
checkbox can grant another introductory period.

## 5. Commands and lifecycle proposal

Use explicit application command methods spanning these modules. Commands changing
money or its purchase eligibility share billing's EF Core/PostgreSQL transaction and
`BillingAccount` lock, followed by consistent enrolment/line locks. Membership
changes and service revocation use the same coordination, so receipt completion
cannot race permission withdrawal. Constraints and operation keys also protect
duplicate enrolment/coverage. Persist notices/jobs transactionally; provider calls
stay outside money locks. This needs real PostgreSQL race tests; EF tracking or
separate `SaveChangesAsync()` calls do not establish the shared transaction boundary.

| Command/event | Result; detailed representation remains proposed |
| --- | --- |
| Subscribe/resubscribe | Check self/admin authority and membership eligibility. Create an episode/instruction; grant initial days if first-time eligible or an administrator explicitly checks the repeat-subscriber box. Audit the exception and deduplicate the grant. Immediately bill only uncovered dates on the shared schedule; no second open bill in the group. |
| Fund/fulfil | Apply the agreed membership-first line allocation. Complete each fully funded authorised line independently; partial funding buys no amount-proportional days. |
| Initial free coverage ends | If not yet paid, deny use. Keep awaiting payment while the independent first-payment window remains open; no extra free days or initial grace. |
| Own-service nonpayment expiry | At the independent initial deadline or renewal grace end, expire unfinished charges and release unused funds. Remove the subscription when no valid window or remaining coverage supports it. Do not cut short granted/paid time solely because a bill expired, and never add renewal grace to an unpaid first subscription. |
| Membership inactivity | Deny use and stop new service invoices. Keep the subscription and original paid/free coverage clock, with ordinary expiry for existing requests; no pause, reapproval or new free grant. |
| Service expires during membership inactivity | Apply normal paid-service expiry/removal even if no renewal invoice exists. Do not extend coverage, manufacture grace or retain a permanently dormant subscription. |
| Membership returns | If the service remains subscribed with valid coverage/grace, its use is eligible again without an administrator reapproving it. If it expired and was removed, require normal resubscription. No automatic replacement of a removed service. |
| Stop renewal | **Confirmed:** stop new charges and cancel only this service's unfinished purchases/release their holds. Keep remaining paid/granted use to its original end, then close the episode. No new unpaid grace, automatic refund or effect on other paid lines/services. |
| Administrator removes service | End the subscription immediately, stop new quotes and invalidate unfinished invoice lines. Preserve completed financial facts; any refund is separate. A later payment does not add the service again. |
| Membership resignation | Immediately block use and initiate deletion; unusable services may be dropped without automatically refunding/crediting purchased unused time. Retain purchase/period/removal evidence for rare manual handling, and preserve available/held-money claims separately. |

Stopping new quotes is not the same action as cancelling an already-issued request.
A bare hold release would just let automatic billing credit fund that same line
again: cancel/requote the unfinished purchase atomically, without refunding paid
lines or adding an unapproved financial pause mechanism.
Job retries, quote rollover, interval changes and resubscription do not restart
automatic free days. The explicit admin checkbox is a separate grant, not a reset.
The invoice deadline does not extend free coverage; free coverage expiry
does not itself cancel a still-valid initial payment window. No amount is owed for
the granted days or for an unfunded gap after them.

**Delayed imports:** validate timeliness using each line's last required funding
event, not import time or one date for the whole bill. Retain enough quote/change
facts to recognise a valid payment predating an automatic timeout or rollover.
Correct a mistaken automatic removal; never undo an explicit stop-renewal request or removal
or overwrite a newer subscription. Check conditional uniqueness if restoring an
episode; use review if a newer one already exists. This is correction of a timely
purchase, not ordinary payment-triggered resubscription.
If released funds have already bought something else, or intervening changes make
the allocation ambiguous, use the correction/review path rather than silently
rewriting completed purchases. This is a required billing integration case, not
another balance/reservation model or a universal historical replay engine.

## 6. Execution checks

1. **Returning admin-only door subscriber; repeat-grant box unchecked.** Admin adds it;
   billing adds its prorated line. UI says subscribed/awaiting payment and the
   reader denies. Partial funding holds money; full authorised funding creates
   coverage. No reapproval within the valid first-payment window.
2. **EUR 10 membership + EUR 30 door renewal.** EUR 10 renews membership now. Door
   retains only its own coverage/grace. After its nonpayment threshold, remove the
   door subscription and release only unfinished door funds. No later door quotes
   until resubscription; membership is unaffected.
3. **Add a service after that cycle's bill is paid.** Create a new open bill in
   the same group for the added service; do not recreate the paid membership line.
4. **Membership lapses in July; door paid until 1 October.** Deny use and stop new
   service invoices, but keep the subscription and 1 October expiry. Membership
   returning in September allows use of that remaining paid time without admin
   reapproval. If membership instead returns after the service has expired and
   been removed, the admin must add this non-self-subscribable door service again.
   No July-to-October clock freeze or charge for the unsubscribed gap is involved.
5. **Door subscription removed for nonpayment.** No endless invoices or access from
   money sent to an old reference. The self-subscription checkbox is disabled, so
   an administrator must subscribe the member again. For a self-subscribable service,
   the member does that themselves. Billing reuses paid time or quotes the aligned
   uncovered period, without charging for the unsubscribed gap.
6. **Receipt 7 July, grace ends 8 July, import 9 July.** A fully funded timely
   renewal buys its original period, not a new 9 July bridge. Correct the mistaken
   automatic removal. A separate explicit administrator removal remains effective.
   Likewise, a timely initial receipt predating quote rollover uses its actual
   invoice/pricing facts, not merely the quote currently displayed after rollover.
7. **Qualification revoked or machine unavailable.** Deny that resource without
   silently cancelling enrolment, paid time or all other resources using the
   service. Readers use server-resolved decisions; live-first/cache/TTL rules stay
   unchanged. Known eligibility ends bound cached grants, not just cache freshness.
8. **Storage.** Active membership plus offer/position/term and purchase balance
   feed its existing atomic checkout. No recurring instruction, general service
   enrolment, storage-specific reference or speculative money hold is added.
9. **First subscription, 14 free days.** Subscribe on 10 March with a 1 April
   boundary. Grant coverage 10 March–24 March (end exclusive); request payment for
   24 March–1 April. Full funding on 15 March creates paid coverage starting
   24 March, without charging for free days. With the association's payment window
   explicitly configured to 18 days for this example, its deadline is 28 March;
   no payment by 24 March changes the display to
   awaiting payment — inactive, not immediate removal. A qualifying 26 March
   payment buys 26 March–1 April; the two-day gap is not billed. After eventual
   removal, another subscription gets no automatic repeat grant; only an explicit
   administrator checkbox selection grants the configured days again.
10. **Free days cross a boundary.** Subscribe on 25 March; the 14-day grant ends
    8 April. With a monthly boundary on the 1st, request 8 April–1 May, not days
    covered for free or a zero-length March bill. The schedule remains on the 1st.
11. **Administrator grants a returning subscriber free days.** Check the box when
    re-adding the service. Create one audited administrator grant for its configured
    duration, without changing automatic eligibility. Retrying that command creates
    no second grant. Initial pricing excludes those granted dates as usual.
12. **Stop renewal with paid time remaining.** On 10 June stop a service covered
    until 1 July. Cancel only unfinished service charges and return their holds;
    use continues until 1 July under the ordinary gates. Generate no renewal/new
    grace, and close the episode at the retained coverage end. No paid line is refunded.
13. **Default door renewal grace.** Paid coverage ends 1 July; the issued renewal
    covers 1 July–1 August, with 7 days' grace ending 8 July. Qualifying full funding
    on 5 July buys that original full period. Without funding, remove at grace
    expiry and return only unfinished holds. A zero-grace service instead ends
     unpaid use at the paid term's end. Neither rule extends an unpaid first request.
14. **Fully discounted period.** An eligible 100%-discounted charge establishes
    coverage without a receipt or zero-value movement, not awaiting-payment status.
    Removal/pending deletion cannot be bypassed by zero-price processing.
15. **Grant/discount adjustment.** A grant reduces/cancels only now-covered unfinished
    lines and releases surplus. A new discount applies equally to unpaid/prepaid
    future time; completed future purchases get reviewed audited credits, preserving
    original purchase/coverage facts and independent services.
16. **Resignation during a paid term.** Drop now-unusable services without automatic
    unused-time credit/refund. Retain sufficient evidence for manual investigation;
    do not forfeit unspent balances or unused holds, or silently undo resignation
    after a later payment. This is system scope, not a legal refund-rights ruling.

## 7. Billing compatibility verdict

**Compatible with targeted refinements, not a replacement engine.**

| Billing part | Refinement required by services |
| --- | --- |
| `RecurringItem` | Typed membership/enrolment owner; lifecycle permission is not another billing flag. |
| `InvoiceLine` | Purpose, authorisation context and purchase-scoped duplicate prevention, including new services after a paid bill. |
| `Coverage` | Stable membership/service benefit identity independent of fee prices and subscription episodes. |
| Complete/expire commands | Separate purchase permission from live use; coordinate owner lifecycle and distinguish timeout from explicit withdrawal during delayed-import reconciliation. |

Independent line fulfilment is a good fit: differently timed optional charges do
not withhold fully funded membership. Keep existing balances, schedules, stable
group references, journal-only line holds and immutable completed facts. No extra
reservation model, per-service bank reference or financial engine is needed.

Engineering follow-up is primarily transaction/race tests and dated-invoice tests,
not more policy knobs. Storage and qualifications do not need to be remodelled
as paid subscriptions to fit this architecture.

Initial free days need an explicit automatic grant source, first-time eligibility
across episodes and grant-aware pricing; the admin exception uses audited grant
coverage with its own retry key. Neither requires a new model, balance or access
state. The free interval and payment deadline remain
separate even though the association might configure the same number of days.

Nonpayment removal simplifies this contract: end the owner's instruction and
unfinished invoice lines instead of pausing an inactive subscription indefinitely.
Resubscription is authorised by the same checkbox/admin action as first subscription.
Membership-only inactivity needs no service lifecycle transition or approval-held
payment state: stop new requests and deny use, but let existing coverage expire.
The owner must detect expiry even when there is no unpaid renewal line to trigger
it. This needs a dated query/scheduled command, not another billing model.

## 8. Review outcome and remaining follow-up

The service foundation has been reviewed as workable. Stopping renewal preserves
remaining time; administrators can explicitly grant configured introductory days
when adding a returning subscriber. These are confirmed, not outstanding choices.
Model fields, history representation and transaction details remain engineering
proposals, not implemented code.

Payment timing is now settled: a configurable association-wide 14-day first-payment
window, and configurable per-service renewal grace (zero allowed; door default
7 days). Grace renewals buy their original full period; unpaid initial subscriptions
get no added renewal grace. Other service durations are deployment configuration,
not a new lifecycle design question. Short-period merging is now agreed, including
billing-period-change bridges. C1–C2 in [POLICY_REVIEW.md](POLICY_REVIEW.md) confirm
one public earliest invoice due date, preserving internal line timing, and the
original-anniversary phase of newly used intervals.

Member transfers between available balances are now agreed when purchase balance
is enabled; transferred billing credit follows normal automatic allocation.
Future-discount adjustments, grant-driven charge reductions, ordinary 100%
discounts, resignation audit/manual handling and inactive-portal access are settled.
Fresh admission after final deletion is confirmed too, without old personal-record
restoration. Correction implementation, exceptional manual procedures, fob-history/
cached-attribution design and privacy/deployment validation remain in
[OPEN_QUESTIONS.md](OPEN_QUESTIONS.md), not an unanswered policy review set. None
requires reopening this architecture or treating stop renewal as immediate loss of
already-covered time.
