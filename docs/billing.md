# S2 billing and subscriptions

S2 extends the S0/S1 application, not a second billing system. Approval now creates
the actual initial membership invoice and queues its PDF alongside welcome mail.
Standing derives from dated coverage and per-charge grace, not an unpaid-invoice
flag. Requests expire without debt. Coverage periods are start-inclusive/end-exclusive.
Payment due dates are inclusive: a receipt booked on the due date can fund the
original request, including initial membership, returns, services and one-offs.
Code uses `EndExclusive` for period ends and `PaymentCutoffExclusive` for the first
day funding is too late. Portal/PDF periods show the last included day; payment
deadlines say “Pay by.” Discount forms accept an inclusive “Through” date.
Expiry is an exclusive cutoff, no earlier than the following day; renewal grace
days are counted after the due date. Existing open requests keep the expiry they
were issued with; newly issued renewals use the post-due grace. Overdue notices
begin after the due date.

## Local walkthrough

Install the pinned renderer once (requires curl, tar, xz and sha256sum), and put it
on the web **and** worker PATH:

```sh
bash scripts/install-typst.sh "$HOME/.local/bin"
export PATH="$HOME/.local/bin:$PATH"
bash scripts/dev.sh
```

The launcher supplies a **synthetic** IBAN, never a deployment payment destination.
The explicit `create-development-user` command seeds a synthetic EUR 10 monthly
membership price only if no price exists (`DevelopmentUser__MembershipMonthlyPrice`
overrides it). Neither migrations nor ordinary startup seed prices. For a fresh
deployment, set the membership price under `/admin/settings/fees` **before
approval**; a missing price rolls back approval, including its mail intents.

1. Open `/services` as a member for standing, coverage, forecasts, balances and
   billing settings. Open `/billing` for outstanding bills, PDFs, line funding and
   bill/transaction history. Forecasts are not issued invoices. Invoice email can be
   disabled without hiding bills or suppressing mandatory membership notices.
2. Configure a service at `/admin/settings/services`, then follow its fee-prices link
   to set dated monthly prices under `/admin/settings/fees`.
   Set self-subscription, initial free days, renewal grace (zero allowed), and funding
   priority. Membership always funds first. No service is seeded by migrations or
   startup; the optional `seed-development-data` command creates a synthetic Door
   access service (14 free days, seven renewal-grace days, no self-subscription) and
   a self-subscribable Trailer access service for local testing.
3. Use the Account overview at `/admin/members/{user-id}` to add an admin-only service. The unchecked
   free-days exception is an explicit, retry-safe grant for a returning subscriber.
   Stopping renewal preserves purchased/granted time; immediate removal is separate.
   Retirement hides the service, prevents new subscriptions, and stops future renewal
   charges for the service's existing subscribers; already-paid coverage remains and
   existing subscriptions stay visible with stop-renewal/member and
   immediate-removal/administrator controls. The membership fee itself is not retired.
4. At `/admin/payments/import`, manually import a **synthetic** confirmed bank transaction
   with its stable source/account/transaction ID and actual booked date. Use the
   invoice's recurring-group reference, not the purchase-deposit reference. Partial
   funding stays held; each fully funded line completes independently. Reimports
   cannot duplicate cash or overwrite source facts.
5. Unknown references go to `/admin/payments`. Open a transaction, inspect its source
   facts, and explicitly choose a purpose and member. Select any relevant group or
   one-off bill by its label, then review and confirm. Names and amounts never guess
   ownership. Ignoring retains the source fact and survives reimport.
6. `/admin/members/{user-id}/actions` offers one-off requests, reasoned discounts,
   whole-month grants, reviewed future-discount credits, cancellations,
   unspent-receipt reversals and completed external refunds. Each named task has a
   review/confirmation step. `/admin/payments/retained-balances` handles erased
   accounts' financial evidence and permitted refund/reversal tasks. Reconciliation
   and CSV exports are under `/admin/payments/accounting`.

Recurring references are stable per member/interval; one-off requests have separate
references. Changing an interval affects the next **unissued** period, preserving
existing quotes, funding, deadlines and references. All groups keep the original
joining-anniversary phase. Short initial/alignment bridges merge once with the next
period; month-end days do not drift. Receipt-date repricing does not run daily merely
to make partial funds sufficient.

