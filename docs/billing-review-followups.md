# Billing review follow-ups

Review of the uncommitted billing changes for post-due grace, missed timely
renewals, service retirement and the prefilled service form. The review was
read-only; it ran no tests or builds. The branch tests already passed 140 fast
and 160 full cases before this review. Findings are ordered by severity.

## Must fix before commit

### Existing retired services still bill

`BillingLifecycle.IssueDueRenewals` skips only `FeeDefinition.Retired`. Services
retired before this change have `ServiceDefinition.Retired = true` but
`FeeDefinition.Retired = false`, so their renewals continue. The new services
table in `/admin/billing-configuration` shows them as “Retired” while billing
continues. Fresh-database tests cannot detect this.

Fix: also skip when the service definition is retired, for example:

```csharp
if (fee.Retired)
    continue;
if (fee.ServiceDefinitionId is { } serviceId &&
    facts.Services.Single(x => x.Id == serviceId).Retired)
    continue;
```

Alternatively, add a one-time migration:

```sql
UPDATE "Fees" SET "Retired" = true
WHERE "ServiceDefinitionId" IN (
    SELECT "Id" FROM "ServiceDefinitions" WHERE "Retired"
);
```

Add a test that simulates a service retired without the fee flag and verifies
that no renewal is issued.

## Should fix

### `service-retired` blocks delayed-receipt recovery

`Expire` records an automatic retirement-related removal as `service-retired`.
`CanRecover` treats every end reason other than `nonpayment` as an explicit
closure. A receipt booked before the expired line’s cutoff can therefore go to
`Review` instead of funding the line after an automatic removal.

Fix: include `service-retired` among the recoverable end reasons, or keep
`nonpayment` as the enrollment end reason and record `service-retired` only in
the service-change audit entry. Add a delayed-receipt-after-retirement test.

### Service enrollment already ended by the first post-due scan

A zero-grace service enrollment that the first post-due scan has ended is not
reopened by a later-imported on-time receipt. `IssueDueRenewals` only sees open
items and enrollments, and the expired service line has already been closed.
Membership does not have this problem because membership items are not ended by
`EndService`.

Fix: add a service re-open path that accepts an enrollment ended by an automatic
removal when `receipt.ReceivedOn` is before the renewal cutoff, then reopens the
item/enrollment the same way `Recover` does. Reordering the helper before
`Expire` is not sufficient because the trailing `Expire` would close the
unfunded new line.

### Receipts dated before the due date cannot rescue a missed renewal

The helper is gated by `through = receipt.ReceivedOn` and by
`renewalExpiresOn <= on`. If the worker is down across the due date and the
payment is dated before it, the receipt becomes credit and the later scan stops
catch-up. Closing this requires issuing and allocating against dated available
credit, not only the incoming receipt. This is narrower than the due-date case
and needs a scope decision.

## Nice to have

- `Match` uses `Finish(notify: false)`. If the helper creates and fully funds a
  renewal invoice, the member may receive no invoice email for that revision.
- Member forecasts still include items whose service fee is retired.
- `SetPrice` still accepts a retired fee while `AddDiscount` rejects it.
- `Expire` uses `facts.Fees.Single(...)` for the service end reason.
  `SingleOrDefault(...)?.Retired == true` is safer if a service ever lacks a fee.
- The retirement `<select>` is a plain select. The house pattern is
  `InputSelect` with explicit `selected` options; use it and add a POST test.
- `?service=abc` can throw during render; bind a string and parse if this matters.
- The SPEC grace-end row says the notice and inactivity happen the following
  day; the code sends both when the line expires. Reword or move the notice.

## Test gaps

- No POST through the new service form: retirement selection, read-only ID/code
  and “editing must not create a duplicate service” are unverified.
- The prefill test uses position-blind `value="7"`-style assertions; assert named
  fields and the prefilled ID.
- No service version of the missed-renewal test.
- No service within-grace delayed-import test.
- No fee-retirement/`CanRecover` interaction test.
- `One_day_renewal_grace` should assert `ExpiresOn` directly.
- `Retiring_a_service` does not prove that a renewal would otherwise have been
  issued.

## What looked correct

- The Scan helper refactor is behaviour-preserving; `Scan` still passes
  `clock.Today`, no group filter and no receipt cutoff.
- Helper placement after `Expire`, before resumption and funding, is order-safe.
- Group filtering, retired-fee checks, enrollment guards, membership-active
  checks and grace expiry checks mirror `Scan`.
- Duplicate receipts cannot double-issue because of the purchase key and the
  account lock.
- Resumption ordering is correct: a helper-created membership line prevents an
  extra resumption.
- B arithmetic is consistent across creation, standing, service grace, scan
  catch-up and the tests. Initial membership, resumption, service initial and
  one-off windows are unchanged.
- Fresh service retirement is symmetric, preserves paid coverage and keeps the
  membership fee untouched.
- Prefill wiring and admin authorization are correct.
- README counts match the added tests.

## Recommended order

1. Fix the retirement backfill or runtime service-retired skip.
2. Fix `service-retired` in `CanRecover` and add the test.
3. Decide whether to implement the service re-open and pre-due receipt paths.
4. Apply the small forecast, invoice-mail, `SetPrice` and `<select>` cleanups.
5. Commit the corrected changes; keep the uncommitted review changes together
   until then.
