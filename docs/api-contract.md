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
  | `master_key_unavailable` | 503 | The secrets master key (ADR-0005) is not mounted, unreadable, or malformed — any secret-bearing write or decrypt (e.g. `POST`/`PUT /credentials` with a `secret`) fails closed. An **operator** misconfiguration, not a client error or a transient fault: 503 (not 500) signals "appliance not fully configured, retry after remediation" rather than "something crashed." The response body is deliberately generic — it never echoes the configured key file path (server filesystem layout, `security.md` control 1); the detailed `MasterKeyUnavailableException` message (env var, path, expected format) is logged server-side only. See `deploy/README.md`, "Generate a secrets master key". |
- **Pagination**: `?limit/offset` + `X-Total-Count` on list endpoints.
- **Write-only secrets**: stored credential material (and any secret held in the
  credential store) appears in requests, never in responses (enforced at the
  serialization layer — `security.md` control 3). The issued session/bearer token is
  an explicit exception — see `### Auth`.
- Long-running operations return `202` with a `run_id`/`job_id` and progress flows
  through the event stream, not polling.

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
| `/targets/{id}/inventory` | GET | Cached hosts/VMs tree (cluster → host → vm), build info, maintenance_mode. |
| `/targets/{id}/discover` | POST | 202 → `discover` job. |

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

### Credentials (service/shared only — ADR-0011)
| Endpoint | Methods | Notes |
|---|---|---|
| `/credentials` · `/credentials/{id}` | GET, POST, PUT, DELETE | Admin writes. Metadata out: name, type, username?, used_by_count, rotated_at, last_tested_at?, expires_at?, health (`valid`\|`auth_failing`). `username` is the protocol-level login (e.g. `administrator@vcenter-sso-domain`) a connection-type (vcenter/nsx/ssh) credential's job handler presents — distinct from `name`, which is only ever a human-facing label; not secret material, so it round-trips in responses (issue #262). `last_tested_at` (issue #560) is stamped by every `credential-test` outcome, success or failure, any type. `expires_at` (issue #560) is `null` until a real upstream response supplies an expiry — never fabricated; `null` means "unknown," not "no expiry." Secret material in only. Issue #521: a `PUT` that sets `secret` (overwriting existing key material) additionally requires step-up re-authentication — a token whose `auth_time` is missing or older than the configured freshness window answers `403 { "error": { "code": "step_up_required", ... } }` instead of writing anything; renaming or flipping `sudo_enabled` alone is never gated. See `docs/security.md` "Step-up re-authentication" for the full mechanism (frontend re-auth redirect + retry tracked separately, issue #534). |
| `/credentials/{id}/test` | POST | Connectivity check; 202 → job. |

