# Application operations

This covers S0–S2, not a production-readiness declaration. Use one release
artifact with `web`, `worker` and explicit maintenance commands. PostgreSQL is the
only database; do not run against a Django database or production during development.

## Release order

1. Stop/drain old workers when schema or job definitions require it. Back up first.
2. Run `dotnet Nulysa.dll migrate` once with the release credentials. EF applies its
   versioned migrations. Reviewed additive Quartz 4.6 upgrades are applied in a
   transaction; Quartz's pinned, guarded create-if-missing schema handles a fresh
   installation. It never drops/recreates scheduled work. Older-than-4.4 Quartz
   schemas are refused and need reviewed upstream upgrades first.
3. Run `dotnet Nulysa.dll install-schedules`. It takes a PostgreSQL advisory lock
   and enlists Quartz in the same EF transaction. Existing matching definitions
   retain start/next-fire times; conflicting definitions fail instead of resetting
   phase. A schedule change needs an explicit reviewed migration/update.
4. Start `dotnet Nulysa.dll web` and one or more `dotnet Nulysa.dll worker` processes.
   Startup refuses missing EF/Quartz schema. Web initializes but does not start
   acquisition; worker has no HTTP listener. Never migrate/install at every startup.

`compose.production.yaml` supplies this topology. Use
`docker compose -f compose.production.yaml run --rm maintenance migrate` and then
`... run --rm maintenance install-schedules` before starting web/worker. Supply all
required values privately; `.env` and `.local/` are ignored by Git.

## Required private configuration

- `ConnectionStrings__Database`: PostgreSQL connection string. Bound connection/
  statement operations; EF/Quartz command timeout is 15 seconds. Use verified TLS
  for a remote database. Password-only deployments without Kerberos can set
  `GSS Encryption Mode=Disable` in this string.
- `DataProtection__Path`: absolute, persistent shared key-ring path, writable only
  by the application user. Production additionally requires
  `DataProtection__CertificatePath` and its private certificate password; key-ring
  XML is encrypted with that certificate. The image runs as non-root `app`.
- `AllowedHosts`: actual hostname(s). TLS terminates at the operator's proxy;
  `ReverseProxy__KnownProxy` is the single exact trusted proxy IP. Forwarded headers
  are ignored otherwise, limited to one hop. HTTPS redirect targets port 443.
