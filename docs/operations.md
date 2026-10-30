# Foundation operations

This is the S0 runbook, not a production-readiness declaration. Use one release
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
- Optional `Association__Name`, `Association__TimeZone` (default Europe/Helsinki),
  `Worker__Concurrency` (default 4, allowed 1–32). All workers share scheduler name
  `Nulysa` and use automatically unique instance IDs. Default cluster check-in is
  5 seconds plus a 15-second failure-detection allowance; test fixtures use shorter
  values. Raise these for expected production pauses, not to promise exactly once.

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
- There are no placeholder billing, bank, deletion or storage jobs. Later slices
  use persisted dated work/operation keys. `Schedules.Fortnightly` establishes an
  anchored 14-calendar-day cadence across DST; day-of-month `*/14` is not equivalent.

Recoverable jobs request recovery. Graceful shutdown cancels cooperating jobs and
hands unfinished firings back; the host's shutdown deadline is 20 seconds. Set the
container/orchestrator stop allowance longer (at least 25 seconds). Uncooperative
code may outlive cancellation: renderers must eventually run in killable child
processes. Two ordinary retries are configured after an initial failure; crash loops
are not bounded by this budget. Recovery and stale completions require domain
transactions/unique keys, not a custom lease/queue framework.

## Independent monitoring

`dotnet Nulysa.dll status` writes one redacted JSON document to stdout and returns
0 only when healthy; unhealthy/configuration/database failures return 1. There is
no public scheduler dashboard, arbitrary Run now or editable job-type endpoint.
Local command access is operator access; protect container/DB/shell permissions.

Checks include:

- missing heartbeat or heartbeat older than three minutes;
- missing cleanup or cleanup older than 26 hours (including the initial period
  before its first midnight run);
- absent/paused/error/complete schedules, or next-fire time over two minutes late;
- original one-shot work age over five minutes, even across recovery/crash loops;
- unreadable enqueue metadata and recorded final failures, independently of history.

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
retry it. No mail is sent in S0. Before public deployment add and rehearse backups,
restore/erasure handling, alert routing, key rotation and password-guessing protection.
