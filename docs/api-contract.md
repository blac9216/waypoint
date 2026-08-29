# Waypoint — API Contract & Data Ledger (M0 output)

Status: **draft v1 — closes milestone M0**. Derived from the UI prototype
(`ui/prototype/`) reconciled against `domain-model.md`. This is the contract the M1
backend implements and the frontend consumes; refine it in PRs, don't fork it in code.
Endpoint shapes are planning-grade — field lists name the load-bearing data, not every
column.

## Conventions

- Base path `/api/v1`, JSON bodies, `snake_case` fields.
- **Auth**: OIDC bearer token (Keycloak; local-auth issuer in M1 — ADR-0004). Role
  claims map to Viewer / Cyber / Operator / Admin — see `### Auth` below for the M1
  local-auth endpoints and the exact (PascalCase) wire values — the API enforces role
  guards server-side (the UI's disabled-with-reason treatment is presentation only).
- **Mode**: every response is mode-aware; endpoints unavailable in the instance mode
  return `409 mode_unavailable` (they exist but cannot function), never `404`.
- **Errors**: `{ "error": { "code", "message", "detail?" } }`. Selected stable codes
  that apply across endpoints (per-endpoint codes are noted inline in the resource
  tables below):

  | Code | Status | Meaning |
  |---|---|---|
  | `master_key_unavailable` | 503 | The secrets master key (ADR-0005) is not mounted, unreadable, or malformed — any secret-bearing write or decrypt (e.g. `POST`/`PUT /credentials` with a `secret`) fails closed. An **operator** misconfiguration, not a client error or a transient fault: 503 (not 500) signals "appliance not fully configured, retry after remediation" rather than "something crashed." The response body is deliberately generic — it never echoes the configured key file path (server filesystem layout, `security.md` control 1); the detailed `MasterKeyUnavailableException` message (env var, path, expected format) is logged server-side only. See `deploy/README.md`, "Production only: secrets master key". |
- **Pagination**: `?limit/offset` + `X-Total-Count` on list endpoints.
- **Write-only secrets**: stored credential material (and any secret held in the
  credential store) appears in requests, never in responses (enforced at the
  serialization layer — `security.md` control 3). The issued session/bearer token is
  an explicit exception — see `### Auth`.
- Long-running operations return `202` with a `run_id`/`job_id` and progress flows
  through the event stream, not polling.

### RBAC summary (reconciled, epic #726)

Full rationale and residual-risk discussion lives in `security.md`'s "RBAC
reconciliation" section; this table is the wire-facing floor per action family. It
narrows/clarifies, never widens, the roles in `domain-model.md`'s Roles table.

| Action family | Viewer | Cyber | Operator | Admin |
|---|---|---|---|---|
| Read dashboards/runs/results/plans/attempts | ✅ | ✅ | ✅ | ✅ |
| Initiate an interactive scan (arbitrary subset) | — | ✅ | ✅ | ✅ |
| Control (pause/resume/abort/cancel/retry/repair-credential) a scan **the caller initiated** | — | ✅ | ✅ | ✅ |
| Control any scan regardless of initiator | — | — | — | ✅ |
| Interactive saved-credential override | — | ✅ | ✅ | ✅ |
| Interactive ad hoc (personal) credential | — | — | ✅ | ✅ |
| Manage recurring scan schedules | — | — | — | ✅ |
| Content: review/diff/approve changed or unknown controls | — | ✅ | ✅ | ✅ |
| Content: activate/roll back a baseline; waive a candidate test | — | — | — | ✅ |
| Trust bundles / scoped TLS bypass | — | — | — | ✅ |
| Temporary SSH enablement / reconcile | — | — | — | ✅ |
| Target/component persistent configuration (bindings, `configured_fact`, purge) | — | — | — | ✅ |
| Retention policy / graph purge | — | — | — | ✅ |
| Alert acknowledgement | — | — | — | ✅ |

This scan-specific "Cyber+/Operator+ control what they initiated" rule is narrow: it
does not widen any non-scan job family's authority (e.g. it grants nothing toward
`download`/`bundle-import`/`update` control), matching ADR-0022/epic #726's explicit
"without widening non-scan job authority."

## Resources

### Auth
| Endpoint | Methods | Notes |
|---|---|---|
| `/auth/login` | POST | Anonymous. Local-auth login (ADR-0004 rollout note — dev-grade stand-in). Issue #29 made this **dev-flag-only** (`LocalAuth:Enabled`, off by default): production sign-in goes through Keycloak's own OIDC authorization-code/token endpoints, which this backend never proxies (ADR-0004 — the app stays a plain OIDC relying party). When the flag is off, `POST /auth/login` answers `404 not_found`. When on: request `{username, password}`, 200 → `{token, role, expires_at}` (no `user` object), 401 → `{ "error": { "code": "invalid_credentials", "message": "Invalid username or password." } }`, and — issue #505 — a warm-up window where the auth backend cannot yet evaluate credentials (e.g. the admin password hash has not finished resolving) answers `503 auth_not_ready` instead of the misleading 401. |
| `/auth/me` | GET | Viewer+. Current session's identity: `{username, role}`. Unaffected by the #29 Keycloak swap — same shape regardless of which issuer authenticated the caller. |

**Session token.** Local auth (when enabled) presents an opaque bearer string; a
Keycloak-authenticated caller presents a real JWT. Both ride `Authorization: Bearer
<token>` — callers don't need to know which issuer minted it, and the server routes
each request to the right validator by token shape (a JWT is three dot-separated
segments; the local token never is). There is no `/auth/logout`: discarding the
client-held token ends the session client-side, and the token also expires
server-side regardless — `expires_at` for local auth (default session lifetime: 8
hours from issue), the token's own `exp` claim for Keycloak. Refreshing an OIDC
session is the frontend's normal OIDC-client concern (silent renew / refresh token),
not a Waypoint-specific endpoint; local auth has no refresh endpoint at all — an
expired local-auth token requires logging in again.

**`role` value casing — read before touching this.** `role` is serialized via the
backend's `Role.ToString()`, so its value is **PascalCase**, while every *key* in this
API is `snake_case` (`expires_at`, not `ExpiresAt`). This is a deliberate, narrow
exception — snake_case governs field *names* here, not the literal values of an enum
— not an inconsistency to "fix" later. The closed set, matching `domain-model.md`'s
Roles in the same strictly-increasing order, is exactly:

```
Viewer | Cyber | Operator | Admin
```

Do not lowercase these client-side and do not compare against `"admin"` — the values
above are the entire domain; any other string on the wire is invalid. This casing
applies everywhere a role appears (`/auth/login`, `/auth/me`, and any future
role-bearing field), not just these two endpoints.

**Clients validate `role` at the wire seam and fail closed.** Because the set is
closed, a `role` that is not one of the four is a contract violation, and a client
must refuse it rather than half-accept it — a client that signs in on an unrecognized
role reaches a state where every role guard denies while the session claims to be
valid. `role` is validated on **both** endpoints, not just one: `/auth/login` and
`/auth/me` each assert a role for the same token, so they must agree, and a
divergence is refused too (neither is treated as overriding the other). This is a
requirement on the client, not a second server-side guard — the API remains the
enforcement point for every actual permission (see Conventions).

The same holds for the other fields these endpoints return: `token` and `username`
must be present and non-empty, and `expires_at` must parse to a real instant. A
client that accepts a response missing any of them builds a session that looks valid
and comes apart later — an empty token authenticates nothing until some request
`401`s, and an unparseable `expires_at` compares false against every expiry check, so
the session survives login only to be rejected on the next restore. Reject the whole
response instead. This is a rule about *responses*; how a client validates a session
it has already stored is its own business.

**What #29 (Keycloak) changed, and what it didn't.** Production sign-in is now
Keycloak's own OIDC authorization-code/token endpoints — this backend never proxies
them (ADR-0004: plain OIDC relying party) and never issues its own login page or
token. `POST /auth/login` survives only as the dev-flag-only local-auth path
described above; its path, request body, and response shape were never part of the
durable contract and remain out of it. `GET /auth/me` and the role guards it feeds
(the client-side disabled-with-reason treatment plus every server-side enforcement
point) are exactly what was expected to survive the swap unchanged, and did:
`/auth/me` keeps returning `{username, role}` no matter which issuer authenticated
the caller, and role claims keep mapping to the same four
`Viewer`/`Cyber`/`Operator`/`Admin` values (Keycloak realm group membership → the
`role` claim → `WaypointClaimTypes.Role` — see
`deploy/keycloak/realm/waypoint-realm.json` and `OidcClaimsMappingOptionsSetup`).

