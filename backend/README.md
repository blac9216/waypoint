# backend

ASP.NET Core (C#) application — REST API, job engine, PowerShell runspace hosting, SSE
streaming. See [ADR-0006](../docs/adr/0006-backend-language.md) and
[ADR-0008](../docs/adr/0008-job-engine.md).

## Solution layout

| Project | Purpose |
|---|---|
| `Waypoint.Api` | ASP.NET Core host: controllers, middleware, authentication, DI composition root (`Program.cs`). |
| `Waypoint.Core` | Domain layer: roles/authorization, the error envelope, pagination, configuration option types, the local-auth and log-redaction abstractions. No ASP.NET Core hosting dependency beyond the lightweight `Microsoft.AspNetCore.Authorization` package. |
| `Waypoint.Infrastructure` | DI wiring for the abstractions `Waypoint.Core` declares (today: the in-memory local-auth implementation). EF Core / Postgres access lands with the schema in issue #4; PowerShell runspace hosting lands with the job engine. |
| `Waypoint.Tests` | xUnit — unit tests for `Core`/`Infrastructure` plus `WebApplicationFactory`-based integration tests against the real HTTP pipeline. |

## Build and test

```bash
cd backend
dotnet build Waypoint.sln
dotnet test Waypoint.sln
```

## Run locally

Local auth (ADR-0004 rollout note) is a **dev-grade single admin user** — there is no
compiled-in default password. Set `LocalAuth__AdminPasswordHash` (SHA-256 hex digest of
the password you choose) before the API will accept any login; with it unset, login
always fails closed:

```bash
# Compute the hash for a password of your choosing — never commit the plaintext or the hash.
printf '<your-dev-password>' | sha256sum | awk '{print $1}'

cd backend
export LocalAuth__AdminPasswordHash=<hash from above>
dotnet run --project Waypoint.Api
# GET  http://localhost:5000/api/v1/health         (no auth)
# POST http://localhost:5000/api/v1/auth/login      { "username": "admin", "password": "<your-dev-password>" }
```

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
nginx lives under [`deploy/`](../deploy/) (separate issue).

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
  `ScaffoldStubController` for the pattern) — there is no generic list wrapper because
  each resource's collection endpoint composes its own query.
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

`Waypoint.Api.Controllers.ScaffoldStubController` exists only to exercise the
conventions above end-to-end (see its XML doc comment) — delete it once a real
paginated, role-guarded resource exists to demonstrate the same shapes in production
code.
