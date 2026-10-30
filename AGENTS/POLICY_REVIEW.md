# Policy review: confirmed outcomes and examples

The review answers below are **confirmed**, including fresh admission after final
deletion and fob-ownership history for cached-event attribution. Dates and amounts
are illustrative.
[SPEC.md](SPEC.md) remains the detailed behaviour reference. Model representation
and execution interpretations labelled as proposals remain engineering work.

## 1. Billing calendars

### C1. Merging must not hide an earlier first-payment deadline

Confirmed: merge a positive paid bridge of fewer than seven calendar days with the
following full period, including bridges from billing-period changes. Issue it
immediately, keep the shared schedule, exclude free-covered days, and retain
receipt-date pricing and independent line fulfilment.

**Example without a deadline conflict:** a EUR 30/month service starts 28 March,
with no free grant and a boundary on the 1st. Request EUR 3.87 for 28 March–1 April
plus EUR 30 for April: EUR 33.87 together, not two successive bills. Its default
first-payment window ends 11 April. The following period's normal due date can be
1 April; the initial request still has its separately stated 14-day payment window.
If EUR 33.87 reaches the bank on 3 April while the request remains valid, initial
receipt-date repricing buys 3 April–1 May for EUR 28 and leaves EUR 5.87 billing
credit, assuming no other requests consume it. No new bank receipt is invented.

**Earlier-deadline example:** subscribe on 14 March with 14 free days ending 28 March.
The paid bridge is again 28 March–1 April, merged with April. But the initial
request issued 14 March has a 28 March deadline, earlier than April's normal due
date. Showing only "due 1 April" would invite payment after the request expires.

**Confirmed:** retain the 14-day window and show **one invoice due date**, the
earliest relevant date among its unfinished charges: **28 March**, not 1 April in
this example. No per-line due dates need to be presented. A brief explanation on
the invoice can follow later. Internal first-payment/renewal-grace/expiry facts
remain charge-specific; combining or revising the bill must not reset them or
shorten another service's window. The visible due date is not a shared expiry timer.

The line layout is engineering work: an extended line with dated pricing segments
is a reasonable proposal. It must not reintroduce whole-invoice fulfilment or keep
adding another full period every time an awaiting-payment bill is refreshed.
Proposed execution interpretation: assess the short bridge when preparing a new
request. An existing eight-day request becoming six days through receipt-date
repricing should not suddenly require an additional month's payment. This retains
the previously agreed delayed-payment examples and is distinct from ordinary
rollover when an unfunded period has entirely elapsed.

### C2. A newly used interval needs a calendar phase

Suppose membership began on 12 March and is paid monthly through 12 July. The
member selects quarterly billing for the first time.

**Confirmed:** derive the quarterly phase from the original joining anniversary:
boundaries are 12 March, June, September and December. Bridge 12 July–12 September,
then bill quarters; do not start a new July/October phase at the change. Existing
target groups retain their established phase, and paid/issued periods are not
rebilled. The accepted short-bridge merge applies where a bridge is short.

## 2. Confirmed transfers: what “available” means

When purchase balance is enabled, members can explicitly transfer in either
direction. No admin approval, automatic sweep or new bank transaction is involved.

- EUR 8 available billing credit and EUR 20 purchase balance: moving EUR 5 to
  purchase balance leaves EUR 3 and EUR 25. Total member advances are unchanged.
- EUR 50 purchase balance, no billing credit, and open EUR 15 membership plus
  EUR 10 door lines: moving EUR 20 into billing credit triggers normal allocation.
  Membership fulfils for EUR 15; EUR 5 is held on the unfinished door line. Purchase
  balance is EUR 30 and available billing credit is zero. The EUR 20 cannot simply
  be transferred back: it is now spent/held, not an available balance.

This does not introduce a member unallocation/refund tool. Cancelling an unfinished
service request can release its unused funding under the already-agreed rules.

## 3. Pricing, adjustments and refunds

### P1. New discounts: unpaid versus already-paid future time

On 20 June an administrator grants a 50% discount effective 1 July. July door
access normally costs EUR 30.

- **July line unpaid:** reprice it to EUR 15. If EUR 10 is already held, it needs
  only EUR 5 more. Its original deadlines remain.
- **July already paid upfront:** credit EUR 15 for the same unused future period,
  using the administrator-reviewed adjustment below.

**Confirmed:** equal treatment for unused future time from the discount's
explicit effective date, not automatic refunds for already-used time. Reprice
unfinished lines; use an audited correction/credit for completed future purchases
without rewriting their original payment/coverage facts. Initially, require an
administrator to review/confirm the paid-period correction rather than build a
general retrospective repricing engine. No automatic bank refund is implied.

Discounts still apply only until their explicit expiry, splitting a billed period
where necessary. This is an explicit adjustment path, not routine repricing of
completed purchases or an automatic bank refund.

### P2. Coverage grants overlapping an outstanding request

Membership costs EUR 10/month. The July membership line is unpaid with EUR 5 held;
an administrator extends the previous paid-through date from 1 July to 1 August.

**Confirmed:** omit/cancel the now-covered unfinished membership charge and
release its EUR 5 to billing credit. July is covered by the grant, not a fabricated
payment. Keep the calendar and any independent door line/deadline intact. Ordinary
credit allocation may then use the released money for another valid line.