## Deployment settings

Use the same configuration in web, worker and maintenance processes. Environment
variables use `__` in place of `:`. Timing is in association-local calendar days;
`Association:TimeZone` defaults to `Europe/Helsinki`.

| Setting | Default / purpose |
| --- | --- |
| `Billing:Iban` | Required, checksum-valid destination in Production; set it before issuing development invoices too. |
| `Billing:Bic`, `Billing:Bank`, `Billing:BusinessId`, `Billing:ContactEmail`, `Billing:Address:0`… | Payee information printed on invoices; association name uses `Association:Name`. |
| `Billing:DefaultIntervalMonths` | 1; initial membership period. |
| `Billing:AllowedIntervals:0`… | Integer months 1–12 by default; allowed values 1–120. |
| `Billing:InvoiceLeadDays` | 14; minimum 7 days of advance notice for renewal invoices. |
| `Billing:InitialMembershipGraceDays` | 14, no additional renewal grace on initial nonpayment. |
| `Billing:MembershipRenewalGraceDays` | 7 days after the due date, zero allowed. |
| `Billing:ServicePaymentWindowDays` | 14; independent from initial free coverage. |
| `Billing:AutomaticMembershipReactivation` | true; otherwise administrator approval of a return request is required. |
| `Billing:PurchaseBalanceEnabled` | false; enables deliberate deposits and member transfers between available balances. |
| `Billing:TypstPath` | `typst`; must report version **0.15.0**. |
| `Billing:TemplateDirectory` | Optional absolute path to trusted, self-contained invoice assets. |

Purchase balance is not automatically swept into recurring bills. Transfers into
billing credit immediately run the recurring allocator. Held/spent funds are not
available for transfers or refunds. Disabling purchase balance after real use needs
an operator reconciliation plan; it does not discard existing funds.

### Invoice template contract

The generic `Billing/Templates/lasku.typ` and `pankkiviivakoodi.typ` adapt
`hacklab-document-templates/parametric/lasku/` without Tampere Hacklab branding.
The bank barcode is self-contained Code 128, without remote packages or barcode
fonts. Finnish and English labels use the member's language.

A deployment can mount a private, read-only template directory containing
`lasku.typ`, helpers and assets, then set `Billing:TemplateDirectory`. It is **trusted
operator code**, not a member upload or a sandbox for untrusted templates. Files
are copied into a private temporary root; symlink files are rejected. Typst gets
`--input invoice=invoice.json`, a restricted root, no system fonts, private package
directories and disabled package-download proxies. Use embedded fonts or vendor
private assets; do not depend on remote packages. Compilation has a 20-second
deadline and kills the child process on cancellation. Temporary PDFs/data are deleted.

JSON fields: `number`, association-local `date`, `due_date`, `language`, `reference`;
`sender` (`name`, `address` array, `business_id`, `email`, `iban`, `bic`, `bank`);
`recipient` (`name`, `address` array); `items` (`description`, `period` with inclusive displayed dates or a one-off payment deadline, `state`,
`amount_cents`); and `paid_cents`, `held_cents`, `remaining_cents`. Values are data,
never interpolated/evaluated Typst source. Read them with
`json(sys.inputs.at("invoice"))`. Preserve the payable/reference semantics in custom
templates. Queued mail targets a revision and cancels stale work; PDF generation
never prices purchases or spends funds. No PDF-version archive is maintained.

### GoCardless bank feed

Set `Bank:Enabled=true`, private `Bank:SecretId`, `Bank:SecretKey` and
`Bank:Accounts:0`… to the provider's account UUIDs. Obtain/renew bank consent outside
this portal; there is no bank-onboarding UI. Account IDs are not IBANs.

`bank-sync` runs at association-local midnight. Only booked entries are ingested;
`bookingDate` supplies the receipt date. Stable transaction IDs, messages and
counterparty details (including outgoing recipients) are retained. Pending entries
are not spendable. A conflict/malformed booked entry fails visibly, rather than
silently altering the old transaction. HTTP calls are bounded and outside member locks.