**`GET /auth/config`** (issue #534): anonymous. `{local_auth_enabled, oidc_authority,
oidc_client_id}` — how the SPA feature-detects the dev-flag local-auth form
(`local_auth_enabled`, mirrors `LocalAuth:Enabled`) and learns the browser-facing
OIDC authority/public client id it needs to build the authorization-code redirect,
without hardcoding either. `oidc_authority` is deliberately **not** the backend's
own `Oidc:Authority` (that is the backend's container-network view of Keycloak,
unreachable from a browser) — it is `Oidc:PublicAuthority`, a same-origin relative
path by default (`/auth/realms/waypoint`, routed through nginx's `/auth/` proxy to
Keycloak) so one image works on any operator's hostname, including an air-gapped
instance with no fixed public hostname at all.

**Frontend OIDC flow (issue #534).** The SPA drives a real authorization-code + PKCE
(RFC 7636, `S256`) redirect against Keycloak's own `/authorize` and token endpoints
(discovered from `oidc_authority` via the standard
`{authority}/.well-known/openid-configuration` document) — hand-rolled with
`fetch`/`crypto.subtle` (`frontend/src/lib/oidc.ts`), no external OIDC library, so the
air-gapped build stays free of runtime CDN dependencies. It authenticates as the
realm's public client (`waypoint-frontend` — `publicClient: true`, no secret, PKCE
required — distinct from the backend's confidential `waypoint-backend` client), whose
tokens still carry `aud: waypoint-backend` (an audience protocol mapper) so this
backend's `Oidc:Audience` check needs no change. The callback lands at the SPA's own
`/oidc/callback` route (deliberately outside nginx's `/auth/` prefix, which proxies
straight to Keycloak and would never reach the React app). Step-up re-authentication
(`docs/security.md`, issue #521) reuses the same redirect with `prompt=login`: a
`403 step_up_required` from a gated write (e.g. `PUT /credentials/{id}` overwriting
secret material) triggers the redirect, and the original request is retried once the
callback completes with a fresh `auth_time`. Logout uses Keycloak's RP-Initiated
Logout (`end_session_endpoint` from the same discovery document) so the browser's
Keycloak SSO cookie ends too, not just the SPA's own session state — otherwise the
next sign-in would silently reuse the still-live Keycloak session.

### Sites, targets, inventory
| Endpoint | Methods | Notes |
|---|---|---|
| `/sites` · `/sites/{id}` | GET, POST, PUT, DELETE | Admin writes. Site: name, description, stigman_override?. |
| `/sites/{id}/targets` · `/targets/{id}` | GET, POST, PUT, DELETE | kind (`vsphere`\|`nsx-api`\|`ssh`), connection.host, credential_ref, discovery_status, last_refreshed. |
| `/targets/{id}/inventory` | GET | Cached hosts/VMs tree (cluster → host → vm), plus a top-level `vcenter` row for the appliance itself (issue #1081), build info, semantic version (issue #974, host and vcenter rows only), maintenance_mode, `instance_uuid` (issue #1063, vm rows only — vSphere's authoritative instance UUID, deconflicts identically named VMs). |
| `/targets/{id}/discover` | POST | 202 → `discover` job. |

🚧 **Planned cached component inventory (epic #726, [ADR-0023](adr/0023-compliance-inventory-and-immutable-plans.md)).**
`/targets/{id}/inventory` supersedes the flat cluster/host/VM tree above with a
component-identity collection once #732–#734 land. Every row is a stable
`Component`: `component_id` (opaque, identity is `(target_id, catalog_component_key,
authoritative vendor identity)` — never hostname/IP/display-name/tree-position),
`catalog_component_key`, `parent_component_id?`, `lifecycle`
(`active`\|`absent`\|`retired`), `configured_fact`/`discovered_fact` (each an
independent, timestamped exact-version/capability observation, optionally carrying a
`build` alongside the mandatory `exact_version` — issue #1081, e.g. an esxi host or the
vcenter root; both present, one, or neither — and a `derived_from_parent` bool, issue
#1063: true only for a VM's discovered fact, copied from its managing vCenter's own
fact rather than independently observed; false for every directly observed fact, so
provenance is never presented as observed when it was actually derived), `fact_conflict` (bool — true when
configured and discovered disagree; never silently resolved by the API), `first_seen_at`, `last_seen_at`,
`continuous_absence_since?`, and `baseline_ready` (bool — one exact catalog
product-version entry plus exactly one active approved baseline under ADR-0022;
`false` is not an error, it is a readiness fact the plan preview reads). Retired
components remain listed (`lifecycle: "retired"`) until an explicit purge; they are
never silently dropped from this collection, only excluded from `all`-scope
expansion.

| Endpoint | Methods | Notes |
|---|---|---|
| `/targets/{id}/components` | GET, POST | GET: superset of `/targets/{id}/inventory`'s planned shape: every known component beneath this target regardless of lifecycle, for Configuration-screen visibility (vs. the scan-scoped view below). POST (Admin, issue #743): declares the ROOT component for a target kind with no discovery operation — today only `ssh` (whole-appliance SRG products). Body: `{ catalog_component_key, exact_version? }`. The key is validated fail-closed against the catalog's closed `ssh`/`target` shape (400 `unknown_catalog_component_key` otherwise — generic SSH never guesses a product); `exact_version`, when supplied, flows through the same configured-fact/linkage path as `PUT /components/{id}`. 400 `declared_component_unsupported_target_kind` for discoverable kinds; 409 `component_exists` when the key is already declared under the target. |
| `/components/{id}` | GET, PUT | GET: full component record incl. observation history summary. PUT (Admin): `configured_fact` only (exact product version/capability Waypoint cannot discover) — never lifecycle or identity, which are discovery/refresh-owned. Setting `exact_version` resolves catalog linkage at write time through the shared linkage resolver (issue #1000; same exact-match / ambiguity-fail-closed semantics as discovery). An empty or whitespace `exact_version` is an explicit CLEAR (not a 400): the configured fact is removed and linkage honestly re-resolves from whatever facts remain — unlinking when nothing remains. |
| `/components/{id}/observations` | GET | `ComponentObservation[]` — immutable discovery provenance: `discovery_refresh_id`, `observed_fact`, `observed_at`, `outcome`, raw-evidence digest reference. Audit/troubleshooting read, Cyber+. |
| `/components/{id}` | DELETE | Admin-only, audited purge. 409 `component_not_retired` unless `lifecycle == "retired"`. Removes retained configuration; historical `PlannedComponentItem` references survive unaffected (they hold a frozen identity snapshot, not a live FK the purge could cascade). |
| `/discovery-refreshes` | GET | `DiscoveryRefresh[]`: `trigger` (`scheduled`\|`pre-scan`\|`manual`), `target_id`/boundary, `started_at`/`completed_at`, `outcome` (`complete`\|`partial`\|`failed`), boundaries reconciled. Backs the discovery-failure alert's "why" drill-down (see Alerts below). |
| `/targets/{id}/discovery-schedule` | GET, PUT | Admin. Per-target override of the appliance-wide daily discovery schedule; `null` body reverts to inherited default. Distinct from `/schedules` (scan schedules) — this is discovery cadence only. |
| `/system/discovery-schedule` | GET, PUT | Admin. The one appliance-wide default (initially daily) every target inherits absent its own override. |

Version-conflict resolution (`fact_conflict: true`) has no dedicated write endpoint:
an interactive Cyber+ initiator's choice is made and frozen at `POST /runs`
plan-preview time (see Compliance plans below), never by mutating the component
record — ADR-0023 is explicit that the choice "mutates neither source." A scheduled
run that hits a conflicted component cannot choose; it skips that component as a
`CoverageOmission` and the conflict remains visible here for an Admin/Cyber to
resolve out-of-band (e.g. correcting the `configured_fact` via `PUT /components/{id}`).

**Shipped by #584/#585/#586, remaining for #587** ([ADR-0021](adr/0021-credential-purpose-matrix.md)):
#584 shipped the per-`(target, purpose)` binding surface —
`PUT`/`DELETE /targets/{id}/credential-bindings/{purpose}` (Admin, purpose
applicability and credential-type compatibility validated against the shared matrix;
machine-readable `invalid_purpose`/`purpose_not_applicable`/
`incompatible_credential_type`/`credential_not_found` errors) and a `bindings` array on
`TargetResponse`; `credential_ref` stays on the wire as the deprecated single-value
mirror of the kind's default purpose (migration 0043's dual-write). #585 shipped
execution resolution: those bindings (plus validated `POST /runs`
`credential_overrides`) snapshot into immutable per-job `job_credential_bindings` rows
at run creation — see the Runs section. #586 shipped the per-target/per-purpose **ad
hoc** tier: `POST /runs` optional `ad_hoc_credentials` (`[{ target_id, purpose,
username, secret }]`, Operator+, scan runs only), each an inline personal credential for
exactly one `(target, purpose)` pair, stored envelope-encrypted in `run_secrets` keyed
by `(run, target, purpose)` (migration 0045 re-keyed `run_secrets` from one row per run
to one row per key) — never a `credentials`/`credential_secrets` row (ADR-0011). Ad hoc
takes precedence over a saved `credential_overrides` entry for the same pair (naming
both is a 400); the flat legacy `credential` tier (one shared secret for the whole run)
remains and is mutually exclusive with `ad_hoc_credentials`. The wizard UI defaulting to
assigned credentials is #587.

🚧 **Superseded precedence order (epic #726, [ADR-0024](adr/0024-compliance-execution-attempts-credentials-and-settings.md)).**
The paragraph above describes the shipped `(target, purpose)`-only binding model.
ADR-0024 supersedes ADR-0021 §§4–7 (target-only defaulting, whole-run missing-binding
rejection, schedule-carried overrides) once #735–#737 land. The end-state precedence,
most-specific first:

1. **Interactive run override** — a compatible saved `credential_overrides` entry
   (Cyber+) or ADR-0016 personal `ad_hoc_credentials` entry (Operator+), scoped to
   `(component, purpose)` rather than `(target, purpose)` once components exist as a
   first-class identity.
2. **Component/purpose service binding** — `PUT /components/{id}/credential-bindings/{purpose}`
   (planned, mirrors the shipped target-level endpoint's shape and error codes),
   Admin-only.
3. **Top-level target/purpose service binding** — the shipped
   `PUT /targets/{id}/credential-bindings/{purpose}` endpoint, unchanged.

**Scheduled runs resolve only layers 2 and 3** — component then target service
bindings — and never carry an interactive/ad hoc override; a schedule payload that
includes `credential_overrides` or `ad_hoc_credentials` is rejected at
`POST /schedules` (400 `validation_error`), not silently ignored at dispatch. This
replaces the shipped "same as interactive, at fire time" schedule behavior
(ADR-0021 §7) rather than leaving both rules standing.

**Missing-binding behavior is superseded from whole-run rejection to per-component
readiness failure.** The shipped `credential_binding_gaps` 400 (whole request
rejected before any row exists) applies only while runs are target-granular. Once
component jobs exist (#735–#737), a missing/incompatible/ambiguous purpose binding
for one component affects only that component's job: it remains visible with zero
attempts and a safe, non-secret readiness reason (naming the component and purpose,
never credential material); independent components' jobs continue; the run
completes as incomplete rather than being rejected outright. `POST /runs` therefore
stops returning `credential_binding_gaps` as a whole-request 400 for scope-level
gaps once this lands — a request-shape error (duplicate override, out-of-scope
target) is still a 400, but a resolvable-at-plan-time credential gap becomes a
per-component `CoverageOmission`/readiness-failed job instead.

**Audited credential repair** (planned, #735–#737): `POST
/jobs/{id}/repair-credential` (Cyber+, own runs; Admin any), body
`{ purpose, credential_id }` or `{ purpose, ad_hoc: { username, secret } }`. Valid
only for a component job in a readiness-failed or `auth-failed` state whose gap
matches the named purpose. Replaces that job's failed `(component, purpose)` binding
for its *next* attempt only — it changes access alone: it cannot alter the planned
component, scope, baseline, closure, selector, transport, control settings, trust
policy, or output semantics (ADR-0024). Records old/new non-secret attribution plus
actor/time/reason in `audit_log`. 409 `job_not_repairable` if the job's readiness
gap does not match the named purpose or the job is not in a repairable state; 400
`incompatible_credential_type` per the shared purpose-compatibility matrix. This
supersedes `/runs/{id}/resume-blocked`'s whole-run credential swap for compliance
runs specifically — `resume-blocked` remains the mechanism for non-compliance job
families that still halt at the run/queue level.

### Credentials (service/shared only — ADR-0011)
| Endpoint | Methods | Notes |
|---|---|---|
| `/credentials` · `/credentials/{id}` | GET, POST, PUT, DELETE | Admin writes. Metadata out: name, type, username?, used_by_count, rotated_at, last_tested_at?, expires_at?, health (`valid`\|`auth_failing`). `username` is the protocol-level login (e.g. `administrator@vcenter-sso-domain`) a connection-type (vcenter/nsx/ssh) credential's job handler presents — distinct from `name`, which is only ever a human-facing label; not secret material, so it round-trips in responses (issue #262). `last_tested_at` (issue #560) is stamped by every `credential-test` outcome, success or failure, any type. `expires_at` (issue #560) is `null` until a real upstream response supplies an expiry — never fabricated; `null` means "unknown," not "no expiry." Secret material in only. Issue #521: a `PUT` that sets `secret` (overwriting existing key material) additionally requires step-up re-authentication — a token whose `auth_time` is missing or older than the configured freshness window answers `403 { "error": { "code": "step_up_required", ... } }` instead of writing anything; renaming or flipping `sudo_enabled` alone is never gated. See `docs/security.md` "Step-up re-authentication" for the full mechanism (frontend re-auth redirect + retry tracked separately, issue #534). |
| `/credentials/{id}/test` | POST | Connectivity check; 202 → job. |

### Runs & jobs
| Endpoint | Methods | Notes |
|---|---|---|
| `/runs/history` | GET | Issue #708/#689 (epic #706): filtered, keyset-cursor-paged run list — the global Jobs workspace's History mode. Viewer+, same floor as every other run read (ADR-0019 decision 6). Query params: `state` (comma-separated allow-list of `runs.state`), `run_type` (comma-separated allow-list of `runs.run_type`), `since`/`until` (ISO-8601, inclusive bounds on `created_at`), `cursor` (opaque, from a previous response's `next_cursor`), `limit` (1–200, default 50). No filter is applied by default — including no implicit "terminal only" filter; a caller browsing "history" passes `state=completed,completed_with_failures,aborted` explicitly, same as every other filter here. An unrecognized `state`/`run_type` value or an unparseable `since`/`until` or garbage `cursor` is 400 `validation_error`, never a 500. Response body: `items` (`RunResponse[]`, same shape `GET /runs` and `GET /runs/{id}` return) and `next_cursor` (opaque, present only when the page was truncated by `limit` with more matching rows remaining — never a silent truncation). Cursor wraps `(created_at, id)` — unlike `/runs/{id}/events/history`'s single-column `job_events.seq` cursor, `runs.created_at` is not unique, so the tie-break column (matching `ORDER BY created_at DESC, id DESC`) travels in the cursor too. A route distinct from `GET /runs` (rather than overloading its `?limit/offset` contract) so the Live Jobs workspace's existing active-work list (#590) is untouched. |
| `/runs` | GET, POST | POST body: `run_type`, `scope` (JSON string — a scan run's `scope.site_id` is always required; `scope.profile_id` is required ONLY for a legacy request with no `scope.target_scope`, optional `target_ids`), `credential_id` \| inline `credential` (personal tier, ADR-0011 — never persisted), `confirmation` (remediate only). Cyber+ for scans; remediation POSTs require Admin + `confirmation: "REMEDIATE"`. 202 body: `run_id`. Issue #639: for that legacy (no `target_scope`) shape, `scope.profile_id` selects which pulled compliance-content profile (`profiles.id`, `GET /profiles`) the scan executes — must reference an installed profile or the request 404s/400s (missing entirely is a 400 `validation_error`; an unknown id is a 404 `not_found`); the run persists it in `scope` so run history shows what was actually scanned. **Issue #895: a `scope.target_scope` request instead REJECTS `scope.profile_id` outright (400 `validation_error`)** — matching `/runs/plan-preview`'s rule below (ADR-0022 §7 "Start a Scan ... never selects a profile") so the wizard's preview→create handoff can reuse one `scope` payload; a `target_scope` run resolves its execution content from each accepted plan item's own catalog execution profile (the component's active baseline) instead. Issue #585 (ADR-0021): optional `credential_overrides` (scan runs only, mutually exclusive with inline `credential`): `[{ target_id, purpose, credential_id }]`, each substituting a stored credential for exactly one (target, purpose) pair. Issue #586 (ADR-0021): optional `ad_hoc_credentials` (scan runs only, Operator+, mutually exclusive with inline `credential`): `[{ target_id, purpose, username, secret }]`, each an inline personal credential for exactly one (target, purpose) pair — encrypted at rest as its own `run_secrets` row keyed by `(run, target, purpose)`, never a stored `credentials` row. Ad hoc takes precedence over `credential_overrides` for the same pair; naming the same `(target_id, purpose)` in both, or twice within `ad_hoc_credentials` itself, is a 400. At creation the API resolves every purpose each selected target's scan requires (shared `CredentialPurposeMatrix`) from `ad_hoc_credentials` first, then `credential_overrides`, then the target's own `credential-bindings`, then the legacy run-level `credential_id` (now reinterpreted as a type-checked override of each target's default purpose) — then snapshots the result as immutable per-job `job_credential_bindings` rows (later target/binding edits never change an in-flight run; an ad hoc-resolved purpose sets `is_run_secret: true` on its snapshot row instead of a `credential_id`). Any missing/incompatible/out-of-scope pair rejects the whole request with a 400 `credential_binding_gaps` whose `error.binding_gaps` array enumerates every `{ target_id, target_name, purpose, reason, credential_id? }` (`reason` ∈ `missing_binding`, `incompatible_credential_type`, `credential_not_found`, `target_not_in_scope`, `purpose_not_applicable`, `duplicate_override`) before any run/job row exists. |

**Interim additive `scope.target_scope` (issue #733, epic #726 Wave 2, ADR-0023) —
NOT yet the end-state contract below.** A scan-run POST may additionally set
`scope.target_scope`: `{ "mode": "all" | "explicit", "target_ids"?: [...],
"component_ids"?: [...] }`, resolved against the merged stable-component model (PR
#839) exactly as the end-state `target_scope` below describes — tri-state parent
resolution into explicit stable identities, ownership/lifecycle/fact-conflict/catalog-
compatibility validation, and an empty `explicit` selection resolves to zero
components rather than widening to the whole site. An invalid `mode` is 400
`validation_error`; a non-empty request that resolves to zero runnable components is
400 `no_runnable_component` with `error.scope_omissions` (`[{ component_id?,
target_id?, reason, detail }]`, `reason` ∈ `component_not_found`,
`component_not_in_scope`, `component_absent`, `component_retired`, `fact_conflict`,
`catalog_incompatible`, `target_not_found`) enumerating why. The requested
`target_scope` and its resolved component set/omissions are frozen into
`run_scope_snapshots` (migration 0056) the instant the run row is created, readable
per run for history/audit — the "requested versus resolved scope" AC this issue
closes. **Deliberately NOT yet wired in this slice:** `target_scope` does not replace
`target_ids` (both shapes still resolve targets independently), it is not exposed
by the Start-a-Scan wizard yet (frontend remainder), and it does not integrate with
`/runs/plan-preview`'s mandatory discovery refresh (also a stated remainder — an
interactive `fact_conflict` is always an omission in this slice, never resolved, since
there is no plan-preview step yet for a Cyber+ initiator to choose from). **Issue #895
correction:** `profile_id` does NOT coexist with `target_scope` on a single request —
see the `/runs` row above; a `target_scope` request always rejects `profile_id`, it
never merely ignores it.

✅ **`POST /api/v1/runs/plan-preview` shipped (issues #733/#734 remainder).** Cyber+.
Body: `{ scope: { site_id, target_scope } }` (`target_scope` required — preview never
selects a profile, so a bare `profile_id` is 400 `validation_error`), plus optional
`credential_overrides`/`ad_hoc_credentials` keyed by `(target_id, purpose)` (this slice's
shipped per-target/per-purpose shape, not yet the end-state `(component_id, purpose)`
keying the table row below describes). Runs the identical resolve→compile pipeline
`POST /runs` uses (`ScopeResolutionService` then `ScanPlannerService`, both already
side-effect-free) entirely in memory and returns the would-be plan — resolved component
ids, scope omissions, accepted plan items, skips (including any item demoted by an
unresolved required credential purpose), `plan_digest`, and `credential_gaps` — with
**zero rows written**: no run, no `run_scope_snapshots`, no `scan_plans`/
`scan_plan_items`, no `job_credential_bindings`. `plan_digest` is byte-for-byte identical
to a subsequent `POST /runs` create's digest for the same inputs (issue #734 AC-4,
test-proven). Zero-runnable-component previews are 200, not an error, matching the
end-state row below. **Not yet in this slice** (tracked by the remainders the end-state
row and paragraph above already name): preview does not itself trigger a mandatory
discovery refresh (it plans against already-discovered inventory, same as `POST /runs`
today), `fact_conflict` resolution (`fact_conflict_resolutions`) does not exist yet —
a conflicted component is always an omission — and the Start-a-Scan wizard does not call
this endpoint yet (frontend remainder).

🚧 **Superseded scan-creation contract (epic #726, ADRs 0022–0024).** The `/runs` POST
row above — `scope.profile_id` selecting an installed profile, and target-granular
`job_credential_bindings` — describes the shipped M2/M3 scan model. It is retained on
the wire only for the one legacy-migration window ADR-0025 describes (see Legacy
scan migration below) and is never dual-written alongside the model that follows.
Once #732–#737 land, a scan run's `scope` no longer accepts `profile_id`: the caller
never selects a profile (ADR-0022, "the scan wizard never chooses profiles"). The
end-state shape:

`scope` becomes `{ site_id, target_scope }` where `target_scope` is exactly one of
`{ "mode": "all", "target_ids": [...] }` (expand every compatible component beneath
the named top-level targets after mandatory refresh) or
`{ "mode": "explicit", "component_ids": [...] }` (the exact stable-component set —
never widens). A request naming both modes, or `profile_id` at all, is 400
`validation_error`. `POST /runs` (scan) becomes a two-step flow:

| Endpoint | Methods | Notes |
|---|---|---|
| `/runs/plan-preview` | POST | Cyber+. Body: `{ site_id, target_scope }` plus optional `credential_overrides`/`ad_hoc_credentials` keyed by `(component_id, purpose)`. Runs the mandatory pre-scan refresh and readiness evaluation **without creating a run**; returns the would-be `CompliancePlan` preview: resolved component set, per-component readiness (`ready`\|`coverage_omission` with reason), any `fact_conflict` requiring caller resolution, and required-purpose credential coverage. This is what the Start-a-Scan wizard renders before the caller confirms — it never selects a profile, only assets (ADR-0022/§7 "Start a Scan ... never selects a profile"). Zero-runnable-component previews are still 200 (an honest empty plan), not an error; the caller decides whether to proceed. |
| `/runs` | POST (scan) | Body: `{ run_type: "scan", scope: { site_id, target_scope }, fact_conflict_resolutions?, credential_overrides?, ad_hoc_credentials? }`. `fact_conflict_resolutions`: `[{ component_id, resolved_fact: "configured"\|"discovered" }]` — required for every component the preview reported as `fact_conflict`; omitting one is a 400 `unresolved_fact_conflict`. Re-runs refresh/readiness (the preview is advisory, not a lock) and freezes the result as an immutable `CompliancePlan`: requested scope, resolved scope, refresh coverage, every `PlannedComponentItem` (component identity, exact catalog/baseline digests, dependency closure, selector/transport/priority, resolved-configuration snapshot digest, credential/trust references), and coverage omissions. 202 → `run_id`. 400 `no_runnable_component` only when refresh validates zero runnable components across the entire requested scope (ADR-0023: "initiation fails only when refresh validates no runnable component") — an explicit scope that resolves to only unsupported/unready components still succeeds as an honest zero-execution plan when at least one boundary was validated; the distinction is refresh validation, not readiness. |
| `/runs/{id}/plan` | GET | Viewer+. The frozen `CompliancePlan` for a run: identical shape to `/runs/plan-preview`'s response but immutable and historical — never re-resolves current inventory/content/credentials. Append-only; later component/content/credential changes never alter what this returns for an existing run. |

Scheduled scans skip `/runs/plan-preview` (nothing to preview interactively) and
skip `fact_conflict_resolutions` (schedules cannot choose — a conflicted component
becomes a `CoverageOmission` and the schedule re-evaluates at next dispatch,
ADR-0023). `POST /schedules` for a scan schedule stores `{ site_id, target_scope }`
the same shape, never `profile_id`.

**Shipped by #585/#586** ([ADR-0021](adr/0021-credential-purpose-matrix.md)):
#585 landed the stored-credential half of per-purpose resolution — `credential_overrides`
on `POST /runs`, per-job `job_credential_bindings` snapshots, and the
`credential_binding_gaps` rejection contract (see the `/runs` row above). #586 landed
the ad hoc half: `run_secrets` (ADR-0016) re-keyed from one row per run to one row per
`(run, target, purpose)`, and `POST /runs`'s new `ad_hoc_credentials` field for
per-target/per-purpose inline secrets — see the `/runs` row above. The flat inline
`credential` tier remains for wire compat (one shared secret for the whole run, mapped
to every selected target's default purpose) and stays mutually exclusive with both
`credential_overrides` and `ad_hoc_credentials`.

| `/runs/{id}` | GET | `RunResponse` (issue #494, matches shipped `RunsController`/`RunContracts.cs` exactly): `id`, `run_type`, `state`, `paused`, `blocked`, `blocked_reason`, `scope`, `credential_id`, `initiated_by`, `schedule_id` (issue #515 — the schedule that dispatched this run, null for an operator-initiated one; distinct from `schedules.last_run_id`, which only ever points at a schedule's most recent run), `created_at`/`started_at`/`completed_at`, and job counts by state (`job_count`, `job_count_queued`, `job_count_running`, `job_count_completed`, `job_count_failed`, `job_count_blocked`). No per-queue/per-benchmark breakdown and no aggregate `pass`/`fail`/`na` — those live only on `/runs/{id}/artifacts`. `blocked`/`blocked_reason` are the run's single credential-halt flag (ADR-0008), not a list of independently-blockable named queues; the frontend (`liverun.ts`) synthesizes a queue-like grouping client-side from each job's `priority` for display, it is not a server concept. |
| `/runs/{id}/jobs` | GET | `JobResponse[]`: `id`, `run_id`, `job_type`, `target_id`, `target_name`, `state`, `stage`, `priority`, `attempt_count`, `created_at`/`started_at`/`finished_at`. No `benchmark` label and no per-job `pass`/`fail`/`na`/`note` on this endpoint — CAT counts are `/runs/{id}/artifacts`'s concern; a job's latest log line arrives only via `job.log` SSE, never a REST field. Compliance runs (planned, #735–#737): gains `component_id` (the `PlannedComponentItem` this job maps to 1:1), `readiness` (`ready`\|`readiness_failed` with a safe reason — never blank for a zero-attempt job), and drops nothing already listed; `attempt_count` becomes exactly the ordered-attempt count described below rather than an automatic-retry counter. At 10,000+ jobs this endpoint is server-side grouped/cursor-paged per ADR-0024 — exact query params land with #757, not fixed here. |
| `/runs/{id}/jobs/{jobId}/attempts` | GET | 📋 Planned (#735–#737, ADR-0024). `AttemptResponse[]`, ordered oldest-first: `attempt_number` (monotonic, 1-based), `started_at`/`ended_at`, `runner_id`, `stage`, `credential_binding_attribution` (`[{ purpose, credential_id? , is_run_secret, source_layer }]`, non-secret), `outcome`, `cancellation`/`cleanup_state`, and result/artifact references. Immutable and append-only — a superseded attempt is never deleted or edited when a later one begins. Viewer+, same floor as every other run read. |
| `/runs/{id}/jobs/{jobId}/attempts/{attemptId}/events` | GET | 📋 Planned. Bounded, cursor-paged historical events for one specific attempt — the attempt-scoped sibling of `/runs/{id}/events/history`'s job-scoped filter, needed because one job now has more than one attempt's worth of history to disambiguate. |
| `/runs/{id}/pause` · `/resume` · `/abort` | POST | Cyber+ (own runs), Admin any (PR #819's role-matrix reconciliation; issue #757's "Cyber controls owned live scans" owner decision superseded the original Operator+ floor). Runs with no recorded initiator (system/scheduled runs) are Admin-only. |
| `/runs/{id}/resume-blocked` | POST | Admin only. Body: `{ credential_id }` — the REPLACEMENT credential to swap onto the run's halted jobs (not the halted credential's own id; the server determines that from the run's blocked job set). Swaps `jobs.credential_id` old→new for that job set — and (issue #585) any `job_credential_bindings` snapshot rows on those jobs naming the halted credential, in the same transaction, so the per-purpose ledger and the column can never disagree about what a resumed job executes with — audits both credential identities, and re-queues (ADR-0008 halt behavior). 409 when the run has no credential halt to resume from, or when the replacement credential is itself queue-halted; 404 when the replacement credential does not exist; 400 when its `credential_type` does not match the halted credential's. |
| `/runs/{id}/purge` | POST, GET | Issue #594 (epic #577): Admin-only, crash-safe purge of a **terminal** compliance run's owned database projections and artifact files. `POST` body: `{ confirmation: "PURGE" }` (400 otherwise — never implicit, same step-up shape as remediate's `confirmation: "REMEDIATE"`). 409 `run_not_terminal` when `state` is not `completed`/`completed_with_failures`/`aborted` (run left untouched); 404 when the run does not exist. **Design: `runs`/`jobs` rows are retained, never deleted** — job_events is append-only-by-trigger and FK'd to `jobs`, so deleting the owning row would either corrupt or require deleting that immutable SSE ledger too; instead `runs.purged_at` marks the run purged in place and a `run_purge_tombstones` row is the durable historical record. What purge actually removes: `attestation_snapshots` rows for the run (a narrow, GUC-gated exception to that table's own append-only trigger — see migration 0042), any leftover `run_secrets` row (normally already gone via the run's own terminal transition), the scan-artifact HDF/CKL files for every `scan` job in the run (deleted by a `purge` job the compliance-runner executes against its own read-write artifact mount — the API process mounts that volume read-only and cannot delete a file itself), and nulls any `schedules.last_run_id` pointing at the run. 200/202 body (`RunPurgeStatusResponse`): `run_id`, `outcome` (`Completed`\|`AlreadyPurged`\|`InProgress`\|`Failed`), `requested_by`, `requested_at`, `prior_state`, `db_phase_done`, `artifacts_phase` (`pending`\|`running`\|`done`\|`failed`), `artifacts_total`, `artifacts_deleted`, `last_error`, `completed_at`. Idempotent and retryable: calling `POST` again on an in-flight purge resumes it (the database phase is never redone; the artifact job is only re-enqueued if the prior attempt's `artifacts_phase` is `failed`), and calling it again on an already-completed purge returns `AlreadyPurged` with the original tombstone's `requested_by`/`prior_state` rather than erroring or double-writing. `GET` polls the same status shape without re-triggering anything; 404 if purge was never requested for this run. Never schedulable (`purge` is absent from the closed schedule `job_type` set, `ScheduleJobTypes.All`) — mirrors `remediate`'s exclusion. |
| `/runs/{id}/retention-hold` | POST, DELETE, GET | Issue #784 (epic #726): Admin-only, audited retention hold on a **completed** compliance run's (`scan`/`remediate`) complete evidence graph. `POST`/`DELETE` body: `{ reason: "..." }` (400 on a blank/missing reason — always required, both directions). 404 when the run does not exist; 409 `unsupported_run_type` when `run_type` is not `scan`/`remediate`; 409 `run_not_terminal` when `state` is not `completed`/`completed_with_failures`/`aborted`. Placing a hold on an already-held run is a no-op that returns the EXISTING hold (first writer wins — the second call's reason is not recorded, no duplicate audit row); removing a hold that is not active returns 404 `not_held`. While active, `POST /runs/{id}/purge` (see above) refuses with 409 `run_retention_held` and leaves the run's `component_results`/findings/artifacts and `upload_attempts` completely untouched — every deletion purge performs is reachable only through that one call, and the hold is checked on every one of them. **Mid-purge boundary, stated exactly:** a hold placed *before* a purge starts is fully honoured (nothing is deleted). A hold placed while a purge is *already in flight* **halts** that purge rather than rolling it back: it is never completed while the hold stands (`POST purge` refuses and the background finalize sweep refuses, so the run is never tombstoned and `runs.purged_at` is never set), but it cannot restore what the purge's already-committed database phase deleted. What happens to the **artifact files** depends on when the hold lands, and only the first two cases are guaranteed: (1) the hold arrives while the database phase is still running — fully honoured, because purge re-reads the hold immediately before enqueueing, so **no artifact-deletion job is enqueued for a held run**; (2) the job is enqueued but not yet claimed — fully honoured, the still-queued job is cancelled at hold time so no runner ever claims it and no file is deleted; (3) a runner has **already claimed** the job — **best-effort only.** Cancellation is cooperative: the job is marked `cancel_requested`, the dispatcher cancels the handler at its next heartbeat tick, and the handler stops at its next per-job checkpoint, so files deleted before that point — possibly all of them — are gone and the hold cannot bring them back. A cancelled pass records `artifacts_phase: "failed"`, so how far it got stays visible and the purge stays retryable. The halted purge stays visible: its `run_purges` row survives, so `GET /runs/{id}/purge` keeps reporting the partially-purged state instead of presenting it as complete, and only removing the hold and re-POSTing `purge` resumes and finalizes it. A purge that already *completed* is unaffected (`AlreadyPurged`; nothing is left to hold). 200 body (`RunRetentionHoldResponse`): `run_id`, `active`, `reason`, `placed_by`, `placed_at` — `GET` never 404s, returning `active: false` for a run that was never held or whose hold was since removed, so the run-details surface can always ask "is this run held". Every place/remove transition (actor, time, reason, direction) is recorded in the existing `audit_log` table (`retention_hold_placed`/`retention_hold_removed`), queryable via `GET /audit`; no new audit table. Non-blocking for epic #726 parity (issue #784's AC6) — this endpoint does not gate any other compliance feature. |
| `/runs/{id}/history` | DELETE, GET | Issue #592 (epic #588, its last child): Admin-only, audited, idempotent deletion of a **terminal** run's generic *operational* history record — structurally separate from `/runs/{id}/purge` above (see docs/domain-model.md's "Operational vs. domain retention ownership" table for the full per-`run_type` classification). `DELETE` body: `{ confirmation: "DELETE" }` (400 otherwise, same step-up shape as purge's `"PURGE"`). 409 `run_not_terminal` when `state` is not `completed`/`completed_with_failures`/`aborted`. **409 `requires_domain_purge_first`** when the run is compliance-owned (`run_type` is `scan` or `remediate`) and `runs.purged_at IS NULL` — epic #588's design: generic history deletion DEFERS to the domain purge for compliance-owned artifacts rather than deleting the operational record out from under results/attestations that still exist; call `POST /runs/{id}/purge` first (no ordering requirement in the other direction — purging an already-history-deleted run, if ever needed, is unaffected). 404 when the run does not exist. **Design: `runs`/`jobs` rows and `job_events` are retained, never deleted** — the identical structural reason `/runs/{id}/purge` already established (migration 0042); `runs.history_deleted_at` marks the row deleted in place (migration 0046) and a `run_history_deletion_tombstones` row (a deliberate sibling of `run_purge_tombstones`, not a shared table) is the durable historical record. What deletion actually does: sets `runs.history_deleted_at` and nulls any `schedules.last_run_id` pointing at the run — no artifact files, no other domain table is touched for any `run_type` (inventory/content/library/transfer state is never touched by this endpoint). 200 body (`RunHistoryDeletionStatusResponse`): `run_id`, `outcome` (`Completed`\|`AlreadyDeleted`), `actor`, `prior_state`, `occurred_at`. Idempotent: calling `DELETE` again on an already-deleted run returns `AlreadyDeleted` with the original tombstone's `actor`/`prior_state` rather than erroring or double-writing. `GET` reads back the same tombstone shape without triggering anything; 404 if deletion was never requested for this run. |
| `/jobs/{id}` | DELETE | Cyber+ (own runs), Admin any — same ownership scope as pause/resume/abort (issue #294; role floor updated by issue #757, matching PR #819's role-matrix reconciliation); a job's owning run with no recorded initiator is Admin-only. Cancels one job independent of its run's other jobs (issue #10/#277). 200 body distinguishes an immediate cancel (`state: "cancelled"`, queued/blocked job) from a cooperative in-flight request (`state: "cancel_requested"`, running/attesting/converting job — stops at the dispatcher's next heartbeat tick); 409 if already terminal; 404 if the job does not exist. |
| `/runs/{runId}/jobs/{jobId}/retry` | POST | Cyber+ (own runs), Admin any — same ownership scope as pause/resume/abort/job-cancel, resolved off `runId` (issue #297; role floor updated by issue #757). Moves a **`failed`** job back to `queued` with `jobs.stage` **preserved**, so the next claim resumes the pipeline at the last-reached stage instead of restarting it (ADR-0012 §5's engine-level resume primitive, now with an HTTP surface). Scoped to `failed` only — NOT `auth-failed` (use `/runs/{id}/resume-blocked`'s credential-swap-resume path instead; retrying without swapping the bad credential just re-fails) and NOT `cancelled` (a deliberate operator action — start a new run rather than silently re-queueing it). A manual retry is an explicit human override of the engine's own retry accounting: it does **not** increment `attempt_count` and is never blocked by the automatic-retry `max_attempts` cap. Records an `audit_log` entry (`event_type: "job.retried"`). 200 body: `job_id`, `state` (`"queued"`), `stage` (echoes the preserved marker, `null` if the job had not completed any stage). 409 if the job is not `failed`; 404 if the job does not exist or does not belong to `runId`. |
| `/runs/{id}/jobs/bulk-cancel` · `/bulk-retry` | POST | Issue #757: audited bulk per-item control, Cyber+ (own runs), Admin any — same ownership scope as the singular actions above. Body: `{ job_ids }` (explicit ids) **or** `{ filter }` (the same `state`/`priority`/`component_kind`/`search` shape `GET /runs/{id}/component-jobs` accepts) — mutually exclusive; a filter resolves to explicit job ids **server-side** before any mutation, bounded to `bulk_action_max_items` (500) matching rows, never "all matching" unbounded execution (400 `too_many_matches` when the filter matches more than the bound — narrow the filter or page explicit ids instead). Every resolved id is attempted independently through the same state-gated primitive the singular endpoint uses (`CancelJobAsync`/`RetryJobAsync`) — one job's conflict (already terminal, not `failed`, not part of this run) does not block the others; the response is a per-item outcome list (`{ job_id, outcome }[]`, outcomes: `cancelled`\|`cancel_requested`\|`retried`\|`not_cancellable`\|`not_retryable`\|`not_found`), never a fake all-or-nothing result. One `audit_log` row (`event_type`: `job.bulk_cancelled`\|`job.bulk_retried`) records the actor, run id, resolved id count, and per-outcome tally. |

🚧 **Superseded retry semantics for compliance jobs (ADR-0024).** The stage-preserving
in-place `retry` above describes the shipped single-attempt job. Once component jobs
own ordered attempts (#735–#737), retry for a `scan`/`remediate` component job
**creates a new attempt** against the same immutable `PlannedComponentItem` rather
than resuming stage state in place: `attempt_number` increments, the prior attempt's
timing/logs/outcome/artifacts remain immutable and addressable, and the new attempt
starts its own stage progression from the top (ADR-0012 stage markers still apply
*within* an attempt for lease-recovery resume — they do not carry across attempts).
This endpoint's response gains `attempt_number` (the new attempt's number) alongside
the existing `job_id`/`state`; `stage` is no longer meaningful across the retry
boundary and is dropped for compliance job types (`echoes the preserved marker`
remains accurate for every other job family, which keeps the shipped single-attempt
shape). A component job may retry only when it has no currently active attempt — one
active attempt per component job is the execution invariant (ADR-0024); this does not
decide whether a separate run may overlap the same target, which remains #649.
| `/runs/{id}/artifacts` · `/jobs/{id}/artifacts/{kind}` | GET | Per-target rows + CKL/HDF download; `?bundle=zip` for the export button. Row CAT counts are **nullable** and gated by `counts_available`, and (issue #1132) carry an evaluated-control denominator — see below. |
| `/runs/{id}/attestations-applied` | GET | Waivers that fired: control, scope, justification, author/version, expired-skips. **Persisted at-scan-time ledger, immutable per run** — see below. |
| `/runs/{id}/plan` | GET | Issue #1125: the frozen scan plan for an already-created run — `plan_schema_version`, `plan_digest`, `explanation`, `accepted_component_count`, and `skips` (one row per plan-time coverage omission: `component_id`, `reason`, `detail`, verbatim from `scan_plans.skips_json`). 404 for a run with no recorded plan (predates issue #734, or a legacy request shape with no `target_scope`) — never a zeroed/empty plan. Same data `POST /runs/plan-preview` shows before creation, now readable afterwards. |
| `/runs/{id}/component-results/summary` | GET | Issue #745's per-status rollup, extended (issue #1125/#1132): `requested_component_count`/`omitted_component_count` fold in the plan's frozen skips alongside the already-planned `planned_component_count`, and `coverage_incomplete` (bool) is true when either the plan omitted a requested component OR any `by_status` bucket's `evaluated_zero_controls` is true (a bucket with zero passed/open findings but at least one `not_reviewed` one — "ran, evaluated nothing"). See below. |
| `/runs/{id}/events/history` | GET | Issue #581 (ADR-0019): bounded, cursor-paged historical read over the run's persisted `job_events` — the complement to `/runs/{id}/events` SSE (below): SSE is the live/replay transport for an open connection, this is a single bounded page for a client that wants completed-run (or completed-so-far) history without holding a stream open. Viewer+, same floor as every other run read — visibility of operational history is not a domain action (ADR-0019 decision 6), so there is no ownership scoping. Query params: `job_id` (narrow to one job), `kind` (comma-separated allow-list of `job_events.event_type`, 400 on an unrecognized value), `level` (comma-separated allow-list of `job.log` payload `severity` — `information`/`warning`/`error`/`verbose`/`debug`; meaningless but harmless on event types with no `severity` field), `cursor` (opaque, from a previous response's `next_cursor`), `limit` (1–500, default 100). 404 for a run that does not exist; an existing run with no matching events (including none yet) is 200 with `items: []` and no `next_cursor` — distinct from 404, so empty history is never confused with "no such run". A garbage `cursor` or an unrecognized `kind`/`level` value or malformed `job_id` is 400 `validation_error`, never a 500. Response body: `items` (array of the same per-event envelope shape SSE sends — `seq`, `ts`, `type`, `run_id`, `job_id`, `data`; `data` is the same already-redacted `payload` column SSE streams, embedded as-is — this endpoint performs no additional transform and introduces no new leak surface) and `next_cursor` (opaque string, present only when the page was truncated by `limit` with more matching rows remaining — a page never silently truncates a large history without saying so; absent, never a bare `null`, once history is exhausted, matching every other nullable field in this API). Ordering is the same commit-order `seq` SSE uses (migration 0001/0104's `trg_job_events_assign_seq`), so a client can page history and then attach to `/runs/{id}/events` with `Last-Event-ID` set to the last `seq` it saw with no gap or duplicate at the seam. |

#### `/runs/{id}/artifacts` — countability is explicit (issue #299)

Each row carries `counts_available` (bool). The CAT counts (`cat_i_open`, `cat_ii_open`,
`cat_iii_open`) are **nullable integers** and are *omitted from the row entirely* (server
omits null properties) whenever the HDF report is absent OR present-but-unparseable — in that
case `counts_available` is `false`. A consumer MUST gate on `counts_available` before trusting
the counts: a corrupt HDF is reported as *uncountable* (counts absent), never as a
compliant-looking `0/0/0`.

**Issue #1132**: when `counts_available` is `true`, the row also carries
`controls_total` and `controls_evaluated` (both nullable integers, null exactly when
`counts_available` is `false`). `controls_evaluated` counts only controls that
produced a real pass/fail outcome — a component whose scan ran but every control
came back skipped (an execution failure, not a genuine "not applicable") reports
`cat_i_open`/`cat_ii_open`/`cat_iii_open` all `0`, identically to a fully passing
component. A consumer MUST compare `controls_evaluated` against `controls_total`
before reading an all-zero CAT row as "clean" — `controls_total > 0` with
`controls_evaluated == 0` means nothing was evaluated, not that nothing failed.
`artifact_kinds` reflects file *presence* on disk (so a
present-but-corrupt HDF still lists `hdf`), which is independent of *countability*.

🚧 **Superseded/extended result semantics (planned, ADRs 0024–0025).** Once component
jobs/attempts and per-control settings exist, each row additionally carries
`component_id`, `coverage` (`executed`\|`coverage_omission` with reason — the honest
incomplete-coverage signal ADR-0023/0025 require), and a `findings` breakdown that
distinguishes genuine compliance status from execution status: every applicable
control in the exact-baseline closure appears **exactly once**, disposition ∈
`Compliant`\|`NonCompliant`\|`Not_Reviewed` — `Not_Reviewed` covers both "did not
execute" (readiness failure, zero attempts, or execution error) and "non-automatable
control with no valid/unexpired attestation." A `Not_Reviewed` control is never
reported as `Not_Applicable`, duplicated, or silently omitted (ADR-0025). This
supersedes any implication that a corrupt/absent HDF or a skipped component simply
has no row: it has a row, with `counts_available: false` and/or explicit
`Not_Reviewed` findings, never a blank. `CoverageOmission` rows (component/boundary
work that never became executable) remain aggregated separately and are never mixed
into the control-level `findings` projection, so a clean `findings` result cannot
conceal missing coverage.

✅ **`GET /jobs/{id}/component-results/findings`** (issue #745, migration 0063's
`component_result_findings`): the per-component finding list for a job's LATEST
`component_results` attempt (highest `attempt_number` — mirrors
`/runs/{id}/component-results/summary`'s own "latest attempt wins" rule, ADR-0024).
Viewer+. Statuses/severities pass through exactly as recorded — epic #726 §6's closed
six-value finding vocabulary (`passed`\|`failed`\|`not_applicable`\|`not_reviewed`\|
`execution_error`\|`skipped`) is never re-bucketed or collapsed, and the exactly-once
`not_reviewed` rule for an applicable-but-unexecuted control holds because this
endpoint performs no re-derivation. Limit/offset paged (`limit` 1–500 default 100,
`offset` ≥ 0, 400 `validation_error` outside those bounds) — one attempt's finding
count is bounded by one benchmark's control count, not an unboundedly growing
history, so this follows the Conventions' `?limit/offset` + **`X-Total-Count`
header** idiom (`GET /runs` precedent) rather than `/runs/{id}/events/history`'s
cursor. The total matching-finding count travels ONLY in the `X-Total-Count`
response header, never in the body — no list endpoint in this API carries an
in-body count. Response body: `job_id`, `attempt_number`/`component_result_status`
(both null when the job has no recorded attempt at all — never claimed yet, a legacy
non-component job, or a purged run's evidence, all indistinguishable and all
honest-empty), `output_kind`/`standards_note` (issue #743: the frozen plan item's
catalog output kind joined from `scan_plan_items`; for an SRG (`hdf`) result
`standards_note` carries the fixed "not DISA-published STIG results" statement —
derived from the frozen catalog kind, never the target's connection kind; both null
for a legacy result with no plan linkage, and `standards_note` null for STIG),
`items` (`control_id`, `rule_id`?, `title`?, `severity`, `status`,
`evidence`?), `limit`, `offset`. 404 only when the job itself does not exist; a job
with zero findings (or zero recorded attempts) is 200 with `items: []` and
`X-Total-Count: 0`, matching `GetUploadAttempts`/`GetComponentResultsSummary`'s
"resource exists, evidence may not yet" convention.

✅ **`GET /jobs/{id}/component-results/artifacts`** (issue #745, migration 0063's
`component_result_artifacts`): artifact METADATA (`kind`, `path` — bare filename, never
a directory path, `digest`, `size_bytes`) for a job's LATEST attempt. Viewer+. Never
streams bytes — byte download for the two downloadable kinds (`hdf`, `ckl`) stays on
the existing `GET /jobs/{id}/artifacts/{kind}` route; a byte-download route for the
other three recorded kinds (`hdf_raw` vs. `hdf_attested` distinction, `summary`,
`log`) is undocumented and remains a remainder. Unpaged — bounded by the closed
5-value `ComponentResultArtifactKinds` vocabulary per attempt. Response: `job_id`,
`attempt_number`/`component_result_status` (both null, same honest-empty convention
as the findings endpoint above), `output_kind`/`standards_note` (same issue #743 SRG
statement as the findings endpoint), `items`. 404 only when the job does not exist.

`/jobs/{id}/artifacts/receipts` (planned, #744/#745, ADR-0025): direct STIG Manager
upload receipts for an eligible CKL — `destination`/`collection`, `benchmark_revision`,
run/component/attempt/artifact identity, `request_attempt`, `status`
(`ok`\|`conflict`\|`error`), sanitized/allowlisted response metadata fields only
(never authorization/session headers or unbounded raw bodies), and `actor`. Upload
failure never alters the scan finding outcome or destroys the immutable CKL; an
authorized retry reuses the retained artifact and frozen destination policy without
rescanning (`POST /jobs/{id}/artifacts/retry-upload`, Admin-only — destination
changes are a distinct future workflow, never an implicit retry mutation).

#### `/runs/{id}/attestations-applied` — persisted at-scan-time ledger (issue #306)

This endpoint reads a **persisted, per-target snapshot** written the instant the attest
stage resolved each scanned target's attestation (`attestation_snapshots`, migration
0021) — never a live re-resolution. A historical run's answer is therefore **immutable**:
editing the underlying config-doc afterward does not change what this endpoint reports
for that run. Each row carries `applied_at`, the genuine scan-time timestamp the
snapshot was recorded at, distinct from `attestation_updated_at` (the config-doc
version's own last-modified time) — a doc-edit time still answers a different question
than a scan-time application time, even though both are now real recorded facts rather
than one being faked. This closes the integrity gap issue #299/#305 could only disclose
via the `derivation: "live-resolution"` wire marker (removed).

Granularity is per-target, not per-control: there is no control-enumeration catalog in
this codebase to join the resolved waiver against. Per-control granularity is future
work once one exists.

🚧 **Superseded granularity (planned, ADR-0024/#785 AC).** Once the per-control
settings model and `Component`/`PlannedComponentItem` identities exist (above), this
endpoint's rows key by `(component_id, control_id)` instead of `target`, and each row
carries the same `AttestationSnapshot` fields the plan/attempt already froze
(`source_layer`, `version`, `actor`, `expiry`, `applied_at`) — this is not a new
resolution, it is exposing the plan's own frozen snapshot per control rather than a
whole-profile answer per target.

### Config documents (three-layer) — superseded by per-control settings

🚧 **Superseded (epic #726, [ADR-0024](adr/0024-compliance-execution-attempts-credentials-and-settings.md)).**
The whole-profile config-doc shortcut below is transitional. It does not survive
alongside the per-control model that follows — once #735–#737 land, `/config-docs`
stops accepting `kind: "input"`/`"attestation"` scoped to a whole profile; only
`remediation-input` (owned by future remediation work, issue #15) may still use this
shape, since remediation settings are explicitly out of #726's scope.

| Endpoint | Methods | Notes |
|---|---|---|
| `/config-docs` | GET | Filter by kind (`input`\|`attestation`\|`remediation-input`), profile, layer (`global`\|`site:{id}`\|`target:{id}`). |
| `/config-docs/{id}` | GET, PUT | PUT creates a new immutable version (author, timestamp, `@vN`). |
| `/config-docs/{id}/versions` | GET | Full history — the auditor answer. |
| `/config-docs/resolve?profile&control&target` | GET | The EFFECTIVE card: resolved value + supplying layer. |

#### Per-control settings (planned end state, ADR-0024)

Input, Attestation, and future Remediation are three independently versioned setting
*kinds* keyed by **stable baseline control identity** (not a whole profile document).
Each kind resolves `Global → Site → Target`, most specific value winning; absence at
a lower layer inherits rather than erases the higher value.

| Endpoint | Methods | Notes |
|---|---|---|
| `/baselines/{id}/controls/{controlId}/settings` | GET | Cyber+. `{ input: SettingResolution?, attestation: SettingResolution?, remediation: SettingResolution? }` where `SettingResolution` is `{ value_or_reference, source_layer, version, author, updated_at, applicability }` — `null` when no layer sets that kind for this control. `value_or_reference` is a secret reference/digest, never plaintext, for any Input marked secret. |
| `/baselines/{id}/controls/{controlId}/settings/{kind}` | PUT | Admin for `global`; Admin for `site:{id}`/`target:{id}` layer writes (Cyber+ read-only at every layer — matches the "RBAC reconciliation" table in `security.md`). Body: `{ layer, value, applicability?, attestation_expiry? }` (kind ∈ `input`\|`attestation`\|`remediation`). Creates a new immutable version; never overwrites history. `attestation_expiry` is required for `kind: "attestation"` when the control is non-automatable; omitting it on that kind is a 400. |
| `/baselines/{id}/controls/{controlId}/settings/{kind}/versions` | GET | Full version history for one (control, kind) at one layer — the per-control analogue of `/config-docs/{id}/versions`. |

Planning snapshots every effective setting a control needs — source layer/version,
value/secret reference/digest, attestation actor/provenance/expiry, and an explicit
missing/inapplicable state — into the `PlannedComponentItem`'s frozen compliance
definition; every attempt reuses that snapshot (`/runs/{id}/plan` surfaces it, see
above). Later edits require a new run. A missing required Input leaves the affected
component job visibly skipped with a safe readiness reason and no attempt; an
applicable non-automatable control with no valid attestation remains `Not_Reviewed`
in the result projection (see Results/Findings, Compliance evidence graph below) —
there is no post-scan human-assessment workflow.

### Catalog, content sources, and exact-version baselines

📋 **Planned** (epic #726, [ADR-0022](adr/0022-compliance-catalog-and-content-lifecycle.md)).
Supersedes the mutable-profile-directory model implied by "Compliance content"
below: baselines bind one exact product version to one exact profile version, never
a range or scan-time picker. ✅ **`/catalog/products` implemented** (issues #728/#729,
epic #726 Wave 1): the read-only execution-catalog surface backed by migration 0050's
normalized catalog tables (PR #822) plus migration 0051's declared-inputs entity
(this issue #729 persistence slice; the semantic importer/parser it consumes is
PR #823), including candidate promotion from the validated semantic importer wired
into `content-pull` (issue #729). ✅ **`/baselines` implemented** (issue #731, epic
#726: the staged→activate operator surface for `IBaselineRepository`/
`BaselineActivationService`, which previously had no HTTP caller). Every other row in
this table remains planned.

| Endpoint | Methods | Notes |
|---|---|---|
| `/catalog/products` | GET | ✅ Implemented (issues #728/#729). Viewer+. The closed, versioned execution-catalog vocabulary: supported products/exact versions, component transport/selector, credential purposes, priority/report group, benchmark, remediation capability, and declared profile inputs (name/type/required, content-derived from `inspec.yml`) — read-only reflection of the reviewed catalog shipped in this repository (ADR-0022: "Operators cannot upload executable plugins, scripts, or catalog mappings"). No write endpoint exists; catalog rows are populated by the `content-pull` job's semantic-import/candidate-promotion pass and by appliance updates, never by an operator-facing write. `GET /catalog/products/{id}` returns the same joined shape for one execution profile id (404 if unknown). |
| `/content-sources` | GET, PUT | Admin. Configured vendor-profile/XCCDF sources: every eligible configured STIG Manager (automatic) plus manual-upload as a source of record. `PUT` sets a per-source sync-schedule override; `null` reverts to the global daily default. |
| `/content-sources/{id}/sync` | POST | Admin. Manual sync trigger (always available alongside the schedule). 202 → `content-sync` job. Strictly additive: success stages candidates and raises a review alert; failure raises a diagnostic alert; neither mutates active content. |
| `/candidate-content` | GET | Cyber+. Staged vendor-profile/XCCDF artifacts awaiting diff/review: `id`, `source_id`, `identity` (product/version/kind), `digest`, `staged_at`, `diff_summary` (added/removed/changed/remapped/metadata-only/unchanged counts), `conflict` (bool — same identity/version claimed by two different complete artifacts). |
| `/candidate-content/{id}/diff` | GET | Cyber+. Per-control diff classification against the currently active baseline (or "new" if none): `added`\|`removed`\|`changed`\|`remapped`\|`severity_impacting`\|`input_impacting`\|`attestation_impacting`\|`metadata_only`\|`unchanged`, each with the versioned-equivalence-algorithm result and closure evidence reference. An `unknown` equivalence result is reported as `changed` (ADR-0022: "incomplete, dynamic, ambiguous, or unsupported analysis is `unknown` and therefore functionally changed"). |
| `/candidate-content/{id}/conflicts/{conflictId}/resolve` | POST | Admin. Body: `{ selected_artifact_id, reason }`. Resolves a same-identity/version conflict by selecting one complete artifact; the other is retained as history, never merged or discarded. Audited (actor/time/reason). |
| `/candidate-content/{id}/controls/{controlId}/approve` | POST | Cyber+ for `changed`/`unknown` controls (Admin inherits); body `{ test_run_id? , waive_reason? }`. Requires a successful isolated candidate-execution `test_run_id` referencing this exact control/closure/baseline UNLESS `waive_reason` is present (Admin-only waiver, audited). `metadata_only` controls are eligible for automatic approval and do not require this call. 409 `test_run_mismatch` if `test_run_id` does not reference this exact control/dependency-closure/product-version baseline. |
| `/candidate-content/{id}/test-run` | POST | Admin-only. Body: `{ target_component_id, confirmation }` — unscheduled candidate execution against any compatible configured component, including production, requiring `confirmation: "CANDIDATE_TEST"`. 202 → `run_id` (a distinct `run_type: "candidate-test"`, never posture evidence, never CKL/upload-eligible). Evidence has no age expiry and binds to the exact control/closure/baseline. |
| `/candidate-content/{id}/activate` | POST | Admin-only. Body: `{ confirmation: "ACTIVATE" }`. Atomically activates only when every control/mapping/dependency/approval/test-or-waiver and whole-baseline validation gate passes (ADR-0022); 409 `baseline_not_ready` with the specific unmet gate(s) otherwise. Activation is a single audited event, separate from approval; existing plans keep their original baseline reference, future dispatches resolve the new active baseline. |
| `/baselines` | GET | ✅ Implemented (issue #731). Viewer+. Every baseline (active, superseded, and staged-not-yet-active) bound to a catalog execution profile, with `status` (`active`\|`superseded`\|`staged`\|`rejected`) and `activated_at`/`activated_by`. `GET /baselines/{id}` returns one row (404 if unknown). |
| `/baselines` | POST | ✅ Implemented (issue #731), documented naming assumption. Admin-only. Body: `{ content_revision_id, catalog_execution_profile_id, benchmark_revision_id? }`. Stages (does not activate) a baseline binding an already-staged content revision to an execution profile — the contract named the read/rollback shape but not this create step; this slice adds it under the documented `/baselines` resource rather than inventing a new noun. Multiple staged baselines may coexist per execution profile (ADR-0022 "independent candidate lifecycles"). |
| `/baselines/{id}/impact-diff` | GET | ✅ Implemented (issue #731). Viewer+. This slice's profile-identity-level impact diff (added/changed/removed relative to the currently active baseline for the same execution profile) — a stated narrower scope than `/candidate-content/{id}/diff`'s planned full per-control semantic diff (issue #730), which remains open. |
| `/baselines/{id}/activate` | POST | ✅ Implemented (issue #731), documented scope-narrowing. Admin-only. Body: `{ confirmation: "ACTIVATE" }`. Atomically activates one staged baseline, superseding any existing active baseline for the SAME execution profile (`IBaselineRepository.ActivateAsync`'s transactional pointer-swap). This slice does **not** yet gate on `/candidate-content/{id}/activate`'s full control-approval/mapping/test-or-waiver/whole-baseline-validation ledger (issue #730/epic #726 remainder) — those gates land as a later `/candidate-content` slice; today's activation is the SRG-shaped "one coherent execution-profile baseline" atomicity guarantee only. 409 `baseline_not_ready` when the target's content revision was rejected; 200 no-op-shaped response when already active. |
| `/baselines/{id}/rollback` | POST | ✅ Implemented (issue #731). Admin-only. Body: `{ confirmation: "ROLLBACK" }`. Atomically reactivates a retained, previously-activated (now superseded) baseline for the same execution profile — the identical atomic operation as activate. History and the rollback candidate itself are never overwritten in place — this creates a new activation event pointing at the old artifact set. |

**Internal-only mechanics.** 🚧 Not part of the public API contract: catalog
compilation, content digesting, deduplication, functional-equivalence computation, and
candidate/production runner execution are internal implementation concerns with no
request/response surface of their own (ADR-0022). Callers observe only their durable
outputs above — `digest`, `diff_summary`, per-control equivalence results, and
`run_id`/`test_run_id` — never a mechanism to invoke or configure the underlying
compilation, digesting, deduplication, or equivalence algorithm directly.

### Profiles & benchmarks
| Endpoint | Methods | Notes |
|---|---|---|
| `/profiles` | GET | From compliance content: name, version, STIG\|SRG, benchmark mapping, control/severity counts, inputs-set/attested/missing stats. 🚧 Superseded read model: once `/baselines` (above) exists, `/profiles` becomes a read view scoped to the currently *active* baseline per product; staged/candidate profiles are `/candidate-content`'s concern, not this endpoint's. |
| `/profiles/{id}/controls` | GET | Control, severity, title, effective input + scope, attest status. Issue #598: `effective input`/`attest status` are the PROFILE's whole-YAML `input`/`attestation` config-doc resolution for an optional `?target=` query param, applied identically to every control row — not resolved independently per control id, since no per-control structured storage exists in the config-doc schema (domain-model.md: "stored as documents ... not parsed into forms"). Controls themselves (id/title/severity) are parsed from the profile's InSpec control files at content-pull time, not derived from config-docs. 🚧 Superseded once per-control settings (above) land: `effective input`/`attest status` resolve per control id from `/baselines/{id}/controls/{controlId}/settings`, not a whole-profile document. |
| `/benchmarks` | GET, POST | ✅ **GET implemented** (issue #730, epic #726 Wave 1): every known `benchmark_key`, Viewer+. POST (manual XCCDF/zip upload) remains 🚧 superseded: manual upload becomes a `/content-sources` entry using the same additive candidate/diff/approval pipeline as automatic STIG Manager sync (ADR-0022 — "Admin manual XCCDF upload uses the same pipeline"), not a direct-to-active write. |
| `/benchmarks/sync` | POST | Admin + connected; from STIG Manager. 🚧 Superseded by `/content-sources/{id}/sync` (above) — every eligible configured STIG Manager is an automatic source, not a single manual sync action. |
| `/profiles/{id}/mapping` | PUT | Change benchmark mapping. 🚧 Superseded (this exact whole-profile shape): mapping changes flow through the candidate/diff/approval/activation pipeline above — a direct in-place `PUT` against active content would bypass ADR-0022's atomic-activation gate. Superseded BY the exact component-level mapping surface below, not left unimplemented. |

✅ **Benchmark identity and component mapping implemented** (issue #730, epic #726
Wave 1, migration 0052 from PR #828). Every revision is immutable and digest-addressed
— multiple revisions of the same `benchmark_key` coexist side by side, never updated
in place. Every mapping write supersedes the prior current row rather than
overwriting it, giving a versioned audit trail entirely from table state.

| Endpoint | Methods | Notes |
|---|---|---|
| `/benchmarks/by-key/{benchmarkKey}` | GET | Viewer+. Every revision sharing one `benchmark_key`, newest-imported first — the coexistence AC made directly observable. Empty array for an unknown key (a `benchmark_key` is a grouping value, not a stored entity — nothing to 404 against). |
| `/benchmarks/{id}` | GET | Viewer+. Single revision detail: `benchmark_key`, `title`, `version`/`release`, `source`, `content_digest`, `rule_count`, `lifecycle_state`, `imported_at`. 404 when unknown. |
| `/benchmarks/{id}/rules` | GET | Viewer+. Every rule in one revision (`rule_id`, `vuln_id`, `severity`, `title`), ordered by `rule_id`. 404 when the revision itself is unknown. |
| `/benchmark-mappings` | GET | Viewer+. Coverage/ambiguity report: every component's current mapping decision plus closed-vocabulary status counts (`mapped_count`/`suggested_count`/`ambiguous_count`/`unmapped_count`) — the AC "rule-level mapping coverage and unmatched/ambiguous rules are queryable" at the mapping-set level. Each row also carries the issue #1002 `derived_state` described below. |
| `/benchmark-mappings/{catalogComponentId}` | GET | Viewer+. The current mapping for one component. 404 only when NO mapping decision has ever been recorded for it — a recorded `unmapped`/`ambiguous` row is 200, not 404 (never conflate "no candidate found" with "never evaluated"). |
| `/benchmark-mappings/{catalogComponentId}` | PUT | **Admin-only.** Explicit mapping/override (AC "mapping changes are Admin-only, versioned, and audited"). Body: `{ benchmark_revision_id?, status, reason? }`. Always inserts a NEW current row with `is_admin_override: true` and the caller's identity as `actor`; the prior current row is superseded, never overwritten. 404 for an unknown component or an unknown `benchmark_revision_id`; 400 for a `status` outside the closed vocabulary (`mapped`\|`suggested`\|`ambiguous`\|`unmapped`) or `status: "mapped"` without a revision id. **Issue #1002:** this body no longer accepts `is_srg_no_benchmark` — migration 0071 removed the admin-stated flag. A request that still sends `is_srg_no_benchmark: true` is rejected 400 naming the replacement; omitting it, or sending `false`, is accepted (legacy-client tolerance). |
| `/benchmark-mappings/{catalogComponentId}/history` | GET | Viewer+. Full mapping history for one component, newest first — the versioned audit trail made directly readable. Empty array (never 404) for a component that has never received a mapping decision. Each row carries `derived_state` computed the same way as the current-mapping endpoint. |

**Issue #1002 (owner-decided benchmark-mapping lifecycle, superseding migration 0052's `is_srg_no_benchmark`):** every mapping response (`GET /benchmark-mappings`, `GET /benchmark-mappings/{id}`, `GET /benchmark-mappings/{id}/history`, and the `PUT` response) carries a `derived_state` field computed at read time from the component's bound catalog content kind (`stig`\|`srg`, migration 0050) — never stored, never admin-settable, never auto-suggested:

- `not_applicable_srg` — the component's bound catalog content kind is `srg`. SRG content has no XCCDF/benchmark concept at all (ADR-0022); this replaces the old admin-stated `is_srg_no_benchmark: true` row shape entirely.
- `benchmark_missing` — the component's bound kind is `stig` and its CURRENT mapping has no benchmark revision (status `unmapped`/`suggested`/`ambiguous`, or no mapping decision has ever been recorded). A persistent, honest, **non-blocking** open alert: it does not block baseline activation/approval, and does not block scanning the component itself either — issue #1021 fixed the scan planner so this state plans and executes the STIG profile profile-only (`ScanPlanItem.is_benchmark_missing: true`, no `benchmark_revision_id`, HDF output; CKL still produced with metadata-only enrichment falling back to static benchmark metadata rather than XCCDF-derived rule identity) instead of the pre-#1021 `ScanPlanSkipReasons.unmapped_benchmark` skip that made the component permanently unplannable. The alert clears once an XCCDF is mapped and the profile+XCCDF pair is approved (#730/#1002 item 3).
- `null` — a `stig` component whose current mapping already has a mapped benchmark revision; nothing further to surface.

### STIG Manager
| Endpoint | Methods | Notes |
|---|---|---|
| `/stigman` · `/sites/{id}/stigman` | GET, PUT | Global default + per-site override; endpoint, oidc client, collection, reachability/token TTL (secret write-only). |
| `/stigman/test` | POST | Reachability + API version. Outcome is a closed set: `ok`\|`unreachable`\|`auth_failed`\|`not_configured`\|`master_key_unavailable` — the last is distinct from `auth_failed`: it means the appliance's own secrets master key (ADR-0005) could not decrypt the configured credential, not that the STIG Manager credential itself is wrong (issue #430). |

### Depot catalog & downloads (connected mode)
| Endpoint | Methods | Notes |
|---|---|---|
| `/catalog/artifacts` | GET | Indexed depot: artifact, sha256, product, version, size, status (incl. `downloading` w/ progress). Browsable without the tool installed. |
| `/catalog/sync` | POST | Local, credential-free re-index of the offline depot share only (issue #690 AC). 202 → `catalog-index` job. |
| `/catalog/pull` | GET | Issue #687: connected vendor catalog-pull readiness (gated on `/downloads/enrollment` state `validated`) plus last attempt/success facts (`last_outcome`, `last_failure_reason`, `last_success_at`, `last_success_item_count`) (Viewer+). Null-valued fields are omitted, not `null`. |
| `/catalog/pull` | POST | Distinct from `/catalog/sync`: runs the installed managed tool's `metadata download` with the stored Activation Code, authenticates and atomically promotes the result, then indexes it (Admin-only). 202 → `catalog-pull` job; 409 `catalog_pull_not_ready` if the enrollment gate is not satisfied. A zero-item result is only reported success when the authenticated vendor catalog is genuinely empty. |
| `/downloads` | GET, POST | POST: artifact ids → queued `download` jobs (Operator+). Queue view: rate, ETA, retries. |
| `/downloads/{id}` | DELETE | Cancel. |
| `/downloads/readiness` | GET | Issue #560, extended by #690: combined Activation Code health + legacy Download Token health (reported independently; the legacy token never gates readiness) + managed-tool-installed state (Viewer+). `tool_installed` is `null` until a download-runner has heartbeated at least once. |
| `/downloads/enrollment` | GET | Issue #691: assisted VCF 9.1 Software Depot enrollment state machine (`tool_unavailable`→`depot_id_unavailable`→`awaiting_portal_registration`→`activation_code_stored`→`validated`/`auth_failing`), the non-secret Depot ID/pairing timestamps, and the corrected `.com` registration URL (Viewer+). The Activation Code value never appears in this or any other response. |
| `/downloads/enrollment/depot-id` | POST | 202 → `depot-enrollment` job (`generate-depot-id`): invokes the installed tool noninteractively for the Software Depot ID (Admin-only, same floor as the credential this flow produces). |
| `/downloads/enrollment/activation-code` | POST | Accepts an existing-or-portal-issued code; 409 if its decoded `asset_id` does not match the generated Depot ID, else stores it encrypted as the `depot-activation-code` credential (Admin-only). |
| `/downloads/enrollment/validate` | POST | 202 → `depot-enrollment` job (`validate-code`): bounded noninteractive tool validation of the stored code (Admin-only). |
| `/downloads/enrollment/reset` | POST | Explicit confirmed identity reset (`{"confirm": true}` required); clears the Depot ID/pairing without touching the stored credential or any legacy Download Token (Admin-only). |

### Library & content library
| Endpoint | Methods | Notes |
|---|---|---|
| `/library/items` | GET | Presence model per mode: `present`\|`superseded`\|`in_depot`(connected)\|`missing`(air-gapped, vs last bundle manifest); provenance. |
| `/library/request-manifest` | GET | Air-gapped "export request manifest". |
| `/content-library/items` | GET, POST, DELETE | OVF/ISO/files only; upload, import-from-repository. |
| `/content-library/copy-to-vcenter` | POST | 202 → `content-library-sync` job. |

### Transfer bundles
| Endpoint | Methods | Notes |
|---|---|---|
| `/bundles/export` | POST | Connected, Admin: selection tree → 202 `bundle-export` job (build + sign). It contains every tool/content payload required for the selected functions; a future exporter may also include the operator's locally built appliance images. |
| `/bundles` · `/bundles/{id}` | GET | History: name, items, sizes, signing key, where/when applied. |
| `/bundles/import` | POST | Air-gapped, Admin: upload → verify (signature/checksums/schema) → contents diff (`new`\|`replaces`\|`identical`). |
| `/bundles/import/{id}/apply` | POST | Admin, after verification; 202 → `bundle-import` job. |

### Compliance content
| Endpoint | Methods | Notes |
|---|---|---|
| `/compliance-content` | GET, PUT | Repo, pinned tag vs tracked branch, commit, last pull by/at, profile inventory + state. |
| `/compliance-content/pull` · `/check` | POST | Connected; 202 → `content-pull` job. |
| `/compliance-content/import` | POST | Air-gapped content bundle; 202 → `content-import` job. |

🚧 **Reconciled with catalog/content sources (ADR-0022).** This section's repo-pull
model remains the *InSpec profile code* acquisition path; it is distinct from, and
does not replace, the "Catalog, content sources, and exact-version baselines" section
above, which governs vendor-profile/XCCDF *acquisition, review, and activation*. A
pulled profile still enters the same candidate/diff/approval/activation lifecycle
before it is executable — `/compliance-content/pull` stages, it does not activate.

### Trust and temporary SSH cleanup

📋 **Planned** (epic #726, [ADR-0025](adr/0025-compliance-trust-cleanup-and-evidence.md)).

| Endpoint | Methods | Notes |
|---|---|---|
| `/trust/bundles` · `/trust/bundles/{id}` | GET, POST, DELETE | Admin. Uploaded CA certificate/chain bundles as versioned public trust material — separate from encrypted credentials. Ingestion validates format/size/chain/duplicates/expiry and fails closed. |
| `/connections/{id}/trust-policy` | GET, PUT | Admin. Selects a `trust_bundle_id` (default, managed-CA verification) or an explicit scoped bypass for exactly one target/service connection: `{ mode: "managed"\|"bypass", trust_bundle_id?, bypass_reason?, bypass_version? }`. Setting `mode: "bypass"` requires `bypass_reason` (400 otherwise) and is audited with actor/time; the response and every downstream readiness/evidence view carries a prominent warning. Never a process-global or appliance-wide default — this endpoint is always scoped to one connection. |
| `/components/{id}/ssh-enablement` | GET, PUT | Admin. `PUT` opts one target into temporary SSH enablement only when the exact catalog product/version declares a reviewed capability provider (400 `capability_not_available` otherwise). Body: `{ enabled: true, management_credential_purpose }`. `GET` returns current authorization plus the durable obligation state machine: `not_required`\|`pending_enable`\|`enabled`\|`restore_pending`\|`restored`\|`cleanup_failed`. `cleanup_failed` is a prominent, retryable, persistent state — it is never cleared by a new scan attempt starting. |
| `/components/{id}/ssh-enablement/reconcile` | POST | Admin. Explicitly retries an unresolved restore obligation (`cleanup_failed`) or safely re-establishes original state under the existing obligation. Required before another attempt may temporarily mutate the same service; does not block independent sibling components. |

Every `PlannedComponentItem` freezes its trust-policy reference and (when opted in)
SSH-enablement authorization/capability/provider/management-purpose at plan time
(see `/runs/{id}/plan` above); these endpoints configure durable target/component
state, not per-run overrides. A runner materializes trust per client/session and
never mutates process-global verification shared by concurrent jobs.

### Alerts

📋 **Planned** (epic #726, ADRs 0022/0023/0025). Persistent in-app + audit-only
signals — never a blocking gate on their own; the underlying condition (discovery
failure, staged content awaiting review, cleanup-failed SSH restore) is what actually
blocks readiness, the alert only surfaces it.

| Endpoint | Methods | Notes |
|---|---|---|
| `/alerts` | GET | Viewer+. `{ id, kind, severity, subject_type, subject_id, message, raised_at, acknowledged_at?, acknowledged_by? }`. `kind` ∈ `discovery_failure`\|`content_review_ready`\|`content_sync_failure`\|`ssh_cleanup_failed`\|`trust_bypass_active`\|`credential_readiness`\|`retention_purge_failed`. Query params `kind`, `acknowledged` (bool), `since`/`until`. |
| `/alerts/{id}/acknowledge` | POST | Admin-only. Records awareness only — it never resolves, clears, or hides the underlying active condition; a re-evaluation that still finds the condition true re-raises or keeps the alert visible rather than requiring a fresh alert row. Audited (actor/time). |

New staged content and failed sync/discovery each raise their own alert `kind` for
their distinct review-vs-diagnostic purpose (ADR-0022/0023) — they are never
collapsed into one generic "something changed" signal.

### Schedules
| Endpoint | Methods | Notes |
|---|---|---|
| `/schedules` · `/schedules/{id}` | GET, POST, PUT, DELETE | Read-only job types only (server-rejects `remediate` etc. — domain rule). cron, site/scope, next_run, last_result; auto-paused states in air-gapped mode for depot kinds. Every dispatched run is recorded with initiator `"scheduled"` alongside `created_by` (the schedule's creator) — domain-model.md Scheduling: "record 'scheduled' as the initiator alongside the schedule's creator." |

### System, users, audit
| Endpoint | Methods | Notes |
|---|---|---|
| `/system` | GET | Version/build, mode, uptime, disk usage by store, depot sync, update availability, runner status, shared capacity pool (capacity/source, active leases, waiting anti-starvation reservations — issue #569, ADR-0020). |
| `/system/update` | POST (upload), `/apply` POST | Admin + re-auth; importing newer locally built images stages `update_available`; apply is a separate intentional action with pre-flight checks and returns 202 → `update` job (ADR-0009, ADR-0015). |
| `/users` | GET, POST, PUT | Admin; role, site scope, auth method, last seen. |
| `/audit` | GET | Cyber+; decrypt events, config versions, run initiations, imports/updates. |
| `/dashboard` | GET | Aggregate: KPI tiles, site posture, recent runs, attention items. |

**Compliance evidence retention sweep (issue #1062, epic #726 sections 6/7; supersedes
the sketch previously here — see [ADR-0025](adr/0025-compliance-trust-cleanup-and-evidence.md)
for the design rationale).** The shipped `/runs/{id}/purge` (documented above, in Runs
& jobs) purges one run's compliance projections. This is the graph-wide policy that
composes with it:

| Endpoint | Methods | Notes |
|---|---|---|
| `/retention-policy` | GET, PUT | Admin. One appliance-wide setting (`retention_policy` singleton, migration 0078), `{ evidence_retention_days }`, default 180 (~6 months). `PUT` body: `{ "evidence_retention_days": <integer, minimum 30> }` (`validation_error` on a missing/non-positive value or a positive value below the 30-day floor — issue #1109's guard against a mistyped, near-zero retention period that the sweep would otherwise act on immediately with no restart and no confirmation). `GET`/`PUT` response (`RetentionPolicyResponse`): `evidence_retention_days`, `updated_by` (`null` until an Admin has ever changed it), `updated_at`. Changes are prospective only — `PUT` never retroactively purges on save, it only changes the age cutoff `EvidenceRetentionSweepHostedService` evaluates on its *next* pass (the sweep re-reads this singleton fresh at the start of every pass rather than caching it, so a change takes effect without a restart). Every accepted `PUT` (including a no-op that resubmits the current value) writes one `retention_policy.updated` `audit_log` row carrying the actor and both the previous and new day counts, atomically with the update — issue #1109, matching the actor/time/reason/direction bar the retention-hold audit trail (issue #784) already set. |

Eligibility and purge under this policy cover the **complete evidence graph**
atomically per run — reusing the exact same `POST /runs/{id}/purge` mechanism the
shipped endpoint already provides (idempotent, retryable, tombstoned) rather than a
second deletion path; the sweep is the automatic *trigger*, not a different
*operation*, and inherits every guarantee and limit that entry point already
documents above (including the mid-purge `Held` boundary) — this section claims
nothing beyond what that call delivers. Readers see retained or tombstoned, never a
partially-missing graph, **except** the pre-existing partially-purged-but-unfinalized
window `/runs/{id}/purge` already documents (deferred, tracked as issue #1097 — not
introduced or widened by this sweep). Per-run retention holds (issue #784,
`/runs/{id}/retention-hold` above) protect this same graph: the sweep excludes held
runs in its own candidate query with a SQL anti-join
(`WHERE NOT EXISTS (SELECT 1 FROM run_retention_holds h WHERE h.run_id = r.id)`), so
the exclusion scales with the candidate set rather than materialising held ids in the
API process (a `ListHeldRunIdsAsync`-shaped C# surface was deliberately rejected —
see issue #1062's history); the `POST /runs/{id}/purge` refusal (409
`run_retention_held`) remains the backstop that keeps the exclusion correct even if
the candidate query ever forgets the anti-join, and is what actually protects a run
held in the narrow window between the candidate query and the purge call.

Policy-driven purges are audited distinctly from operator-initiated ones: every purge
this sweep requests carries the reserved actor `system:retention-sweep` (never a real
username) through to `run_purges.requested_by` and the completed
`run_purge_tombstones.actor` — the same append-only tombstone trail every purge
already writes, not a second audit table.

`EvidenceRetentionSweepHostedService` runs disabled by default
(`EvidenceRetentionSweep:Enabled=false`), matching the existing
`RunHistoryRolloff:Enabled` sweep's own default-off posture; an operator opts in via
configuration once satisfied with the configured retention period. **Not yet shipped:**
a dedicated `/retention-policy/sweep-status`-style read endpoint for last/next sweep
outcome (structured log events are the only visibility today) and the Admin UI
surface for viewing/setting the period (issue #1062's frontend remainder).

## Event streams (SSE)

`/api/v1/events` (global — feeds the job log drawer and nav badges) and
`/api/v1/runs/{id}/events` (per-run — feeds the selected Live Jobs detail). Event envelope:

```json
{ "seq": 48211, "ts": "2026-08-02T14:07:31Z", "type": "job.state",
  "run_id": "run-0802-0405Z", "job_id": "j-3021",
  "data": { "target": "esxi-01.example.internal", "from": "running", "to": "attesting" } }
