# Testing instructions

- Before `dotnet test` (fast suite), start local PostgreSQL: `docker compose up -d postgres`.
- Full suite: `dotnet test --settings tests.full.runsettings`. Full suite also requires
  Keycloak: `docker compose -f compose.yaml -f compose.keycloak.yaml up -d`, plus Playwright
  Chromium. Wait for containers to be ready; see [README checks](README.md#checks) for setup.
- When writing tests, consider their runtime. Very slow tests should not be enabled by default.
  The test suite with slow tests excluded should be able to finish in less than 60 seconds.
- Keep slow tests to the "full" test suite.
- Add or update tests for the code you change.
- Prefer running the limited test suite during development. Only run the full test suite when
  touching code that is specifically tested by it. CI will run the full suite.

# Editing this file

- This file should be kept minimal. It is read by every agent working in the repo.
  Only add truly useful, terse information applicable to the entire project here.
