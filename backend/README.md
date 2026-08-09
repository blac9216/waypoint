# backend

ASP.NET Core (C#) application — REST API, job engine, PowerShell runspace hosting, SSE
streaming. See [ADR-0006](../docs/adr/0006-backend-language.md) and
[ADR-0008](../docs/adr/0008-job-engine.md).

## Solution layout

| Project | Purpose |
|---|---|
| `Waypoint.Api` | ASP.NET Core host: controllers, middleware, authentication, DI composition root (`Program.cs`). |
| `Waypoint.Core` | Domain layer: roles/authorization, the error envelope, pagination, configuration option types, the local-auth and log-redaction abstractions. No ASP.NET Core hosting dependency beyond the lightweight `Microsoft.AspNetCore.Authorization` package. |
| `Waypoint.Infrastructure` | DI wiring for the abstractions `Waypoint.Core` declares (the in-memory local-auth implementation) plus Postgres access: the schema migrations pipeline (`Data/`, issue #4). PowerShell runspace hosting lands with the job engine. |
| `Waypoint.Tests` | xUnit — unit tests for `Core`/`Infrastructure` plus `WebApplicationFactory`-based integration tests against the real HTTP pipeline. |

## Build and test

```bash
cd backend
dotnet build Waypoint.sln
dotnet test Waypoint.sln
```

## Lint and format

The canonical pre-PR command, mirroring the `Invoke-ScriptAnalyzer` convention in the
sibling PowerShell repos:

```bash
cd backend
dotnet format Waypoint.sln --verify-no-changes
```

`dotnet build` already runs the Roslyn analyzers repo-root `Directory.Build.props`
turns on (`EnableNETAnalyzers`, `AnalysisLevel=latest`) as build warnings — nullable,
disposal, async, locale (`CA1305`), and performance (`CA1848`) diagnostics surface
there, not only in an IDE. Formatting, brace style, naming, and the file-header rule
below live in the repo-root `.editorconfig`; `dotnet format` (without
`--verify-no-changes`) applies fixes for all of it, including inserting a missing
license header. Both a clean `dotnet build` and a clean
`dotnet format --verify-no-changes` are required before opening a PR.

## Run locally

Local auth (ADR-0004 rollout note) is a **dev-grade single admin user** — there is no
compiled-in default password. Set `LocalAuth__AdminPasswordHash` (a salted, iterated
PBKDF2 hash — see `Pbkdf2PasswordHasher`, issue #62) before the API will accept any
login; with it unset, login always fails closed:

```bash
cd backend
dotnet build Waypoint.Api
# Prompts for the password (input hidden) and prints the hash to stdout. Never pass
# the password as an argument — that would land it in argv, readable via
# /proc/<pid>/cmdline (docs/security.md control 2). Never commit the plaintext or the hash.
dotnet run --project Waypoint.Api --no-launch-profile --no-build -- --hash-password

export LocalAuth__AdminPasswordHash=<hash from above>
dotnet run --project Waypoint.Api --no-launch-profile
# GET  http://localhost:5000/api/v1/health         (no auth)
# POST http://localhost:5000/api/v1/auth/login      { "username": "admin", "password": "<your-dev-password>" }
```

`LocalAuth__AdminPasswordHash` is fine for this bare `dotnet run` loop, but it leaks via
`/proc/<pid>/environ`, `docker inspect`, and crash dumps — the containerized compose
stack (`deploy/docker-compose.yml`, `deploy/README.md` "Bring-up" step 3) prefers a
mounted file instead: set `LocalAuth__AdminPasswordHashFile` to a path containing the
hash and it takes precedence (issue #333, `LocalAuthOptionsPostConfigure`). The env var
above remains a supported fallback either way — a mounted file just isn't the natural
shape for a local `dotnet run` session, so this quick-start keeps using it.

`--no-launch-profile` is not optional here. Without it, `dotnet run` applies the `http`
profile in `Waypoint.Api/Properties/launchSettings.json`, whose
`applicationUrl: http://localhost:5205` **silently overrides both the default port above
and any `ASPNETCORE_URLS` you export** — so the URLs in this block would refuse
connections against a perfectly healthy build. Bind a different port explicitly with
`--urls`, e.g.:

```bash
dotnet run --project Waypoint.Api --no-launch-profile --urls http://127.0.0.1:5299
```

## Database & schema (issue #4)

The M1 subset of the schema — `credentials`, `credential_secrets`, `runs`, `jobs`,
`job_events`, `depot_artifacts`, `downloads`, `audit_log`, `appliance_state` — is the
contract in [`docs/api-contract.md`](../docs/api-contract.md) ("Postgres schema
sketch"); this section is a pointer, not a duplicate. ADR-0002 (Postgres), ADR-0005
(envelope-encrypted secrets), and ADR-0008 (job engine) are the design rationale for
that shape.

- **Migrations run before the API takes traffic** (ADR-0009 expectation), not lazily
  on first request: `Program.cs` calls `Waypoint.Infrastructure.Data.ISchemaMigrator`
  right after `WebApplicationBuilder.Build()`, before the request pipeline is wired up
  at all. A migration failure is a fatal startup exception like any other (exit 1).
- **Pipeline shape**: a small hand-rolled runner (`NpgsqlSchemaMigrator`) over
  embedded `Data/Migrations/*.sql` files, tracked in a `schema_migrations` table —
  not EF Core's generated-migration model. The job queue's claim query (ADR-0008) is
  raw `SELECT ... FOR UPDATE SKIP LOCKED` by nature, so this keeps one data-access
  story (plain Npgsql) instead of mixing an ORM in for schema only. Every migration
  file is written to be idempotent on its own (`IF NOT EXISTS` / `OR REPLACE` /
  `ON CONFLICT`) as defense in depth beyond the tracking table.
- **Configuration**: the connection string is the standard ASP.NET Core
  `ConnectionStrings:Waypoint` slot (`ConnectionStrings__Waypoint` env var to
  override); `Database:RunMigrationsOnStartup` (default `true`) gates whether startup
  applies migrations at all — `appsettings.Testing.json` turns it off because the
  in-process `WebApplicationFactory` test host has no Postgres to migrate against.
  `appsettings.json`'s default connection string matches
  `deploy/docker-compose.yml`'s `postgres` service defaults (dev-only credentials
  already committed there), so the image works against the dev stack unmodified.
  `deploy/docker-compose.yml` also sets `ConnectionStrings__Waypoint` on the
  `backend` service, composed from the same `POSTGRES_USER`/`POSTGRES_PASSWORD`/
  `POSTGRES_DB` variables the `postgres` service reads (#103) — an operator who
  overrides those in `deploy/.env` does not need to separately override the
  backend's connection string too; both containers move together.
- **Deviations from the contract sketch**:
  - `jobs.run_id` is **nullable**. Only the scan/remediate job types are ever fanned
    out from a Run (ADR-0008); the M1 download vertical slice's job types
    (`download`, `catalog-index`, ...) are queued as standalone jobs per the
    per-endpoint "202 → `<type>` job" notes in the API contract.
  - `runs.site_id` and `jobs.target_id` remain **plain UUID columns with no FK**, even
    though `sites`/`targets` now exist (migration 0009, issue #19) — adding one now
    would be an unrelated schema change to tables issue #19 doesn't otherwise touch, and
    the M2 run/scan slice that actually populates `runs.site_id`/`jobs.target_id` is
    better placed to decide the FK's `ON DELETE` behavior alongside that work.
    `jobs.target_name` carries a human-readable label for job types with no target row
    at all (e.g. a depot artifact name for a `download` job).
  - `sites`/`targets` (migration 0009, issue #19): `targets.kind` is a `CHECK` against
    the closed `vsphere`/`nsx-api`/`ssh` set rather than a lookup table (three
    hand-authored values, per `docs/domain-model.md` "Target" — a table would be
    over-engineering for a set this size and this stable). `targets.connection` is
    JSONB (kind-specific fields, e.g. hostname) for the same "shape is still
    planning-grade" reasoning ADR-0002 already gives `depot_artifacts.metadata`, and
    the API layer (not the schema) enforces that it never carries secret material —
    only `credential_ref` ever names a credential. `discovery_status`'s value set
    (`never_discovered`/`discovering`/`discovered`/`failed`) is invented: neither
    `docs/domain-model.md` nor `docs/api-contract.md` enumerates it, only the field
    name.
- **Queue-claim index**: `idx_jobs_queue_claim` is a partial index on
  `(priority, created_at) WHERE state = 'queued'`, matching the ADR-0008 claim query
  exactly — the claim is an index scan over only the claimable rows, not a table
  scan. `idx_jobs_lease_recovery` (`lease_expires_at) WHERE state = 'running'`)
  serves dead-job recovery from the lease/heartbeat columns present from day one.
- **`job_events` scoping**: the contract's six event types are not all job-scoped, so
  neither `job_id` nor `run_id` can be `NOT NULL` for the table as a whole. Three
  tiers, enforced by `job_events_scope_check` — **job-scoped** (`job_id` required:
  `job.state`, `job.log`, `download.progress`), **run-scoped** (`run_id` required,
  `job_id` NULL: `run.progress`, `queue.state`, a "queue" being a *run's* priority
  queue per ADR-0008 and `domain-model.md`), and **appliance-wide** (both NULL:
  `system.notice`, which belongs to no job and no run and must still be carriable on
  the global stream). An unclassified seventh event type fails closed.
- **`job_events.seq` is assigned in commit order**, which is the guarantee
  `Last-Event-ID` replay actually needs: *a reader that has observed `seq = N` on a
  stream can never afterwards miss a committed event with `seq < N`.* A plain
  `BIGINT GENERATED ALWAYS AS IDENTITY` does **not** provide that — identity values
  are handed out at `INSERT` time while rows become visible at `COMMIT` time, so a
  slow writer holding the lower `seq` can commit after a faster writer holding the
  higher one, and a client that recorded the higher value as its `Last-Event-ID` would
  never see the lower one again. Instead `trg_job_events_assign_seq` takes a
  transaction-scoped advisory lock and *then* draws from `job_events_seq_seq`; because
  the lock is held until commit, a lower `seq` is always already committed before a
  higher one is even assigned. The lock is global rather than per-run because the API
  contract's global stream is itself a stream. **Cost, stated plainly**: every
  `job_events` INSERT serializes against every other, and the lock is held for the
  remainder of the inserting transaction — write events in short transactions and as
  late in a transaction as possible. Holding the lock across other row locks is also
  a deadlock surface (emit-then-update-row versus update-same-row-then-emit), which
  "emit last, in a short transaction" avoids as well. `seq` is *not* gap-free (a
  rolled-back insert burns its value); replay safety does not depend on gap-freeness.
- **`job_events` replay shape**: both SSE scopes in the API contract (global and
  per-run) are a `seq > $lastEventId ORDER BY seq` index range scan, never a sort —
  `seq` is the primary key and `idx_job_events_run_seq` covers the per-run scope.
- **`credential_secrets`** carries `ciphertext`, `data_key_wrapped`, and
  `master_key_id` as separate columns (ADR-0005) so master-key rotation is a re-wrap,
  not a schema change.
- **`depot_artifacts.metadata`** is `JSONB` — vendor catalog shapes are not
  normalised into columns (ADR-0002); `sha256` and `status` are promoted to real
  columns because checksum verification and catalog-status queries need them.

Tests: `Waypoint.Tests/Infrastructure/Postgres/` runs the real migrations pipeline,
the queue-claim query, and the `job_events` scope and commit-order guarantees against
a real, disposable PostgreSQL 16 container (docker) — see the class doc comments for
what each proves. `JobEventsSeqTests` deliberately interleaves writers so that
assignment order and commit order diverge, because a concurrency test whose writers
all commit before the reader queries cannot fail.
They share one container per test run via an xUnit collection fixture
(`PostgresFixture`), isolated per `docs/testing.md`'s recipe (a container name and
host port unique to the run).

### The job_events write budget (issue #117)

`trg_job_events_assign_seq` serializes every `job_events` INSERT behind one advisory
lock -- that is what makes `Last-Event-ID` replay safe, and it caps row-at-a-time
writes at ~900 events/s no matter how many writers (measured in #117). The budget,
decided in epic #6 slice 1:

- **Volume writers use `IJobLogBuffer`** (`BufferedJobEventWriter`): batches of at
  most `JobEngine:EventBatchMaxSize` (100) rows per multi-row INSERT, flushed every
  `JobEngine:EventFlushInterval` (250 ms), so one lock acquisition amortizes across
  the batch. Delivery is best-effort with a bounded buffer
  (`JobEngine:EventBufferCapacity`, 10,000): a full buffer or failed flush drops
  events, counted and logged -- `job_events` is observability, never job/run state.
- **State-transition emits use `IJobEventPublisher`**: one row, durable now.
- **Nothing follows an emit inside its transaction.** Both writers emit in their own
  autocommit statement; holding the ordering lock across other work is the anti-
  pattern the schema doc comments warn against.
- Every event INSERT carries `JobEngine:EventCommandTimeoutSeconds` (5 s), not
  Npgsql's inherited 30 s, and the timeout log line names lock contention as the
  likeliest cause.

Both paths scrub payloads through `ISecretRedactor` (`InPlaySecretRedactor`) before
the row is written -- `docs/security.md` control 1 at the Postgres sink.

## Docker

```bash
docker build -t waypoint-api backend
# Optional build args stamp GET /api/v1/health's version/build fields:
docker build -t waypoint-api \
  --build-arg BUILD_VERSION=0.1.0 --build-arg BUILD_SHA=$(git rev-parse HEAD) --build-arg BUILD_DATE=$(date -u +%FT%TZ) \
  backend

docker run --rm -p 8080:8080 -e LocalAuth__AdminPasswordHash=<hash> waypoint-api
```

The compose stack that wires this image together with Postgres, Keycloak (M3+), and
nginx lives under [`deploy/`](../deploy/).

### Container health — the convention for every Waypoint service image

**The image owns how it reports health; the orchestrator only invokes it.** Every service
image Waypoint ships must expose a health probe executable with *nothing but what the
image already contains*, declare it as a `HEALTHCHECK` in its own Dockerfile, and let
compose (or any other orchestrator) call that same probe.

The reason is concrete rather than stylistic. `mcr.microsoft.com/dotnet/aspnet` — like
most modern slim runtime bases — ships **neither `curl` nor `wget`**. A compose-side
`test: ["CMD", "wget", ...]` therefore fails *every* probe, the container never becomes
healthy, and anything gated on `depends_on: condition: service_healthy` (nginx, here)
never starts at all. The two ways out are to install an HTTP client into the runtime
image — paying image size and attack surface on every service, forever — or to let the
application answer its own probe. Waypoint does the latter.

For `Waypoint.Api` that mechanism is a health-check mode on the app itself:

```bash
dotnet Waypoint.Api.dll --health-check   # exit 0 = healthy, exit 1 = unhealthy
```

It performs a loopback `GET /api/v1/health`, requires a `200` whose payload reports
`"status": "ok"`, and never throws — any failure is an unhealthy verdict. The URL is
derived from `ASPNETCORE_URLS` (wildcard binds such as `http://+:8080` are rewritten to
loopback; `https://` entries are skipped, so the probe never needs a trusted dev
certificate), falling back to `http://127.0.0.1:8080/api/v1/health`. Set
`WAYPOINT_HEALTHCHECK_URL` to override it outright. Implementation:
`Waypoint.Api/Diagnostics/HealthCheckProbe.cs`.

**Applying this to the next service image:**

1. Give the app a self-contained probe mode — an argument, a subcommand, or a tool the
   image already carries. Do **not** add `curl`/`wget` to a runtime image just to satisfy
   a healthcheck.
2. Declare `HEALTHCHECK` in that service's own Dockerfile, so plain `docker run` and every
   orchestrator get the correct behaviour by default.
3. In compose, either omit `healthcheck:` (inheriting the image's) or restate the *same*
   command. Never invent an orchestrator-side probe the image cannot execute.
4. Cover it with a test that asserts the **exit code** of the real binary — that is the
   only thing Docker observes (`Waypoint.Tests/Api/HealthCheckProbeTests.cs`).

### Startup failure exits non-zero

`Program.cs` catches a fatal startup exception, logs it, and returns exit code **1**.
This is load-bearing: `restart: on-failure`, compose's health gating and any CI step that
reads `$?` all treat exit 0 as "the process did its job and stopped", so a backend that
cannot bind its port must never report success. `StartupExitCodeTests` pins both
directions (unbindable URL → non-zero; valid URL → keeps running).

## Conventions this scaffold establishes

Everything below implements `docs/api-contract.md` Conventions and is meant to be
reused, not reinvented, by every future endpoint:

- **Errors**: throw `Waypoint.Core.Errors.ApiException` (or a subclass/factory such as
  `ApiException.NotFound()`); `Waypoint.Api.Middleware.ErrorHandlingMiddleware` turns it
  into `{ "error": { "code", "message", "detail?" } }`. Auth failures and unmatched
  routes get the same envelope via the `UseStatusCodePages` handler in `Program.cs` —
  no controller code needed for 401/403/404. Model-state 400s (missing field, malformed
  JSON body, mistyped query parameter) go through
  `Waypoint.Api.Validation.ValidationErrorFactory`, installed as
  `ApiBehaviorOptions.InvalidModelStateResponseFactory`, so `[ApiController]` emits
  `validation_error` in the same envelope instead of RFC 7807 camelCase `ProblemDetails`.
  All three paths share `ErrorEnvelopeWriter` — do not hand-roll a fourth.
- **JSON**: `Waypoint.Core.Serialization.WaypointJsonOptions` is the single source of
  snake_case naming, applied to both MVC's serializer and any hand-written JSON in
  middleware. Use it in tests too (`ReadFromJsonAsync(..., WaypointJsonOptions.Default)`).
- **Roles**: decorate an endpoint with `[RequireViewerRole]` / `[RequireCyberRole]` /
  `[RequireOperatorRole]` / `[RequireAdminRole]` (`Waypoint.Core.Authorization`) —
  each requires that role or higher, per the Viewer < Cyber < Operator < Admin hierarchy
  in `docs/domain-model.md`.
- **Pagination**: accept `Waypoint.Core.Pagination.PageRequest` as a `[FromQuery]`
  parameter and set the `X-Total-Count` response header yourself (see
  `CatalogController.ListArtifacts` for the pattern) — there is no generic list wrapper
  because each resource's collection endpoint composes its own query.
- **Mode gating**: `ApiException.ModeUnavailable()` is reserved for endpoints that exist
  but cannot function in the appliance's current mode (409, per the contract) — not yet
  called by any endpoint; wire it in as connected/disconnected-mode features land
  (ADR-0010).
- **Logging**: `Waypoint.Core.Logging.ISecretRedactor` is the log-scrubbing hook point
  `docs/security.md` control 1 requires from the start. `Program.cs` routes every
  Serilog console line through it already; the registered implementation is a no-op
  until issue #6 supplies the real scrubber — nothing downstream needs to change when it
  does.
- **Local auth is a seam, not the design.** `Waypoint.Core.Auth.ILocalAuthenticationService`
  is what issue #29 replaces with Keycloak OIDC validation; don't add call sites that
  depend on the in-memory implementation directly.
- **License headers**: every `.cs` file carries the Apache-2.0 boilerplate from the
  `LICENSE` appendix (`Copyright 2026 Justin Black`, one punctuation deviation from the
  verbatim appendix text noted in `.editorconfig` — a tooling limitation, not a license
  change). This is mechanically enforced, not just documented: `dotnet build` flags a
  missing/mismatched header as `IDE0073`, and `dotnet format` inserts the correct one.