```

Types: `job.state`, `job.log` (level + line, post-scrub), `run.progress` (counts,
percent), `queue.state` (run-level credential-halt signal — see below),
`download.progress`, `system.notice`. Follow-tail, counters, and every progress bar
in the prototype bind to these six types — anything the UI animates MUST arrive as
an event, not a poll.

`job.state`'s `to` value is one of the job state machine's values, including the
terminal `cancelled` (set by per-job cancel and run-abort alike) — issue #494: an
earlier build of the frontend's `JobState` allowlist omitted `cancelled`, which
silently dropped (rather than rendered) a cancelled job's final transition.

`queue.state`'s payload (issue #494, matches `JobQueueRepository`'s shipped
emission) is **`{ blocked, reason, credential_ids }`** on a halt trip or
**`{ blocked: false, swapped: true, old_credential_id, new_credential_id,
resumed_job_count }`** on a credential-swap-resume — there is no per-queue `key` on
the wire. It reflects the run's single credential-halt flag (`RunResponse.blocked`/
`blocked_reason`), not a per-named-queue halt: ADR-0008's "priority queue" is a
run-wide job-dispatch concept, not multiple independently-blockable named lanes.

Each type has a fixed **scope tier**, enforced by the schema
(`job_events_scope_check`; decided in #104, recorded here per #116):

| Tier | Types | `job_id` | `run_id` |
| --- | --- | --- | --- |
| job-scoped | `job.state`, `job.log`, `download.progress` | required | free (set when the job belongs to a run) |
| run-scoped | `run.progress`, `queue.state` | must be NULL | required |
| appliance-wide | `system.notice` | must be NULL | must be NULL |

`queue.state` is run-scoped by decision: ADR-0008's halted "priority queue" belongs
to a Run. The per-run stream carries a run's run-scoped events plus its jobs'
job-scoped events; `system.notice` appears only on the global stream.

**Replay guarantee (normative, stronger than "monotonic"):** `seq` is assigned in
**commit order** — a row visible to any reader has a `seq` greater than every row
that became visible before it (enforced by `trg_job_events_assign_seq`; #104). This
is what makes `Last-Event-ID` replay exact: reconnecting with the last seen `seq`
and replaying `WHERE seq > last` yields every missed event exactly once, in order.
Merely-monotonic assignment (e.g. an identity column) does NOT provide this — that
was PR #104's round-1 defect, re-documented here so it is not re-introduced.

`seq` is deliberately **not gap-free**: a rolled-back insert burns its value.
Clients MUST NOT gap-check (a missing number is not a missing event); the
commit-order guarantee above is what makes gap detection unnecessary for replay
safety.

`download.progress` is job-scoped **without exception**: it can never be emitted
for a download with no job row. Every download — including catalog-index and
future content-pull work — executes as a job on the ADR-0008 engine (all
execution flows through the job queue), so a progress emitter always has a
`job_id`; a hypothetical job-less download
would need a contract change plus a `job_events_scope_check` relaxation, decided
here to be rejected rather than left open (#116).

🚧 **Planned additions (epic #726, ADRs 0023–0025).** Two new appliance-wide event
types join `system.notice`'s tier for the alert/discovery signals above —
`alert.raised` (`{ alert_id, kind, severity, subject_type, subject_id }`) and
`alert.acknowledged` (`{ alert_id, acknowledged_by }`) — rather than overloading
`system.notice`'s free-form shape, since alerts are a first-class queryable resource
(`/alerts`) and need a stable machine-readable `kind`. `job.state`'s state-machine
values extend per the new per-component scan job states below; attempt boundaries
(a new attempt starting after retry) emit a distinct `job.attempt_started`
(job-scoped, `{ attempt_number }`) rather than overloading `job.state`'s `from`/`to`
shape, since a new attempt is not a state transition of the same execution — it is a
new execution record.

### Live Jobs and historical log queries (ADR-0019; #581 and #590 implemented)

SSE remains the live transport, but it is not the historical paging API. Issue #581
implemented `GET /runs/{id}/events/history?job_id=...&kind=...&level=...&cursor=...&limit=...`
(documented above, in the `/runs/{id}` table) — bounded, cursor-paged, distinguishing
an empty history (200, `items: []`) from a missing run (404), applying the same
Viewer+ authorization and write-time redaction as SSE, and never silently truncating
a large history (a full page always carries a `next_cursor`). The cursor wraps
`job_events.seq` alone: `seq` is already a total, commit-ordered key (migration
0001/0104's `trg_job_events_assign_seq`), so no composite `(timestamp, id)` cursor was
needed. Retention/expiry policy (a history query against records a future retention
sweep has removed) is not yet defined — deferred to whatever issue introduces
`job_events` retention, since nothing in this codebase deletes `job_events` rows today
(they are append-only-by-trigger; even `/runs/{id}/purge` leaves them in place).

The Live Jobs workspace (#590) ships against `/runs`'s plain list (documented above —
`?limit/offset` + `X-Total-Count`, not a cursor): it fetches the newest page, narrows
to non-terminal runs client-side, and fans out `GET /runs/{id}/jobs` per active run.
`#590` also adds the first frontend consumer of `GET /runs/{id}/events/history`
(`frontend/src/api/jobEventHistory.ts`), used for the historical log view of a
selected terminal job.

