# Nulysa

ASP.NET Core membership management for Finnish hackerspace associations. **S0–S2
are implemented: foundations, membership/mail, billing and subscriptions.**
This is not production-ready; later delivery/cutover gates, including MobilePay
before S6, remain mandatory. The Django reference at `~/code/nulysa-py/` is unchanged.

The portal uses Blazor static SSR, with Interactive Server available per component
when needed. Login/logout remain ordinary HTTP-based Razor Pages; there is no
WebAssembly client or global interactive circuit.

## Design

- [Specification](AGENTS/SPEC.md): authoritative requirements and policy status.
- [Architecture and slices](AGENTS/ARCHITECTURE.md): module boundaries and S0–S6 gates.
- [UI design](AGENTS/UI_DESIGN.md): portal/admin layouts, responsive behaviour,
  light/dark themes, association stylesheets and UI delivery plan.
- [UI foundation](docs/ui.md): implemented shell, appearance preferences and local
  association stylesheet configuration; later task/workflow redesign remains planned.
- [Billing](AGENTS/BILLING_DESIGN.md), [services](AGENTS/SERVICE_DESIGN.md),
  [policy examples](AGENTS/POLICY_REVIEW.md), [remaining work](AGENTS/OPEN_QUESTIONS.md).
- [Foundation evidence and limits](docs/foundations.md),
  [S1 membership/mail](docs/membership.md), [S2 billing/configuration](docs/billing.md),
  [operations runbook](docs/operations.md), [OIDC configuration](docs/oidc-reference.md),
  [performance follow-ups](docs/performance-followups.md).

## Host development

Requires Linux, .NET SDK **10.0.113** and Docker Compose (or Podman's Docker
compatibility). PostgreSQL **18.4** is the only database provider. Invoice rendering
requires Typst **0.15.0**, also used in CI and the release image:

```sh
bash scripts/install-typst.sh "$HOME/.local/bin"
export PATH="$HOME/.local/bin:$PATH"
```

```sh
bash scripts/dev.sh
```

This starts local PostgreSQL, waits for readiness, restores/builds the application,
applies migrations, installs schedules, then runs web and worker together with the
same development settings. No manual exports or second terminal are needed.
**Stop any manually started web/worker first.** Ctrl+C stops both processes but
keeps PostgreSQL and its data. Restart the command after changing application code.
Use `CONTAINER_ENGINE=podman bash scripts/dev.sh` for native Podman.

To run a maintenance command with the same setup:

```sh
bash scripts/dev.sh status
```

Web listens on `http://127.0.0.1:5000`; the worker has no HTTP listener. The launcher
enables purchase balance for both processes by default. Set
`Billing__PurchaseBalanceEnabled=false` to test the disabled configuration; this
does not change deployment defaults or stored balances. Restart the launcher after
changing the setting. Web startup validates schema but never runs jobs. Migrations and schedule installation
are explicit release commands, not startup side effects. No database is seeded
automatically. For a full synthetic demo dataset (admin/board/sysop logins, a
couple of dozen members with varied payment states, applicants, payment-review
receipts and Door/Trailer services):

```sh
bash scripts/dev.sh seed-development-data
```

The seed is explicit, Development-only and one-shot; it never runs on startup or
in Production. See [development data](docs/development-data.md) for the fixed
credentials, scenarios and reset instructions. To create a single synthetic local
login instead:

```sh
DevelopmentUser__Email='synthetic@example.test' \
DevelopmentUser__Password='Synthetic-local-password-123!' \
bash scripts/dev.sh create-development-user
```

This command is unavailable in Production. It grants the new account both
`Administrator` (board administration) and `Superuser` (System Console) roles and
prints the user ID after account creation and both grants commit together.
It supplies a synthetic monthly membership price if none exists and issues the
initial invoice. The launcher supplies a synthetic IBAN; never use it for real payments.
The command accepts any non-empty password,
without Identity's password-complexity rules; normal password validation is unchanged.
OIDC is disabled unless configured.
Never use the published synthetic development credentials in a deployment.

The new development account can review applications immediately. Visit `/apply`,
confirm the verification link printed by the worker (also spooled at
`.local/data-protection/mail/`), then use `/admin/applications`. Development mail is
always printed to the console; set `Mail__Transport=Smtp` plus `Mail__Host`,
`Mail__Port` and `Mail__FromEmail` to send it as well.
Approval queues password setup; `/profile` and `/resign` provide self-service.
Use `/` for the general front page, `/services` for membership/subscriptions and balances,
`/billing` for outstanding bills and history, and `/admin/payments` for bank review/exports.
Configure real prices and bank details before any deployment approvals; no production
prices are seeded. Use synthetic data only. See the [S1 walkthrough](docs/membership.md)
and [S2 walkthrough](docs/billing.md).

