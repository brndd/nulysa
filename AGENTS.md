# Working in this project

- Read `AGENTS/SPEC.md` and `AGENTS/ARCHITECTURE.md`; follow their current slice.
  Preserve confirmed domain decisions and distinguish proposals from policy.
- S0 has an ASP.NET Core implementation and its own synthetic PostgreSQL/process/
  OIDC/browser acceptance suite; see `docs/foundations.md` for evidence and limits.
  S1 has not been implemented. Do not claim Django tests validate the new stack.
- Target C#/ASP.NET Core on a supported .NET LTS release, Blazor Web App (static SSR
  by default, selective Interactive Server; no initial WebAssembly/Auto mode),
  EF Core/Npgsql/PostgreSQL, Identity/OpenIddict and Quartz.NET persistent jobs.
  Pin SDK/packages during scaffolding; no SQLite support initially.
- The S0 portal uses Blazor static SSR; login/logout remain HTTP-based Razor Pages.
  Preserve those authentication endpoints and S0 tests when extending the UI.
- Use named commands and direct calls, one explicit transaction for domain writes
  and associated Quartz scheduling, and scoped DbContexts. No generic repository,
  mediator/event pipeline or custom queue framework.
- Use real isolated PostgreSQL for database/financial/concurrency/worker tests;
  never production data. Keep secrets local and out of docs/source control.
- `~/code/nulysa-py/` is the Django design/test reference; `~/code/mulysa/` is the legacy
  integration/migration reference. Do not modify either as part of this replacement
  unless explicitly asked. `README.md` indexes the planning documents.
