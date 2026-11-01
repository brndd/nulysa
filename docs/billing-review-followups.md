# Billing review follow-ups

Status after the implementation pass. The corrections and small cleanups below
are implemented; 4B remains a scope decision. The standard test suite passes
with 150 cases.

## Decisions

- **Existing retired services (the old "must fix").** Won't fix. Retirement
  before the retirement change set only `ServiceDefinition.Retired`; renewal
  issuance checked only `FeeDefinition.Retired`. Fresh services set both flags.
  There is no production data and development databases are disposable, so no
  migration, backfill or runtime service-retired skip was added.
- **`Billing:InvoiceLeadDays` minimum is 7.** Enforced in `FoundationHost` and
  documented in `docs/billing.md`. Renewal invoices must have at least a week of
  advance notice. A "pay immediately" demand/late notice is a different invoice
  type, not a zero-lead renewal. This widens the issuance window but does not
  replace the corrections below.
- **Invoice email for a helper-created renewal.** Not changed. A renewal that is
  fully funded in `Match` closes the invoice, so `Finish(false)` does not send a
  payment request. Sending an invoice notice after payment would be a separate
  paid-invoice/receipt mail type, not a flag flip.

## Implemented fixes

### Service enrollment re-open (#3)

`Match` now reopens a service enrollment that an automatic expiry ended when a
receipt dated before the renewal cutoff arrives later. It runs after
`Expire(preserved...)` and before `IssueDueRenewals`, so it covers both a prior
worker scan and `Match`'s own expiry pass. The existing `Recover` reopening was
factored into `ReopenService` so the line and no-line paths share it.

The reopen predicate requires:

- automatic nonpayment ending (`RenewalStoppedAt == null`, `FirstPaidOn != null`);
- no newer enrollment for the same service (the unique active-enrollment index
  would otherwise reject the reopen);
- an item in the matched billing group;
- no expired or cancelled line for the enrollment; existing lines are left to
  `Recover`/`CanRecover`;
- the service and fee are not retired (a retired service gets no new line);
- a timely receipt according to `ServiceGraceEnd`;
- active membership on the receipt date.

Tests:

- `Pre_due_receipt_after_worker_outage_reopens_service_and_membership`
- `Timely_delayed_service_renewal_is_recovered_after_expiry`
- `Retiring_a_service_keeps_an_issued_renewal_recoverable_by_a_timely_receipt`
- `Partial_timely_receipt_reopens_a_service_and_a_second_receipt_completes_it`
- `A_late_receipt_does_not_reopen_an_old_enrollment_after_resubscription`

### `service-retired` recovery (#2)

`CanRecover` now treats `nonpayment` and `service-retired` as automatic end
reasons through one `IsAutomaticEndReason` predicate. A timely receipt for an
already-issued retired-service renewal funds the line instead of going to
`Review`. The retirement test also covers the forecast and `SetPrice` changes.

Open policy question, unchanged: the current code leaves an already-issued
unpaid renewal line payable after retirement. If retirement should cancel it,
close the line at retirement and document that rule.

### Pre-due incoming receipt (4A)

The `through` catch-up limit was removed from `IssueDueRenewals`. The existing
`nextStart > on + InvoiceLeadDays` bound still prevents issuing renewals too far
ahead, but a receipt dated before the due date can now settle the upcoming
renewal exactly as a worker running on the receipt date would. Tests cover a
pre-due membership and service receipt imported after the grace window.

### Membership notice without a renewal line

When a scan observes an inactive membership with no open membership line,
`Expire` now queues one `MembershipInactive` notice. It uses a deterministic
delivery key based on the account and `InactiveSince`, so repeated scans do not
duplicate it. The existing line-expiry notice still wins when a line exists.
Test: `Missed_membership_renewal_without_an_issued_line_sends_one_inactive_notice`.

### Small cleanups

- Member forecasts skip retired fees.
- `SetPrice` rejects a missing or retired fee, matching `AddDiscount`.
- `Expire` uses `SingleOrDefault` for the service fee; a missing fee no longer
  blocks an account scan.