## System Console

The read-only console at `/system-console` currently shows scheduler health,
maintenance schedules/last successes, recent failures and billing operation IDs/counts. Other
diagnostics will be added with their features. Only the separate `Superuser` role
grants access; board `Administrator` membership is not sufficient.

From an operator shell, grant/revoke access for an **existing numeric user ID**
(replace `123`; account IDs can be inspected in PostgreSQL):

```sh
bash scripts/dev.sh grant-superuser 123
bash scripts/dev.sh revoke-superuser 123
```

Production uses `dotnet Nulysa.dll grant-superuser 123` / `revoke-superuser 123`
with the usual private environment. These idempotent commands do not create or
approve accounts, and there is no web role editor. Normal login/account gates still
apply. Authorization checks the current database assignment, so revocation takes
effect on the next console request/query without waiting for cookie renewal.

## Checks

The standard test target is fast by default:

```sh
docker compose up -d postgres
dotnet test
```

`tests.runsettings` excludes tests marked `Category=Slow` and the `Group=Browser`
tests. It keeps fast PostgreSQL schema, transaction/concurrency, schedule, OIDC and
static UI checks, including real Typst rendering. Chromium and Keycloak are not needed
for this target. An additional `--filter` narrows this selection; use the full settings
when targeting excluded tests.

The **full target**, including all slow and browser tests, is:

```sh
dotnet test --settings tests.full.runsettings
```

For example, run only the slow cases with
`dotnet test --settings tests.full.runsettings --filter 'Category=Slow'`.

It needs PostgreSQL, local Keycloak and Chromium. Tests create/drop only uniquely
named `nulysa_s0_*` databases and a synthetic realm. They refuse a non-loopback
database/broker; no production data or legacy importer is involved. The test
database role needs `CREATEDB`.

Test counts and timings change as the suite grows; run the commands below or check
CI for current totals. Historical release evidence and limits are recorded in
[S0 evidence](docs/foundations.md), [S1 evidence](docs/membership.md) and
[S2 evidence](docs/billing.md#acceptance-evidence).

Mark new slow tests with `[Trait("Category", "Slow")]`; browser tests use
`[Trait("Group", "Browser")]`. Fast database tests should stay in the standard suite.

Full acceptance checks (also run by CI):

```sh
docker compose -f compose.yaml -f compose.keycloak.yaml up -d
dotnet build -c Release
tests/Nulysa.Tests/bin/Release/net10.0/.playwright/node/linux-x64/node tests/Nulysa.Tests/bin/Release/net10.0/.playwright/package/cli.js install chromium
dotnet test --no-build -c Release --settings tests.full.runsettings
dotnet format --verify-no-changes --no-restore
python3 scripts/test_dev.py
bash scripts/container-smoke.sh
```

Wait for PostgreSQL and `http://127.0.0.1:58080/realms/master/.well-known/openid-configuration`
before testing. Missing prerequisites fail tests; none of these gates silently skip.
Override `NULYSA_TEST_POSTGRES` or `NULYSA_TEST_KEYCLOAK` only for disposable loopback
instances. Worker/process tests use bounded readiness polling, not simulated workers.

Full-run timings identify circuit revalidation (~31s), stalled SMTP timeout (~15s),
worker recovery/retry scenarios (~5s each), and subprocess status checks (~6s) as the
main slow paths. They remain intact in the full target; no assertions or recovery waits were
weakened to make the standard target faster. Browser tests are full-only because
they also require external browser/broker setup.

`scripts/container-smoke.sh` builds the release image and checks separate web/worker
processes, encrypted persistent keys, migrations, timer wiring, diagnostics and
schedule reinstall in a fresh database. CI runs all gates. With Podman Compose,
pass the PostgreSQL container explicitly because `compose ps -q` is unsupported:

```sh
CONTAINER_ENGINE=podman NULYSA_TEST_POSTGRES_CONTAINER=<container-id> \
    bash scripts/container-smoke.sh
```

## Containers

The release image builds with SDK **10.0.403** and runs ASP.NET **10.0.14**, each
explicitly pinned. Microsoft does not publish a `sdk:10.0.113` image; the host SDK
pin remains compatible with Fedora's supported 1xx SDK. Both profiles are tested.

`compose.production.yaml` describes the release topology, **not permission to
deploy this unfinished registry publicly**. Supply its required private settings and certificate/key
mounts, start PostgreSQL, run `maintenance migrate` and `maintenance install-schedules`,
then start web/worker. A trusted TLS proxy is external to this file. See the
[runbook](docs/operations.md) before configuring it.
