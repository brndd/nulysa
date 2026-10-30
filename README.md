# Nulysa

ASP.NET Core membership management for Finnish hackerspace associations. **S0 is
implemented: infrastructure and synthetic acceptance tests, not a usable registry
or a production-ready release.** Membership workflows and essential mail are S1;
billing is S2. The Django reference at `~/code/nulysa-py/` is unchanged.

The portal uses Blazor static SSR, with Interactive Server available per component
when needed. Login/logout remain ordinary HTTP-based Razor Pages; there is no
WebAssembly client or global interactive circuit.

## Design

- [Specification](AGENTS/SPEC.md): authoritative requirements and policy status.
- [Architecture and slices](AGENTS/ARCHITECTURE.md): module boundaries and S0–S6 gates.
- [Billing](AGENTS/BILLING_DESIGN.md), [services](AGENTS/SERVICE_DESIGN.md),
  [policy examples](AGENTS/POLICY_REVIEW.md), [remaining work](AGENTS/OPEN_QUESTIONS.md).
- [Foundation evidence and limits](docs/foundations.md),
  [operations runbook](docs/operations.md), [OIDC configuration](docs/oidc-reference.md).

## Host development

Requires Linux, .NET SDK **10.0.113** and Docker Compose (or Podman's Docker
compatibility). PostgreSQL **18.4** is the only database provider.

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

Web listens on `http://127.0.0.1:5000`; the worker has no HTTP listener. Web
startup validates schema but never runs jobs. Migrations and schedule installation
are explicit release commands, not startup side effects. No database is seeded
automatically. To create a synthetic local login:

```sh
DevelopmentUser__Email='synthetic@example.test' \
DevelopmentUser__Password='Synthetic-local-password-123!' \
bash scripts/dev.sh create-development-user
```

This command is unavailable in Production. It grants the new account the `Superuser`
role for System Console access; account creation and the grant commit together.
It accepts any non-empty password,
without Identity's password-complexity rules; normal password validation is unchanged.
OIDC is disabled unless configured.
Never use the published synthetic development credentials in a deployment.

## System Console

The read-only console at `/system-console` currently shows scheduler health,
maintenance schedules/last successes and recent final failure observations. Other
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
static UI checks. Chromium and Keycloak are not needed for this target. An additional
`--filter` narrows this selection; use the full settings when targeting excluded tests.

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

Baseline measured when the fast/full split was introduced, with dependencies
already running (restore/build included):

| Target | Cases | Test execution | Total command |
| --- | ---: | ---: | ---: |
| Standard | 27 | 18.7s | 25.4s |
| Full | 41 | 115.3s | 117.2s |

The current totals are 29 standard / 45 full cases;
the standard suite still runs in about 20 seconds, below the 60-second budget.

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

The last full-run timings identified circuit revalidation (~31s), worker recovery/
retry scenarios (~55s combined), and subprocess status checks (~6s) as the main slow
paths. They remain intact in the full target; no assertions or recovery waits were
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
deploy S0 publicly**. Supply its required private settings and certificate/key
mounts, start PostgreSQL, run `maintenance migrate` and `maintenance install-schedules`,
then start web/worker. A trusted TLS proxy is external to this file. See the
[runbook](docs/operations.md) before configuring it.