### Runs & jobs
| Endpoint | Methods | Notes |
|---|---|---|
| `/runs/history` | GET | Issue #708/#689 (epic #706): filtered, keyset-cursor-paged run list — the global Jobs workspace's History mode. Viewer+, same floor as every other run read (ADR-0019 decision 6). Query params: `state` (comma-separated allow-list of `runs.state`), `run_type` (comma-separated allow-list of `runs.run_type`), `since`/`until` (ISO-8601, inclusive bounds on `created_at`), `cursor` (opaque, from a previous response's `next_cursor`), `limit` (1–200, default 50). No filter is applied by default — including no implicit "terminal only" filter; a caller browsing "history" passes `state=completed,completed_with_failures,aborted` explicitly, same as every other filter here. An unrecognized `state`/`run_type` value or an unparseable `since`/`until` or garbage `cursor` is 400 `validation_error`, never a 500. Response body: `items` (`RunResponse[]`, same shape `GET /runs` and `GET /runs/{id}` return) and `next_cursor` (opaque, present only when the page was truncated by `limit` with more matching rows remaining — never a silent truncation). Cursor wraps `(created_at, id)` — unlike `/runs/{id}/events/history`'s single-column `job_events.seq` cursor, `runs.created_at` is not unique, so the tie-break column (matching `ORDER BY created_at DESC, id DESC`) travels in the cursor too. A route distinct from `GET /runs` (rather than overloading its `?limit/offset` contract) so the Live Jobs workspace's existing active-work list (#590) is untouched. |
| `/runs` | GET, POST | POST body: `run_type`, `scope` (JSON string — a scan run's `scope.site_id` and `scope.profile_id` are both required, optional `target_ids`), `credential_id` \| inline `credential` (personal tier, ADR-0011 — never persisted), `confirmation` (remediate only). Cyber+ for scans; remediation POSTs require Admin + `confirmation: "REMEDIATE"`. 202 body: `run_id`. Issue #639: `scope.profile_id` selects which pulled compliance-content profile (`profiles.id`, `GET /profiles`) the scan executes — must reference an installed profile or the request 404s/400s (missing entirely is a 400 `validation_error`; an unknown id is a 404 `not_found`); the run persists it in `scope` so run history shows what was actually scanned. Issue #585 (ADR-0021): optional `credential_overrides` (scan runs only, mutually exclusive with inline `credential`): `[{ target_id, purpose, credential_id }]`, each substituting a stored credential for exactly one (target, purpose) pair. Issue #586 (ADR-0021): optional `ad_hoc_credentials` (scan runs only, Operator+, mutually exclusive with inline `credential`): `[{ target_id, purpose, username, secret }]`, each an inline personal credential for exactly one (target, purpose) pair — encrypted at rest as its own `run_secrets` row keyed by `(run, target, purpose)`, never a stored `credentials` row. Ad hoc takes precedence over `credential_overrides` for the same pair; naming the same `(target_id, purpose)` in both, or twice within `ad_hoc_credentials` itself, is a 400. At creation the API resolves every purpose each selected target's scan requires (shared `CredentialPurposeMatrix`) from `ad_hoc_credentials` first, then `credential_overrides`, then the target's own `credential-bindings`, then the legacy run-level `credential_id` (now reinterpreted as a type-checked override of each target's default purpose) — then snapshots the result as immutable per-job `job_credential_bindings` rows (later target/binding edits never change an in-flight run; an ad hoc-resolved purpose sets `is_run_secret: true` on its snapshot row instead of a `credential_id`). Any missing/incompatible/out-of-scope pair rejects the whole request with a 400 `credential_binding_gaps` whose `error.binding_gaps` array enumerates every `{ target_id, target_name, purpose, reason, credential_id? }` (`reason` ∈ `missing_binding`, `incompatible_credential_type`, `credential_not_found`, `target_not_in_scope`, `purpose_not_applicable`, `duplicate_override`) before any run/job row exists. |

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
| `/runs/{id}/jobs` | GET | `JobResponse[]`: `id`, `run_id`, `job_type`, `target_id`, `target_name`, `state`, `stage`, `priority`, `attempt_count`, `created_at`/`started_at`/`finished_at`. No `benchmark` label and no per-job `pass`/`fail`/`na`/`note` on this endpoint — CAT counts are `/runs/{id}/artifacts`'s concern; a job's latest log line arrives only via `job.log` SSE, never a REST field. |
| `/runs/{id}/pause` · `/resume` · `/abort` | POST | Operator+ (own runs), Admin any. Runs with no recorded initiator (system/scheduled runs) are Admin-only. |
| `/runs/{id}/resume-blocked` | POST | Admin only. Body: `{ credential_id }` — the REPLACEMENT credential to swap onto the run's halted jobs (not the halted credential's own id; the server determines that from the run's blocked job set). Swaps `jobs.credential_id` old→new for that job set — and (issue #585) any `job_credential_bindings` snapshot rows on those jobs naming the halted credential, in the same transaction, so the per-purpose ledger and the column can never disagree about what a resumed job executes with — audits both credential identities, and re-queues (ADR-0008 halt behavior). 409 when the run has no credential halt to resume from, or when the replacement credential is itself queue-halted; 404 when the replacement credential does not exist; 400 when its `credential_type` does not match the halted credential's. |
| `/runs/{id}/purge` | POST, GET | Issue #594 (epic #577): Admin-only, crash-safe purge of a **terminal** compliance run's owned database projections and artifact files. `POST` body: `{ confirmation: "PURGE" }` (400 otherwise — never implicit, same step-up shape as remediate's `confirmation: "REMEDIATE"`). 409 `run_not_terminal` when `state` is not `completed`/`completed_with_failures`/`aborted` (run left untouched); 404 when the run does not exist. **Design: `runs`/`jobs` rows are retained, never deleted** — job_events is append-only-by-trigger and FK'd to `jobs`, so deleting the owning row would either corrupt or require deleting that immutable SSE ledger too; instead `runs.purged_at` marks the run purged in place and a `run_purge_tombstones` row is the durable historical record. What purge actually removes: `attestation_snapshots` rows for the run (a narrow, GUC-gated exception to that table's own append-only trigger — see migration 0042), any leftover `run_secrets` row (normally already gone via the run's own terminal transition), the scan-artifact HDF/CKL files for every `scan` job in the run (deleted by a `purge` job the compliance-runner executes against its own read-write artifact mount — the API process mounts that volume read-only and cannot delete a file itself), and nulls any `schedules.last_run_id` pointing at the run. 200/202 body (`RunPurgeStatusResponse`): `run_id`, `outcome` (`Completed`\|`AlreadyPurged`\|`InProgress`\|`Failed`), `requested_by`, `requested_at`, `prior_state`, `db_phase_done`, `artifacts_phase` (`pending`\|`running`\|`done`\|`failed`), `artifacts_total`, `artifacts_deleted`, `last_error`, `completed_at`. Idempotent and retryable: calling `POST` again on an in-flight purge resumes it (the database phase is never redone; the artifact job is only re-enqueued if the prior attempt's `artifacts_phase` is `failed`), and calling it again on an already-completed purge returns `AlreadyPurged` with the original tombstone's `requested_by`/`prior_state` rather than erroring or double-writing. `GET` polls the same status shape without re-triggering anything; 404 if purge was never requested for this run. Never schedulable (`purge` is absent from the closed schedule `job_type` set, `ScheduleJobTypes.All`) — mirrors `remediate`'s exclusion. |
| `/runs/{id}/history` | DELETE, GET | Issue #592 (epic #588, its last child): Admin-only, audited, idempotent deletion of a **terminal** run's generic *operational* history record — structurally separate from `/runs/{id}/purge` above (see docs/domain-model.md's "Operational vs. domain retention ownership" table for the full per-`run_type` classification). `DELETE` body: `{ confirmation: "DELETE" }` (400 otherwise, same step-up shape as purge's `"PURGE"`). 409 `run_not_terminal` when `state` is not `completed`/`completed_with_failures`/`aborted`. **409 `requires_domain_purge_first`** when the run is compliance-owned (`run_type` is `scan` or `remediate`) and `runs.purged_at IS NULL` — epic #588's design: generic history deletion DEFERS to the domain purge for compliance-owned artifacts rather than deleting the operational record out from under results/attestations that still exist; call `POST /runs/{id}/purge` first (no ordering requirement in the other direction — purging an already-history-deleted run, if ever needed, is unaffected). 404 when the run does not exist. **Design: `runs`/`jobs` rows and `job_events` are retained, never deleted** — the identical structural reason `/runs/{id}/purge` already established (migration 0042); `runs.history_deleted_at` marks the row deleted in place (migration 0046) and a `run_history_deletion_tombstones` row (a deliberate sibling of `run_purge_tombstones`, not a shared table) is the durable historical record. What deletion actually does: sets `runs.history_deleted_at` and nulls any `schedules.last_run_id` pointing at the run — no artifact files, no other domain table is touched for any `run_type` (inventory/content/library/transfer state is never touched by this endpoint). 200 body (`RunHistoryDeletionStatusResponse`): `run_id`, `outcome` (`Completed`\|`AlreadyDeleted`), `actor`, `prior_state`, `occurred_at`. Idempotent: calling `DELETE` again on an already-deleted run returns `AlreadyDeleted` with the original tombstone's `actor`/`prior_state` rather than erroring or double-writing. `GET` reads back the same tombstone shape without triggering anything; 404 if deletion was never requested for this run. |
| `/jobs/{id}` | DELETE | Operator+ (own runs), Admin any — same ownership scope as pause/resume/abort (issue #294); a job's owning run with no recorded initiator is Admin-only. Cancels one job independent of its run's other jobs (issue #10/#277). 200 body distinguishes an immediate cancel (`state: "cancelled"`, queued/blocked job) from a cooperative in-flight request (`state: "cancel_requested"`, running/attesting/converting job — stops at the dispatcher's next heartbeat tick); 409 if already terminal; 404 if the job does not exist. |
| `/runs/{runId}/jobs/{jobId}/retry` | POST | Operator+ (own runs), Admin any — same ownership scope as pause/resume/abort/job-cancel, resolved off `runId` (issue #297). Moves a **`failed`** job back to `queued` with `jobs.stage` **preserved**, so the next claim resumes the pipeline at the last-reached stage instead of restarting it (ADR-0012 §5's engine-level resume primitive, now with an HTTP surface). Scoped to `failed` only — NOT `auth-failed` (use `/runs/{id}/resume-blocked`'s credential-swap-resume path instead; retrying without swapping the bad credential just re-fails) and NOT `cancelled` (a deliberate operator action — start a new run rather than silently re-queueing it). A manual retry is an explicit human override of the engine's own retry accounting: it does **not** increment `attempt_count` and is never blocked by the automatic-retry `max_attempts` cap. Records an `audit_log` entry (`event_type: "job.retried"`). 200 body: `job_id`, `state` (`"queued"`), `stage` (echoes the preserved marker, `null` if the job had not completed any stage). 409 if the job is not `failed`; 404 if the job does not exist or does not belong to `runId`. |
| `/runs/{id}/artifacts` · `/jobs/{id}/artifacts/{kind}` | GET | Per-target rows + CKL/HDF download; `?bundle=zip` for the export button. Row CAT counts are **nullable** and gated by `counts_available` — see below. |
| `/runs/{id}/attestations-applied` | GET | Waivers that fired: control, scope, justification, author/version, expired-skips. **Persisted at-scan-time ledger, immutable per run** — see below. |
| `/runs/{id}/events/history` | GET | Issue #581 (ADR-0019): bounded, cursor-paged historical read over the run's persisted `job_events` — the complement to `/runs/{id}/events` SSE (below): SSE is the live/replay transport for an open connection, this is a single bounded page for a client that wants completed-run (or completed-so-far) history without holding a stream open. Viewer+, same floor as every other run read — visibility of operational history is not a domain action (ADR-0019 decision 6), so there is no ownership scoping. Query params: `job_id` (narrow to one job), `kind` (comma-separated allow-list of `job_events.event_type`, 400 on an unrecognized value), `level` (comma-separated allow-list of `job.log` payload `severity` — `information`/`warning`/`error`/`verbose`/`debug`; meaningless but harmless on event types with no `severity` field), `cursor` (opaque, from a previous response's `next_cursor`), `limit` (1–500, default 100). 404 for a run that does not exist; an existing run with no matching events (including none yet) is 200 with `items: []` and no `next_cursor` — distinct from 404, so empty history is never confused with "no such run". A garbage `cursor` or an unrecognized `kind`/`level` value or malformed `job_id` is 400 `validation_error`, never a 500. Response body: `items` (array of the same per-event envelope shape SSE sends — `seq`, `ts`, `type`, `run_id`, `job_id`, `data`; `data` is the same already-redacted `payload` column SSE streams, embedded as-is — this endpoint performs no additional transform and introduces no new leak surface) and `next_cursor` (opaque string, present only when the page was truncated by `limit` with more matching rows remaining — a page never silently truncates a large history without saying so; absent, never a bare `null`, once history is exhausted, matching every other nullable field in this API). Ordering is the same commit-order `seq` SSE uses (migration 0001/0104's `trg_job_events_assign_seq`), so a client can page history and then attach to `/runs/{id}/events` with `Last-Event-ID` set to the last `seq` it saw with no gap or duplicate at the seam. |

#### `/runs/{id}/artifacts` — countability is explicit (issue #299)

Each row carries `counts_available` (bool). The CAT counts (`cat_i_open`, `cat_ii_open`,
`cat_iii_open`) are **nullable integers** and are *omitted from the row entirely* (server
omits null properties) whenever the HDF report is absent OR present-but-unparseable — in that
case `counts_available` is `false`. A consumer MUST gate on `counts_available` before trusting
the counts: a corrupt HDF is reported as *uncountable* (counts absent), never as a
compliant-looking `0/0/0`. `artifact_kinds` reflects file *presence* on disk (so a
present-but-corrupt HDF still lists `hdf`), which is independent of *countability*.

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

### Config documents (three-layer)
| Endpoint | Methods | Notes |
|---|---|---|
| `/config-docs` | GET | Filter by kind (`input`\|`attestation`\|`remediation-input`), profile, layer (`global`\|`site:{id}`\|`target:{id}`). |
| `/config-docs/{id}` | GET, PUT | PUT creates a new immutable version (author, timestamp, `@vN`). |
| `/config-docs/{id}/versions` | GET | Full history — the auditor answer. |
| `/config-docs/resolve?profile&control&target` | GET | The EFFECTIVE card: resolved value + supplying layer. |

### Profiles & benchmarks
| Endpoint | Methods | Notes |
|---|---|---|
| `/profiles` | GET | From compliance content: name, version, STIG\|SRG, benchmark mapping, control/severity counts, inputs-set/attested/missing stats. |
| `/profiles/{id}/controls` | GET | Control, severity, title, effective input + scope, attest status. Issue #598: `effective input`/`attest status` are the PROFILE's whole-YAML `input`/`attestation` config-doc resolution for an optional `?target=` query param, applied identically to every control row — not resolved independently per control id, since no per-control structured storage exists in the config-doc schema (domain-model.md: "stored as documents ... not parsed into forms"). Controls themselves (id/title/severity) are parsed from the profile's InSpec control files at content-pull time, not derived from config-docs. |
| `/benchmarks` | GET, POST | POST = manual XCCDF/zip upload. |
| `/benchmarks/sync` | POST | Admin + connected; from STIG Manager. |
| `/profiles/{id}/mapping` | PUT | Change benchmark mapping. |

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
· `inventory_items` (target_id, type, parent_id, name, build, maintenance) ·
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

## Data ledger (screen → source)

| Screen | Reads | Writes / actions |
|---|---|---|
| Live Jobs | filtered `/runs`, `/runs/{id}/jobs`, global + run SSE, bounded event history (planned) | select concurrent work; type-authorized pause/resume/abort/retry |
| Dashboard | `/dashboard`, global SSE | — |
| Start a Scan | `/sites`, `/targets`, `/targets/{id}/inventory`, `/profiles`, `/credentials` (names only) | POST `/runs`, POST `/schedules`, POST discover (refresh) |
| Compliance Results | compliance-filtered `/runs`, `/runs/{id}` + artifacts + attestations-applied | export bundle; Remediate entry → POST `/runs` (Admin); explicit compliance purge (planned) |
| Benchmarks | `/profiles`, `/profiles/{id}/controls`, `/config-docs` + resolve, `/benchmarks` | PUT config-docs (new version), sync/upload benchmarks, change mapping |
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
