# Performance follow-ups

These are measurement tasks from the S0–S2 audit, not confirmed failures.
Neither audit ran benchmarks, query plans or load tests. Measure before
redesigning. The assumed load is one association with a few hundred members,
one web process and one worker.

## Billing due-work scan

`BillingDueWorkJob` visits every live billing account every minute and loads
that account's full journal before deciding there is no work. The scan also
creates and removes a `due-work` operation on an empty account. Per-account
failures are isolated, but an aggregate failure still fails the job run.

Measure: wall time and database time for one run at 200, 500 and 1,000 accounts,
with realistic years of movements per account. Check lock waits and the cost of
the empty-scan operation. Do not add a single-condition due filter: a safe
filter must also cover pending initial requests, open lines at `ChargeDueOn`
and `ExpiresOn`, grace notices and service grace ends. If the measured run no
longer fits its minute, either add the complete work query or reduce the
schedule frequency.

## Member registry

`MembershipQueries.Registry` evaluates standing across all matching candidates
before filtering, sorting and pagination. Search runs in SQL. UI3 reduces the
standing inputs to the latest started membership coverage and at most three
relevant membership lines per account; it does not load the money journal.
Limiting candidates before standing would return incorrect filtered pages/counts.

Measure: page latency and rows loaded at 500 and 2,000 members. Optimize only
with a SQL standing expression or a separate denormalized view, not by limiting
before the standing calculation.

## In-memory held and balance scans

`BillingFacts.Held` scans the account's movements on every call, and
`Balances` rescans them. A `/billing` view calls `Held` twice per line;
`AllocateCredit` recomputes balances inside its loop. This is CPU work on an
already-loaded list, not extra SQL.

Measure: CPU time for `/billing` and one scan with 100–200 lines and 2,000+
movements. If it matters, build a held-by-line dictionary and cached balances
once per `BillingFacts.Load`.

## Invoice rendering

Each PDF render copies the whole template tree and runs `typst --version` before
`typst compile`. Startup now rejects a missing template directory or renderer,
and render failures move to `Review` instead of retrying forever.

Measure: per-render latency and CPU for downloads and invoice-mail batches. If
needed, cache the version check and copy only the assets the template uses.

## Lower-priority limits

- `BillingQueries.Reconcile` runs two correlated held-sum subqueries per invoice
  line with no account/date bound. Measure on a large journal before changing it.
- CSV exports materialise an arbitrary date range into one string. Decide a
  maximum range or streaming approach before production exports grow.
- The invoice PDF endpoint is now limited to ten requests per user per minute.
  Output caching was not added; evaluate it only if measured downloads justify
  the extra stale-data risk.