- The retirement control is an `InputSelect` with explicit `selected` options.
  SSR does not derive the selected option from the select's value, so the
  explicit options are required.
- The prefill test asserts named fields, the prefilled ID, read-only ID/code and
  the selected option.
- A POST test edits a service with the retirement control and proves no
  duplicate service is created.
- `?service=abc` binds as a string and is ignored when it cannot be parsed.
- The SPEC grace-end row now states that the inactive notice and inactivity
  happen on the first non-grace day.
- `One_day_renewal_grace` asserts `ExpiresOn` directly.

## Remaining scope decision

### 4B. Receipt was already billing credit when the renewal was missed

A receipt imported more than `InvoiceLeadDays` before the due date becomes
credit instead of funding a line. If no worker scan or other group match occurs
from `due - InvoiceLeadDays` through expiry, the credit cannot rescue the missed
renewal. With defaults this is roughly a 22-day complete outage plus an early
payment. The money is not lost; it remains billing credit and can fund a return
period, resubscription or manual correction.

A full fix is not a date lookup:

- `BillingCredit` is a single pool. The `ReceiptToBilling` movement keeps the
  receipt date, but `AllocateCredit` does not identify credit lots and applies
  credit with the application date.
- SPEC 612-614 says previously available billing credit uses its application
  effective date, not the original deposit date. Using the receipt date to
  fund a missed renewal would change that policy.
- If the credit was spent on another line after the expiry, retroactive
  allocation would require reallocation/reversal. That is a ledger policy.
- Transfers, refunds, reversals and repricing credits all affect the pool.

Options:

1. **Accept and document.** Recommended unless very early payments plus long
   outages are a real requirement.
2. **Pending receipt/reservation.** Hold an early group receipt for that group's
   next renewal instead of general credit. New state, UI and refund/unassign
   rules; cleanest product fix if needed.
3. **Dated credit lots.** Track credit sources and allocate by date. General but
   a significant ledger change.
4. **Auto-return from credit on scan.** Start a prorated return from available
   credit after inactivity. Changes reactivation semantics; not recommended
   implicitly.
5. **Issue the renewal immediately on an early receipt.** Avoids credit but
   creates far-future invoices and turns early payment into prepayment.

## Remaining test gap

- `Retiring_a_service` could include a control proving a renewal would otherwise
  have been issued. The normal service-renewal tests already cover issuance, so
  this is a test-strength improvement rather than an untested behaviour.

## Operational notes

- `billing-due-work` runs every minute, and `WorkStatus` treats it as stale
  after 3 minutes. A 15-22 day complete worker outage is an operational
  incident, not a routine bank-import delay. Ensure stale-work alerts reach an
  administrator, not only the system console.
- The due-work scan and bank sync are separate schedules. After an outage the
  due-work scan may run before the bank sync imports an on-time receipt. The
  corrections make the result order-independent; consider running bank sync
  before due work on recovery as an additional mitigation.
- A late bank import with a running worker is handled by `Recover` when the
  renewal line exists. The no-line cases require the worker to miss the invoice
  preparation window through expiry.

## Architecture note

The current design is reconciliation-based, not a command queue. Missed ticks
are not lost work; the next scan recomputes from dated state. That is generally
more robust than replaying a backlog, and it already catches up within grace.

The shared root cause of the no-line cases is that some lifecycle transitions
are destructive and based on `clock.Today`, while late facts can have earlier
effective dates. `Recover` and the reopen path are correction mechanisms for
that hybrid model.

A queue of due-work commands would not fix this by itself; the expiry command
would still run before a late receipt is known. A milestone queue with as-of
replay could work if all facts are present, but it still needs corrections for
later imports and adds admin-action timestamps, as-of pricing and idempotent
notice keys. Full event sourcing/bitemporal state would be the general answer
but is a large project for this scale.

Recommended direction: keep the targeted corrections and add a bounded as-of
replay layer only if this class of finding keeps recurring.