- OIDC settings and separate persistent RSA keys: see [OIDC](oidc-reference.md).
- Essential mail, membership retention/erasure and terms settings:
  see [S1 operations](membership.md#mail-configuration-and-failure-handling).
- Invoice payee/Typst and GoCardless settings: see [S2 operations](billing.md).
  Supply the real checksum-valid `Billing__Iban` in Production. Configure the
  membership price before approving members; a missing price rolls back approval.
- Optional `Association__Name`, `Association__TimeZone` (default Europe/Helsinki),
  `Worker__Concurrency` (default 4, allowed 1–32). All workers share scheduler name
  `Nulysa` and use automatically unique instance IDs. Default cluster check-in is
  5 seconds plus a 15-second failure-detection allowance; test fixtures use shorter
  values. Raise these for expected production pauses, not to promise exactly once.

The portal is static SSR by default. When adding Interactive Server components,
the TLS proxy must support long-lived SignalR/WebSocket connections. Multiple web
instances need session affinity for circuits; sharing Data Protection keys alone
does not share their in-memory UI state. A restart can lose that state, so keep
operation outcomes in PostgreSQL and recheck them after reconnect/reload.

Never put secrets, profiles, provider payloads or personal identifiers into job keys
or history. Registered jobs receive minimal string identifiers. The failure wrapper
keeps the exception type, not its private message/inner exception. Database history
retains at most 2,000 entries / seven days, with input recording disabled. It is not
atomic with domain writes and can fail or be trimmed. Logs are not a profile archive.

## Schedules and recovery

- `maintenance.heartbeat`: every 30 seconds; missed ticks are skipped, not replayed.
- `maintenance.authentication-cleanup`: Helsinki midnight, one coalesced missed run.
  OpenIddict pruning preserves usable refresh chains. Token expiry/account gates
  are enforced on requests even if maintenance is late.
- `maintenance.membership-due-work`: every minute, coalescing missed scans, bounded
  application/mail cleanup and recovery of unscheduled pending delivery intents.
- `maintenance.final-deletion`: next local midnight on first installation, then every
  14 calendar days; preserves its persisted phase across reinstall/restarts and DST.
  Eligibility is rechecked per member, never inferred from a scheduler tick.
- `maintenance.billing-due-work`: every minute, per-account locked generation,
  expiry, service removal and bank-staleness alerts; dated standing does not wait
  for the scan. Zero-price catch-up is bounded to 24 periods per account/run.
- `maintenance.bank-sync`: association-local midnight, booked GoCardless imports.
  Disabled feeds return without external calls. Inspect provider status separately
  from scheduler success. There is no storage job yet.
  Day-of-month `*/14` is not an anchored fortnightly schedule.

After a complete worker outage, due work can run before bank sync imports an
on-time receipt. Corrections are order-independent; preferring bank sync on
recovery is an additional mitigation. `billing-due-work` counts as stale after
three minutes, so a 15–22 day outage is an operational incident. Late imports with
a running worker are recovered when the renewal line exists; the no-line cases
need the worker to miss the invoice-preparation window through expiry.

Recoverable jobs request recovery. Graceful shutdown cancels cooperating jobs and
hands unfinished firings back; the host's shutdown deadline is 20 seconds. Set the
container/orchestrator stop allowance longer (at least 25 seconds). Uncooperative
code may outlive cancellation: invoice rendering uses a killable child process with
a 20-second deadline. Two ordinary retries are configured after an initial failure; crash loops
are not bounded by this budget. Recovery and stale completions require domain
transactions/unique keys, not a custom lease/queue framework.

## Independent monitoring

`dotnet Nulysa.dll status` writes one redacted JSON document to stdout and returns
0 only when healthy; unhealthy/configuration/database failures return 1. There is
no public scheduler dashboard, arbitrary Run now or editable job-type endpoint.
Local command access is operator access; protect container/DB/shell permissions.

The specification also requires a separate superuser-only, initially read-only
[System Console](../AGENTS/SPEC.md#system-console). It is cross-cutting, not limited
to the `operations` module; diagnostic views will be added alongside features
throughout implementation. The first post-S0 increment is at `/system-console`,
showing scheduler health, schedule state/next execution/last success and the latest
50 final failure observations. It is a read-only snapshot; reload to refresh.
S1 also shows the latest 50 delivery IDs, states, attempts and known provider IDs,
without token-bearing message bodies or recipient profiles.
S2 adds open-invoice/review-receipt counts and the latest 25 billing operation IDs,
account IDs, kinds, result IDs and timestamps; no financial repair buttons.
Unavailable diagnostics are reported as unavailable, not healthy. There are no
retry/cancel/schedule-edit actions. This is not an added S0 exit gate.

Use operator-shell commands `dotnet Nulysa.dll grant-superuser <user-id>` and
`dotnet Nulysa.dll revoke-superuser <user-id>` for existing numeric account IDs.
They serialize role changes in a transaction and are idempotent. Neither command
creates/adopts a member or bypasses account gates. There is no web role editor.
The console route and diagnostic query both check live superuser assignment in a
fresh scope; revocation does not wait for existing cookie role claims to expire.

Board-administrator access alone does not grant access. Superusers are trusted
sysops with assumed server access; avoid unnecessarily displaying sensitive data,
without elaborate console-specific redaction. Existing log/history redaction is
unchanged. The CLI status command remains the independent monitoring interface.

Checks include:

- missing heartbeat or heartbeat older than three minutes;
- missing cleanup or cleanup older than 26 hours (including the initial period
  before its first midnight run);
- absent/paused/error/complete schedules, or next-fire time over two minutes late;
- original one-shot work age over five minutes, even across recovery/crash loops;
- unreadable enqueue metadata and recorded final failures, independently of history.
- missing/stale membership scan, stale deletion batch after its first run, and
  pending/failed/uncertain mail independently of Quartz execution history.

All one-shot application work belongs to Quartz group `work` and stamps its original
UTC `enqueued_at` on the job detail. The synthetic worker harness demonstrates the
pattern; operation keys and delivery truth belong to each real feature, not Quartz.
The status command outputs counts/issue codes, not job payloads or exception messages.

An external monitor must execute this command and alert; the failing worker cannot
be its own only monitor. `/health/live` proves only that the web process responds.
For stale/aged work inspect release registration, worker logs and the domain operation.
For final failures use a future feature-specific repair command; do not clear flags
or rerun arbitrary jobs to make monitoring green. A later successful occurrence clears
that job's operational failure observation, not its financial/delivery history.

An uncertain external send needs provider reconciliation/review; do not blindly
retry it. S1's failure/retention handling is described in the membership runbook.
Before public deployment add and rehearse backups,
restore/erasure handling, alert routing, key rotation and password-guessing protection.
