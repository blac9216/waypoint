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
- **Errors**: `{ "error": { "code", "message", "detail?" } }`.
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
| `/auth/login` | POST | Anonymous. Local-auth login (ADR-0004 rollout note — dev-grade M1 stand-in; #29 replaces this flow with Keycloak OIDC). Request: `{username, password}`. 200 → `{token, role, expires_at}` — **no `user` object**. 401 → `{ "error": { "code": "invalid_credentials", "message": "Invalid username or password." } }`. |
| `/auth/me` | GET | Viewer+. Current session's identity: `{username, role}`. Unaffected by the #29 Keycloak swap — same shape regardless of which issuer authenticated the caller. |

**Session token.** Opaque bearer string; presented on every subsequent request as
`Authorization: Bearer <token>`, same header the OIDC flow this stands in for uses —
callers don't need to know which issuer minted it. There is no `/auth/logout`:
discarding the client-held token ends the session client-side, and the token also
expires server-side at `expires_at` regardless (M1 local-auth default session
lifetime: 8 hours from issue). No refresh endpoint in M1 — an expired token requires
logging in again.

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

**What #29 (Keycloak) changes, and what it doesn't.** `POST /auth/login` is the
dev-grade local-auth stand-in the ADR-0004 rollout note describes and is expected to
disappear outright, replaced by Keycloak's OIDC authorization-code/token endpoints —
its path, request body, and response shape are not part of the durable contract.
`GET /auth/me` and the role guards it feeds (the client-side disabled-with-reason
treatment plus every server-side enforcement point) are expected to survive that swap
unchanged: `/auth/me` keeps returning `{username, role}` no matter which issuer
authenticated the caller, and role claims keep mapping to the same four
`Viewer`/`Cyber`/`Operator`/`Admin` values.

### Sites, targets, inventory
| Endpoint | Methods | Notes |
|---|---|---|
| `/sites` · `/sites/{id}` | GET, POST, PUT, DELETE | Admin writes. Site: name, description, stigman_override?. |
| `/sites/{id}/targets` · `/targets/{id}` | GET, POST, PUT, DELETE | kind (`vsphere`\|`nsx-api`\|`ssh`), connection.host, credential_ref, discovery_status, last_refreshed. |
| `/targets/{id}/inventory` | GET | Cached hosts/VMs tree (cluster → host → vm), build info, maintenance_mode. |
| `/targets/{id}/discover` | POST | 202 → `discover` job. |

### Credentials (service/shared only — ADR-0011)
| Endpoint | Methods | Notes |
|---|---|---|
| `/credentials` · `/credentials/{id}` | GET, POST, PUT, DELETE | Admin writes. Metadata out: name, type, username?, used_by_count, rotated_at, health (`valid`\|`auth_failing`). `username` is the protocol-level login (e.g. `administrator@vcenter-sso-domain`) a connection-type (vcenter/nsx/ssh) credential's job handler presents — distinct from `name`, which is only ever a human-facing label; not secret material, so it round-trips in responses (issue #262). Secret material in only. |
| `/credentials/{id}/test` | POST | Connectivity check; 202 → job. |

### Runs & jobs
| Endpoint | Methods | Notes |
|---|---|---|
| `/runs` | GET, POST | POST body: site_id, scope (products/components + inventory selection), credential (`service` \| inline personal — never persisted), schedule?. Cyber+ for scans; remediation POSTs require Admin + `confirmation: "REMEDIATE"`. |
| `/runs/{id}` | GET | Header, progress, pass/fail/na, per-queue status incl. `blocked`. |
| `/runs/{id}/jobs` | GET | Per-target rows: state, stage progress, counts, note. |
| `/runs/{id}/pause` · `/resume` · `/abort` | POST | Operator+ (own runs), Admin any. Runs with no recorded initiator (system/scheduled runs) are Admin-only. |
| `/runs/{id}/resume-blocked` | POST | Admin only. Body: `{ credential_id }` — the REPLACEMENT credential to swap onto the run's halted jobs (not the halted credential's own id; the server determines that from the run's blocked job set). Swaps `jobs.credential_id` old→new for that job set, audits both credential identities, and re-queues (ADR-0008 halt behavior). 409 when the run has no credential halt to resume from, or when the replacement credential is itself queue-halted; 404 when the replacement credential does not exist; 400 when its `credential_type` does not match the halted credential's. |
| `/jobs/{id}` | DELETE | Operator+ (own runs), Admin any — same ownership scope as pause/resume/abort (issue #294); a job's owning run with no recorded initiator is Admin-only. Cancels one job independent of its run's other jobs (issue #10/#277). 200 body distinguishes an immediate cancel (`state: "cancelled"`, queued/blocked job) from a cooperative in-flight request (`state: "cancel_requested"`, running/attesting/converting job — stops at the dispatcher's next heartbeat tick); 409 if already terminal; 404 if the job does not exist. |
| `/runs/{runId}/jobs/{jobId}/retry` | POST | Operator+ (own runs), Admin any — same ownership scope as pause/resume/abort/job-cancel, resolved off `runId` (issue #297). Moves a **`failed`** job back to `queued` with `jobs.stage` **preserved**, so the next claim resumes the pipeline at the last-reached stage instead of restarting it (ADR-0012 §5's engine-level resume primitive, now with an HTTP surface). Scoped to `failed` only — NOT `auth-failed` (use `/runs/{id}/resume-blocked`'s credential-swap-resume path instead; retrying without swapping the bad credential just re-fails) and NOT `cancelled` (a deliberate operator action — start a new run rather than silently re-queueing it). A manual retry is an explicit human override of the engine's own retry accounting: it does **not** increment `attempt_count` and is never blocked by the automatic-retry `max_attempts` cap. Records an `audit_log` entry (`event_type: "job.retried"`). 200 body: `job_id`, `state` (`"queued"`), `stage` (echoes the preserved marker, `null` if the job had not completed any stage). 409 if the job is not `failed`; 404 if the job does not exist or does not belong to `runId`. |
| `/runs/{id}/artifacts` · `/jobs/{id}/artifacts/{kind}` | GET | Per-target rows + CKL/HDF download; `?bundle=zip` for the export button. Row CAT counts are **nullable** and gated by `counts_available` — see below. |
| `/runs/{id}/attestations-applied` | GET | Waivers that fired: control, scope, justification, author/version, expired-skips. **Persisted at-scan-time ledger, immutable per run** — see below. |

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
| `/profiles/{id}/controls` | GET | Control, severity, title, effective input + scope, attest status. |
| `/benchmarks` | GET, POST | POST = manual XCCDF/zip upload. |
| `/benchmarks/sync` | POST | Admin + connected; from STIG Manager. |
| `/profiles/{id}/mapping` | PUT | Change benchmark mapping. |

### STIG Manager
| Endpoint | Methods | Notes |
|---|---|---|
| `/stigman` · `/sites/{id}/stigman` | GET, PUT | Global default + per-site override; endpoint, oidc client, collection, reachability/token TTL (secret write-only). |
| `/stigman/test` | POST | Reachability + API version. |

### Depot catalog & downloads (connected mode)
| Endpoint | Methods | Notes |
|---|---|---|
| `/catalog/artifacts` | GET | Indexed depot: artifact, sha256, product, version, size, status (incl. `downloading` w/ progress). Browsable without the tool installed. |
| `/catalog/sync` | POST | 202 → `catalog-index` job. |
| `/downloads` | GET, POST | POST: artifact ids → queued `download` jobs (Operator+). Queue view: rate, ETA, retries. |
| `/downloads/{id}` | DELETE | Cancel. |

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
| `/bundles/export` | POST | Connected, Admin: selection tree → 202 `bundle-export` job (build + sign). |
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
| `/schedules` · `/schedules/{id}` | GET, POST, PUT, DELETE | Read-only job types only (server-rejects `remediate` etc. — domain rule). cron, site/scope, next_run, last_result; auto-paused states in air-gapped mode for depot kinds. |

### System, users, audit
| Endpoint | Methods | Notes |
|---|---|---|
| `/system` | GET | Version/build, mode, uptime, disk usage by store, depot sync, update availability. |
| `/system/update` | POST (upload), `/apply` POST | Admin + re-auth; pre-flight checks; 202 → `update` job (ADR-0009). |
| `/users` | GET, POST, PUT | Admin; role, site scope, auth method, last seen. |
| `/audit` | GET | Cyber+; decrypt events, config versions, run initiations, imports/updates. |
| `/dashboard` | GET | Aggregate: KPI tiles, site posture, recent runs, attention items. |

## Event streams (SSE)

`/api/v1/events` (global — feeds the job log drawer and nav badges) and
`/api/v1/runs/{id}/events` (per-run — feeds the live run view). Event envelope:

```json
{ "seq": 48211, "ts": "2026-08-02T14:07:31Z", "type": "job.state",
  "run_id": "run-0802-0405Z", "job_id": "j-3021",
  "data": { "target": "esxi-01.example.internal", "from": "running", "to": "attesting" } }
```

Types: `job.state`, `job.log` (level + line, post-scrub), `run.progress` (counts,
percent), `queue.state` (incl. `blocked` + reason), `download.progress`,
`system.notice`. Follow-tail, counters, and every progress bar in the prototype bind
to these six types — anything the UI animates MUST arrive as an event, not a poll.

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
`stigman_connections` · `depot_artifacts` (jsonb metadata, status) · `downloads` ·
`library_items` · `content_library_items` · `bundles` (direction, manifest jsonb,
signature, applied_at/where) · `compliance_content` (singleton: ref, commit, pulled_by)
· `schedules` · `users` (oidc_sub, role, site_scope) · `audit_log` (append-only) ·
`appliance_state` (singleton: version, mode, update status). Job queue = `jobs` rows
claimed `FOR UPDATE SKIP LOCKED` (ADR-0008); Keycloak lives in its own database.

## Data ledger (screen → source)

| Screen | Reads | Writes / actions |
|---|---|---|
| Live Run | `/runs/{id}`, `/runs/{id}/jobs`, run SSE | pause/resume/abort, resume-blocked |
| Dashboard | `/dashboard`, global SSE | — |
| Start a Scan | `/sites`, `/targets`, `/targets/{id}/inventory`, `/profiles`, `/credentials` (names only) | POST `/runs`, POST `/schedules`, POST discover (refresh) |
| Results | `/runs`, `/runs/{id}` + artifacts + attestations-applied | export bundle; Remediate entry → POST `/runs` (Admin) |
| Benchmarks | `/profiles`, `/profiles/{id}/controls`, `/config-docs` + resolve, `/benchmarks` | PUT config-docs (new version), sync/upload benchmarks, change mapping |
| Download Catalog | `/catalog/artifacts`, `/downloads`, `/system` (stores) | POST downloads, catalog sync, schedule edits |
| Library | `/library/items`, `/content-library/items` | uploads, import-from-repo, copy-to-vcenter, request-manifest |
| Transfer | `/bundles`, import verification detail | POST export / import / apply |
| Configuration | `/sites`, `/targets`, `/credentials`, `/stigman`, `/compliance-content`, `/users`, `/system` | full CRUD (Admin), tests, tool install, update upload/apply |

Every prototype element traces to a row above; if a future design element cannot be
traced to a resource here, that's the trigger to amend this contract *first*
(design-brief rule: the domain model and this contract are normative).
