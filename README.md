# Nulysa

ASP.NET Core membership management for Finnish hackerspace associations. **S0 is
implemented: infrastructure and synthetic acceptance tests, not a usable registry
or a production-ready release.** Membership workflows and essential mail are S1;
billing is S2. The Django reference at `~/code/nulysa-py/` is unchanged.

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

This command is unavailable in Production. OIDC is disabled unless configured.
Never use the published synthetic development credentials in a deployment.

## Checks

The full suite needs the disposable PostgreSQL above, local Keycloak and Chromium.
Tests create/drop only uniquely named `nulysa_s0_*` databases and a synthetic realm.
They refuse a non-loopback database/broker; no production data or legacy importer
is involved. The test database role needs `CREATEDB`.

```sh
docker compose -f compose.yaml -f compose.keycloak.yaml up -d
dotnet build -c Release
tests/Nulysa.Tests/bin/Release/net10.0/.playwright/node/linux-x64/node tests/Nulysa.Tests/bin/Release/net10.0/.playwright/package/cli.js install chromium
dotnet test --no-build -c Release
dotnet format --verify-no-changes --no-restore
python3 scripts/test_dev.py
bash scripts/container-smoke.sh
```

Wait for PostgreSQL and `http://127.0.0.1:58080/realms/master/.well-known/openid-configuration`
before testing. Missing prerequisites fail tests; none of these gates silently skip.
Use `dotnet test --filter 'Group!=Browser&Group!=Worker'` for a shorter local run.
Override `NULYSA_TEST_POSTGRES` or `NULYSA_TEST_KEYCLOAK` only for disposable loopback
instances. Worker/process tests use bounded readiness polling, not simulated workers.

`scripts/container-smoke.sh` builds the release image and checks separate web/worker
processes, encrypted persistent keys, migrations, timer wiring, diagnostics and
schedule reinstall in a fresh database. It supports `CONTAINER_ENGINE=podman` and
`NULYSA_TEST_POSTGRES_CONTAINER=<container-id>`. CI runs all gates.

## Containers

The release image builds with SDK **10.0.403** and runs ASP.NET **10.0.14**, each
explicitly pinned. Microsoft does not publish a `sdk:10.0.113` image; the host SDK
pin remains compatible with Fedora's supported 1xx SDK. Both profiles are tested.

`compose.production.yaml` describes the release topology, **not permission to
deploy S0 publicly**. Supply its required private settings and certificate/key
mounts, start PostgreSQL, run `maintenance migrate` and `maintenance install-schedules`,
then start web/worker. A trusted TLS proxy is external to this file. See the
[runbook](docs/operations.md) before configuring it.