Issue #708/#689 (epic #706) implemented the separately-planned global filtered/cursor
list read as its own route, `GET /runs/history?state=&run_type=&since=&until=&cursor=&limit=`
(documented in the `/runs` table above) — additive, does not change `GET /runs`'s
existing `?limit/offset` contract that `useLiveJobs.ts` depends on. It backs the
global Jobs workspace's History mode: browsing terminal (and, via explicit filters,
any) runs with server-side filtering and keyset paging, independent of the active-work
list's pagination.

Queue-halt observability (#147): tripping the consecutive-auth-failure halt emits
`queue.state` for each newly-blocked run and one `system.notice` — including when
the halt only flips the durable credential state (nothing queued at that instant),
and when a later fan-out for a halted credential creates born-`blocked` work.

## State machines

- **Scan job**: `queued → running → attesting → converting → uploaded` terminal;
  `failed` from any stage; `auth-failed` (counts toward queue halt); `blocked` (halted
  queue; only exit is `resume-blocked` → `queued`). SRG jobs skip `converting/uploaded`
  (HDF-only) → `done`.
- **Run**: `pending → running → completed | completed_with_failures | aborted`;
  `paused` and `blocked` are queue-level flags, not run states.
- **Download**: `queued → downloading → verifying → verified | failed` (checksum
  mismatch ⇒ `failed`, artifact quarantined).

🚧 **Superseded/extended per-component job state (planned, ADR-0024).** A compliance
component job gains `readiness_failed` as a distinct pre-execution terminal-for-that-
attempt state — reached with **zero attempts** when a required credential/input/
baseline/trust gate fails before any attempt starts (ADR-0023/0024's "readiness-failed
component job remains visible... may have zero attempts"). This is not the same as
`failed` (which implies at least one attempt ran and ended badly) or `blocked` (a
queue-wide halt); a job may move `readiness_failed → queued` only via an authorized
credential repair (`POST /jobs/{id}/repair-credential`, above) creating a new attempt.
The per-job attempt sub-state-machine (`queued → running → attesting → converting →
uploaded | done` per attempt, `failed`/`cancelled` terminal per attempt) is scoped
*within* one attempt; a new attempt after retry restarts this sub-machine from
`queued` rather than resuming the prior attempt's position — the prior attempt's
final state is frozen, immutable history (see `/runs/{id}/jobs/{jobId}/attempts`
above). Cancellation remains cooperative: `Stop` requests cancellation of the
currently active attempt and is terminal only once the runner records cleanup
outcome (including any SSH-restore obligation, ADR-0025) — a component job is never
considered stopped while a restore obligation remains `cleanup_failed`.

The job graphs above describe handler-driven pipeline transitions. Engine actors have
additional recovery/control edges: lease recovery or claim release may move `running →
queued`, and abort may move active work to `cancelled`. These edges are validated
separately so a handler cannot requeue itself and bypass retry accounting.

**Stage resumability (ADR-0012).** A multi-stage job's pipeline position is recorded
on a durable `jobs.stage` marker, separate from `jobs.state`. `queued` remains the
only claimable, unleased state — a job resting between stages (e.g. after finishing
`attesting`, before `converting` begins) is `queued` with `stage` set to where it left
off, exactly like a fresh job except for that marker (`stage` is `NULL` for a job that
has not started its first stage). The claim query hands the marker to the handler
(`GET /runs/{id}/jobs` already projects `stage` per job), which resumes its internal
stage dispatch there instead of re-running completed stages. This extends the same
engine-only privilege `running → queued` already had (see the paragraph above) to
`attesting → queued` and `converting → queued`: lease recovery and a stage-complete
requeue may perform these moves, a handler may not. A `failed` job keeps its last
`stage` marker on the row, so whatever resumes it (a future retry action) re-enters
the pipeline at that stage rather than from the beginning — the marker is not cleared
by failure, only by reaching a later stage or a fresh run.

The consecutive-auth-failure window is the credential's most recent resolved job
outcomes: rows without `finished_at` are excluded, and equal finish times are ordered by
job id. Newly queued work therefore cannot displace a resolved failure or suppress the
halt; a successful resolved outcome still breaks the consecutive sequence.

## Postgres schema sketch

`sites` · `targets` (site_id, kind, connection jsonb, credential_id, discovery_status)
· `inventory_items` (target_id, type, parent_id, name, build, version, maintenance) --
`version` (issue #974) is the host's semantic vSphere product version, additive
alongside `build`, which is retained unchanged as a discovered fact ·
`credentials` (owner='shared', type, username?, health, rotated_at) · `credential_secrets`
(credential_id, ciphertext, data_key_wrapped, master_key_id — ADR-0005) · `runs` ·
`jobs` (run_id, target_id, priority 1-6, state, stage, counts, note, lease/heartbeat)
· `job_events` (append-only; seq, type, payload jsonb — SSE replay source) ·
`config_docs` (kind, profile, layer_type, layer_ref) · `config_versions` (doc_id, vN,
author, ts, body) · `profiles` · `benchmarks` · `profile_mappings` ·
`stigman_connections` · `depot_artifacts` (jsonb metadata, status) ·
`catalog_pull_state` (singleton: last attempt/outcome/failure, last genuine success +
item count — issue #687) · `downloads` ·
`library_items` · `content_library_items` · `bundles` (direction, manifest jsonb,
signature, applied_at/where) · `compliance_content` (singleton: ref, commit, pulled_by)
· `schedules` · `users` (oidc_sub, role, site_scope) · `audit_log` (append-only) ·
`appliance_state` (singleton: version, mode, update status). Job queue = `jobs` rows
claimed `FOR UPDATE SKIP LOCKED`; Keycloak lives in its own database.

The API inserts jobs. Dedicated runners claim them directly from Postgres with an
atomic job-type allowlist: `compliance-runner` handles discovery, credential tests,
scans, remediation, and compliance-content pull/import; `download-runner` handles
catalog, download, and library/bundle content work. The claimant owns leases,
cancellation, state transitions, and structured `job_events`; the API replays those
events over SSE (ADR-0013, ADR-0014, ADR-0017).

🚧 **Planned schema additions (epic #726, ADRs 0022–0025) — sketch only, not a
migration plan.** `components` (target_id, catalog_component_key, vendor_identity,
lifecycle, configured_fact/discovered_fact jsonb, fact_conflict, first/last_seen_at,
continuous_absence_since) · `component_observations` (component_id,
discovery_refresh_id, observed_fact jsonb, observed_at, outcome, evidence_digest) ·
`discovery_refreshes` (trigger, target_id, started_at/completed_at, outcome) ·
`content_sources`, `candidate_content` (source_id, identity, digest, diff jsonb,
conflict) · `baselines` (product_version, profile_version, xccdf_version, status,
activated_at/by) · `control_settings` (baseline_id, control_id, kind, layer, value_ref,
version, author) · `compliance_plans` / `planned_component_items` (append-only;
requested/resolved scope, component identity, catalog/baseline digests, dependency
closure, config-snapshot digest, credential/trust references) · `component_jobs`
(1:1 with `planned_component_items`; supersedes today's `jobs` row per compliance
component) · `component_job_attempts` (component_job_id, attempt_number, timing,
runner/lease, credential attribution, outcome — append-only) · `coverage_omissions`
(plan_id, identity_or_boundary, stage, reason) · `trust_bundles`,
`connection_trust_policies` · `ssh_enablement_obligations` (component_id, provider,
state, original_state, cleanup_attempts) · `control_findings` (component_id,
control_id, disposition, reason) · `upload_receipts` (artifact_id, destination,
status, sanitized_response jsonb) · `alerts` (kind, severity, subject, raised_at,
acknowledged_at/by) · `compliance_retention_policy` (singleton: retention_months,
versioned) · `compliance_purge_tombstones`. These extend, and in the marked
superseded cases replace, the `jobs`/`inventory_items`/`profiles`/`config_docs` rows
above for compliance work specifically; every other job family's schema is
unaffected.

## Legacy scan migration (ADR-0025)

The shipped `scope.profile_id` payload and target-granular `job_credential_bindings`
are transitional, not a permanent second scan model. There is one migration, not a
dual-write adapter:

1. Existing runs remain readable via their shipped `RunResponse`/`scope` shape as
   historical legacy evidence — `GET /runs/{id}` never rewrites a pre-migration run's
   persisted `scope` into the new `{ site_id, target_scope }` shape.
2. Each configured legacy schedule/saved intent is deterministically translated to
   `{ site_id, target_scope }` only when its exact requested scope survives
   catalog-resolved component planning unchanged. If translation would widen, narrow,
   or ambiguously reinterpret scope, `PUT /schedules/{id}` instead sets the schedule
   to an explicit `action_required` state (audited, with a safe reason) rather than
   silently changing or auto-disabling it — an Admin must resolve it via `PUT
   /schedules/{id}` before it dispatches again.
3. Once `/runs/plan-preview` and the `{ site_id, target_scope }` shape ship, `POST
   /runs` (scan) stops accepting `scope.profile_id` entirely — 400 `validation_error`
   naming the field, not a silent ignore. There is no window where both payload
   shapes are simultaneously creatable.
4. In-flight legacy runs at the moment of cutover drain under their original
   contract; no new legacy-shaped run is created after cutover.

## Data ledger (screen → source)

| Screen | Reads | Writes / actions |
|---|---|---|
| Live Jobs | filtered `/runs`, `/runs/{id}/jobs`, global + run SSE, bounded event history (planned) | select concurrent work; type-authorized pause/resume/abort/retry |
| Dashboard | `/dashboard`, global SSE | — |
| Start a Scan | `/sites`, `/targets`, `/targets/{id}/components` (planned; `/targets/{id}/inventory` shipped), `/credentials` (names only) — never `/profiles` (ADR-0022: the wizard never selects a profile) | POST `/runs/plan-preview` (planned), POST `/runs`, POST `/schedules`, POST discover (refresh) |
| Compliance Results | compliance-filtered `/runs`, `/runs/{id}` + artifacts + attestations-applied + `/runs/{id}/jobs/{jobId}/attempts` (planned) | export bundle; Remediate entry → POST `/runs` (Admin); `POST /runs/{id}/purge` and the graph-wide retention sweep (planned) |
| Benchmarks | `/catalog/products`, `/content-sources`, `/candidate-content` + diff/approve/activate (planned), `/baselines` (✅ shipped, issue #731) — plus shipped `/profiles`, `/profiles/{id}/controls`, `/baselines/{id}/controls/{controlId}/settings` (planned, supersedes whole-profile `/config-docs`) | PUT per-control settings (new version), resolve content conflicts, approve/waive candidate controls, ✅ stage/activate/rollback baselines (issue #731; the fuller candidate-approval-gated `/candidate-content/{id}/activate` remains planned) |
| Download Catalog | `/catalog/artifacts`, `/downloads`, `/system` (stores) | POST downloads, catalog sync, schedule edits |
| Library | `/library/items`, `/content-library/items` | uploads, import-from-repo, copy-to-vcenter, request-manifest |
| Transfer | `/bundles`, import verification detail | POST export / import / apply |
| Configuration | `/sites`, `/targets`, `/credentials`, `/stigman`, `/compliance-content`, `/users`, `/system` | full CRUD (Admin), tests, tool install, update upload/apply |

Live Jobs owns operational state and diagnostics only. Discovery links to Targets,
downloads to Catalog/Library, content jobs to Compliance Content, bundles to Transfer,
and updates to Configuration/System for durable state and domain actions.

Every prototype element traces to a row above; if a future design element cannot be
traced to a resource here, that's the trigger to amend this contract *first*
(design-brief rule: the domain model and this contract are normative).