`/admin/payments` shows attempts, last success and authorization/failure status.
`Bank:StaleHours` defaults to 26 (allowed 24–168). Minute-level due work detects
staleness and queues administrator alerts, throttled to one alert per account/day.
An independent worker/status monitor is still essential: a stopped worker cannot
email about its own outage. Do not put provider secrets/payloads in logs.

**Agreed provider milestone:** bank transfers and GoCardless ingestion are the S2
provider implementation. MobilePay's authenticated adapter, duplicate-callback and
payout reconciliation tests, and sandbox acceptance remain mandatory **before S6**.
MobilePay is not supported by this release; browser returns are never payment proof.

## Corrections, reconciliation and erasure

- Source receipts are immutable. Eligible unspent allocations reverse with linked
  journal facts into review, then can be explicitly reassigned. Completed purchases
  or unavailable funds are rejected for treasurer investigation: no negative balance,
  automatic entitlement clawback or fictional fee debt is created.
- Discounts reprice unfinished future time using its retained quote basis. Completed
  future purchases instead need the calculated, administrator-reviewed credit. Whole
  membership months extend coverage and release now-unneeded holds, without fake cash.
- Refund recording does **not** send money. First complete and verify the external
  bank refund, then record its amount/purpose/reference with a reason.
- The payments page reconciles lifetime journal totals in one PostgreSQL snapshot.
  The difference and invalid line/receipt lists must be zero/empty. It reports,
  never silently repairs. Export date-bounded journal CSV and bank-transactions CSV
  separately; the latter includes review/ignored/outgoing/non-EUR entries. Dates use
  effective/booked time, not import time. Formula-like source strings are escaped.
  These are source/reconciliation exports, not a general ledger or an accountant's
  approved account mapping/period-close report; validate those at S6.
- Timely delayed receipts can repair automatic expiry using the original dated
  request. Conflicts with explicit removal, newer subscriptions or reallocated funds
  stay in manual review; this is not a universal historical replay engine.
- Pending deletion blocks purchases immediately and releases unfinished holds.
  Receipts arriving then remain in review even after restoration/reimport. Erasure
  removes personal subscription/instruction records, retains financial history and
  only minimal name/email for an unspent claim. Claims appear in payments administration;
  refunding the last available funds clears those contact fields. Rejoining never
  matches an old claim by email. Retention/lawful bases still need deployment review.
- Billing credit is held in dated receipt lots and consumed oldest first. A missed
  renewal can be funded retroactively only from a lot received before its cutoff;
  transfers and other corrections use ordinary application-date credit. Per-balance
  statements show each movement and running balance over the journal.

The billing model is reconciliation-based, not a command queue: a missed scan is
recomputed from dated state on the next run, with `Recover` and service re-open as
the correction paths for late facts. A command queue or event-sourced rewrite is
not planned at this scale.

## Acceptance evidence

`BillingCalendarTests`, `BillingTests`, `BankFeedTests`, configuration/schema tests,
and the full-only `BillingBrowserTests` cover the billing acceptance groups. The
browser journey exercises actual SSR forms for service/price configuration,
subscription, invoice preference, partial and unmatched bank payments, explicit
assignment (including one-off invoices), deposits/transfers, stop renewal, interval
selection, one-off PDFs and administrator route denial. Typst is exercised, not
mocked, including a custom template with an association-local date assertion.

Review regressions cover receipts predating return quotes, adjusted pricing across
rollover/free coverage, renewal billing after restoration, date-scoped discount
credits and reviewed one-off payments, including ownership and retry checks.
Follow-up regressions also run maintenance after backdated settlement, preserve
the selected renewal interval, exercise payment-triggered return after expired
coverage restoration, and price unquoted days through local-date discount withdrawals.
Browser retries for blank/nonexistent financial accounts retain the intended
invoice, purpose and operation key without moving money on validation errors.

The S2 Release suites passed with no skips, including Chromium/Keycloak and real
workers. Run CI for current counts and timings. Release compilation, formatting,
migration-model consistency, launcher/script checks and the production-mode
container smoke (including pinned Typst) also passed.

See the README for test commands. Provider tests use synthetic
HTTP fixtures, not live GoCardless credentials. Live bank/SMTP onboarding, MobilePay,
accountant sign-off, retention and production cutover are not certified here.
