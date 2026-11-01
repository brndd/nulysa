# S0 foundation evidence

S0 is implemented in C#/ASP.NET Core. Evidence below comes from this checkout's
synthetic PostgreSQL/process/protocol/browser tests, **not** the Django suite.
S0 itself included no membership, billing, mail delivery or legacy migration workflow.
The subsequent [S1 implementation](membership.md) now adds real membership and
essential mail, preserving these infrastructure gates. [S2](billing.md) now adds
billing/subscriptions; legacy migration remains later work.

A first post-S0 System Console increment now provides a superuser-only static SSR
shell and read-only scheduler diagnostics. Operator CLI commands grant/revoke the
Identity `Superuser` role; live checks deny board-only users and revoked accounts.
`SystemConsoleTests` covers route/query permissions, existing-cookie revocation,
health/failure/unavailable views and CLI provisioning (the subprocess case is
full-suite-only). S1 added delivery diagnostics and S2 billing diagnostics; further
cross-module views remain feature follow-up.
Verified 31 October 2026: the fast and full Release cases passed; formatting and
migration-model consistency checks passed. Run CI or the commands in
[`README.md`](../README.md) for current counts and timings.

The portal uses a Blazor Web App with static SSR by default and opt-in Interactive
Server support. Login/logout remain HTTP-based Razor Pages. `UiTests` covers static
rendering, branding, framework assets and antiforgery-protected HTTP logout;
`InteractiveUiTests` exercises events, transport reconnection/state retention and
account-gate revalidation with a test-only component, not a shipped demo page.
Interactive authentication rechecks account gates/security stamps every 30 seconds
in a fresh scope; future domain commands must still enforce authorization themselves.

**Locally verified 30 October 2026:** the Release suite passed on PostgreSQL 18.4,
including the Blazor UI checks, real worker processes and the Chromium/Keycloak
flow. The production-mode container smoke passed, formatting/model-drift checks
passed, and NuGet reported no
known vulnerable packages in the resolved graph.

## Pinned foundation

| Component | Version |
| --- | --- |
| Host SDK / observed Fedora runtime | 10.0.113 / 10.0.13 |
| Container build SDK / runtime | 10.0.403 / 10.0.14 |
| Identity / EF Core | 10.0.14 |
| Blazor framework assets | 10.0.14 |
| Npgsql EF provider | 10.0.3 |
| OpenIddict | 7.7.2 |
| Quartz persistent store / serializer / history | 4.6.2 |
| PostgreSQL | 18.4 |
| Synthetic Keycloak / Playwright | 26.4.7 / 1.64.1 |

SDKs, direct dependencies, transitive lockfiles and the EF CLI are pinned. Compiler,
nullable analysis, SDK analyzers, formatting and NuGet vulnerability auditing are
enabled; warnings are errors. The cryptography dependencies include the October
security fixes rather than suppressing vulnerability warnings.

## Executable evidence

| Contract | Test/source |
| --- | --- |
| Identity, stable subjects, unique normalized email, non-reused numeric IDs | `SchemaTests` |
| Shared EF/Quartz transaction; no visibility before commit; rollback on schedule failure | `WorkerTests.Shared_transaction*`, `Scheduling_failure*` |
| Queued restart, recovery after death before/after commit, surviving peer | `WorkerTests.Queued_work*`, `Process_death*` |
| Shutdown cancellation and recovery, bounded failures/timeouts | `WorkerTests.Graceful_shutdown*`, `Ordinary_failures*` |
| Concurrent duplicate commands, conflicting inputs, stopped/late worker overlap | `WorkerTests.Concurrent_retries*`, `False_failure_detection*` |
| Cancellation is not a hard kill | `WorkerTests.Cancellation_is_cooperative*` |
| Repeated crashes remain aged even without failure history | `WorkerTests.Repeated_process_death*` |
| Idempotent named installation, conflicts, timer heartbeat and missed cleanup coalescing | `ScheduleTests` |
| Helsinki midnight and anchored 14-day intervals across both DST changes; persisted phase | `CalendarTests`, `ScheduleTests.Persisted_calendar*` |
| Quartz 4.5-shaped schema delta, preserving work | `SchemaTests.Quartz_45*` |
| Redacted JSON, exit status, missing/overdue/aged/failed work, history-independent failure observations | `ConfigurationTests`, `SchemaTests.Diagnostics*`, `WorkerTests` |
| Code/refresh flow, signatures/issuer/audience/nonce, redirects/scopes/S256/client authentication, concurrent code redemption | `OidcTests` |
| Admission/deletion gates on cookie, authorization, code exchange, refresh and userinfo | `OidcTests.Account_gate*` |
| 365 idle days, exact boundary, renewal beyond an absolute year, cleanup preserving usable refresh | `OidcTests.Refresh*`, `Cleanup*` |
| Cookie/key persistence across restarts and concurrent instances; antiforgery | `OidcTests.Cookie*` |
| Real replacement → Keycloak → downstream browser login and email-change continuity | `KeycloakTests` |
| Static SSR portal, branding, framework assets and antiforgery-protected HTTP logout | `UiTests` |
| Opt-in server events, transport reconnect/state retention and account-gate revalidation | `InteractiveUiTests` |
| Production configuration validation, encrypted keys and separate release processes | `ConfigurationTests`, `scripts/container-smoke.sh` |

`scripts/test_dev.py` separately checks the development launcher's environment,
process cleanup and failure paths.
`dotnet test` uses the fast default filter in `tests.runsettings`; slow recovery,
retry/status and browser checks run with `dotnet test --settings tests.full.runsettings`.
The full target remains the acceptance gate and is run by CI, not the default target.
Both targets were verified after the split; see CI for current counts and timings.
Commands and prerequisites are in
[`README.md`](../README.md); GitHub Actions runs them and the release-container smoke.
CI configuration existing in this checkout is not a claim that a remote Actions run
has already completed.

## Observed limits and deliberately unproved claims

- Quartz execution is at least once. A paused worker can resume after a peer has
  recovered its firing. The synthetic command's PostgreSQL lock/unique operation
  key prevents duplicate effects; Quartz clustering alone does not fence old code.
- Cancellation only stops cooperating code. Shutdown recovery is explicitly enabled;
  ordinary failures receive two retries after the initial attempt. Process deaths
  can repeat indefinitely and must be detected from original work age.
- History is bounded diagnostics, not business truth. Separate minimal failure
  observations survive history trimming; later feature commands must expose their
  own unfinished delivery/purchase records. An accepted/uncertain external send is
  **not** proven safe by this synthetic domain-effect test.
- Upgrade testing reconstructs the upstream 4.5→4.6 schema delta; it does not run an
  old Quartz binary or certify every historical upstream migration. Older schemas
  require reviewed upstream migrations. No vendor drop/recreate script is used.
- Authentication cleanup is first due at the next Helsinki midnight. Until its first
  successful run, status honestly reports a missing cleanup observation. A fresh
  heartbeat alone does not certify cleanup or all other jobs.
- Account gates still use admission/deletion facts, now written by S1's real
  application/approval/password setup/reset/resignation/restoration/erasure commands.
  Payment standing must never become the login flag. See S1's separate evidence/limits.
- Existing Keycloak/downstream sessions survive disabling the upstream account.
  Only fresh upstream access is blocked. No production realm, Matrix/Nextcloud
  mapping or legacy identity switch has been certified; those are S6 work.
- Production password-guessing protection, operational backup/restore and key rotation,
  privacy/retention validation and all later slices remain deployment gates. S0 is
  not suitable for public deployment.