Partly overlapping grants reduce the unfinished charge to uncovered days, returning
any surplus funding. Whole-month membership extensions remain the administrative
tool; this does not add arbitrary-day membership extensions or reprice paid services.

### P3. Honorary membership uses an ordinary 100% discount

**Confirmed:** apply an ordinary 100% membership discount with an explicit, very
distant expiry, for example approximately 1,000 years. The existing discount rules
support this; no separate exemption feature or indefinite mode is needed.

Consequence: establish each eligible fully discounted covered period without a
payment, fictitious receipt or zero-value money movement. Do not leave it awaiting
payment or make it inactive for failing to pay zero. Discount expiry restores
normal dated pricing. Admission, subscription/removal and pending-deletion gates
still apply. The precise zero-price representation is engineering work.

### P4. Leaving the association versus merely stopping renewal

A member pays EUR 90 for April–June door access, then resigns on 1 June.

- Merely stopping door renewal is settled: keep the paid term's remaining use.
- Resignation is different: membership/access end immediately and deletion starts.
  Basic membership fees are non-refundable.

**Confirmed system scope:** resignation may simply drop the now-unusable services,
without automatically refunding or crediting unused purchased time. Keep enough
purchase, payment, covered-period and removal audit facts for later manual
investigation and rare manual handling. No extensive refund-entitlement or
valuation engine is required. This is not a legal determination that refund rights
are waived, nor a new rule for every association-initiated withdrawal.

Unused purchased service time is not unspent money. Available balances and unused
holds retain their separate financial claim; deletion cannot silently forfeit them.

Mistaken payment assignments and reversals use the same traceable correction
discipline. For example, a reversed EUR 50 deposit after EUR 40 was spent cannot
silently make the remaining EUR 10 balance negative or revoke purchases/rentals.
**Engineering follow-up:** treasurer review and explicit correction for such
exceptional reversals; do not fabricate collectible fee debt or an automatic clawback.

## 4. Account lifecycle exceptions

### A1. Deletion waiting time and administrator restoration

Someone resigns on 1 June and asks to undo it on 8 June. Login/access have already
been disabled; payment alone cannot cancel deletion. Those parts are confirmed.

**Confirmed:** waiting time is a configuration-file option, default 14 calendar
days. A deletion batch runs every 14 days and deletes only accounts whose waiting
time has elapsed: normally actual deletion is 14–28 days after marking, on the next
successful eligible batch. A delayed/failed job may take longer; it must never
delete early. Waiting time and job cadence are separate from payment windows.

An administrator may cancel pending deletion on a verified request before actual
final deletion, including after eligibility but before the batch runs. Re-evaluate
surviving membership and service coverage, rather than restoring expired time or
separately revoked access.
Final deletion still proceeds despite outstanding credit; keep the required
financial claim separately, without retaining a usable member profile.

### A2. Payments while deletion is pending; rejoining afterwards

A standing order sends EUR 40 on 5 June while deletion is pending.

**Confirmed:** show the EUR 40 to the treasurer as a **failed-to-match payment**,
even if its reference previously identified this member. Preserve the source
receipt and money; do not allocate it, buy coverage, reactivate the person or delay
deletion automatically. If an administrator cancels deletion, manual payment repair
is acceptable. Restoration or repeat imports must not silently rematch the receipt.

**Confirmed:** after final deletion, rejoining requires a fresh application, email
verification and administrator approval, creating a new account/member identity.
Final deletion actually removes the account and personal records, not a soft-delete
flag or a restorable archive. Do not keep a shadow profile to recognise returners,
or restore qualifications, fob registrations or subscriptions. Any surviving
financial claim is handled explicitly by the treasurer, not silently merged by email.
Only separately justified minimal financial/audit data may remain under their
retention rules; rejoining is not a reason to preserve the deleted personal data.

### A3. Extra portal restrictions during ordinary membership inactivity

An inactive member logs in to pay, update their email and see when their still-paid
door/storage term ends. This is not a pending-deletion account.

**Confirmed:** preserve the member's own profile and payment information needed to
pay for membership, with convenient access to the member-services page. The existing
resignation/privacy routes remain available. Storage, access-control tools and
other members-only areas may be restricted. No inactive-login ban or all-areas
access guarantee is implied; paid door time does not bypass inactive membership.

## 5. Credential ownership

### F1. Reusing a previously assigned fob

Alice revokes a lost fob; Bob later finds it and scans it.

**Confirmed:** revocation removes its current registration and frees the UID for
ordinary scan-and-claim/reuse. Bob needs no administrator reassignment and receives
only his own permissions. Do not keep a revoked registration that blocks reuse.

Historical events primarily identify the original user/member; fob UID is supporting
context. Reuse must never turn Alice's history into Bob's, including delayed offline
uploads. A still-owned fob denied for inactivity or another eligibility reason is
not released or claimable. Live checks and offline-cache staleness rules remain.

**Confirmed architectural requirement:** keep minimal ownership history per fob so
cached requests can be traced back through revocation/reuse. This history does not
keep a registration active, block UID reuse or preserve a deleted account/profile.
Its identifying links remain subject to justified retention/erasure, not an assumed
permanent record of former members.

**Engineering proposal:** a cached permission/offline report carries an opaque
assignment/grant version, resolved against ownership history. If Alice's old cached
grant is used after Bob claims the UID, preserve the original grant's attribution;
UID plus event time alone could incorrectly select Bob. Do not infer who physically
presented the fob, overwrite the reported decision or change live-first/TTL rules.
