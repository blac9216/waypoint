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
- **Write-only secrets**: credential material and tokens appear in requests, never in
  responses (enforced at the serialization layer — `security.md` control 3).
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
| `/credentials` · `/credentials/{id}` | GET, POST, PUT, DELETE | Admin writes. Metadata out: name, type, used_by_count, rotated_at, health (`valid`\|`auth_failing`). Secret material in only. |
| `/credentials/{id}/test` | POST | Connectivity check; 202 → job. |

### Runs & jobs
| Endpoint | Methods | Notes |
|---|---|---|
| `/runs` | GET, POST | POST body: site_id, scope (products/components + inventory selection), credential (`service` \| inline personal — never persisted), schedule?. Cyber+ for scans; remediation POSTs require Admin + `confirmation: "REMEDIATE"`. |
| `/runs/{id}` | GET | Header, progress, pass/fail/na, per-queue status incl. `blocked`. |
| `/runs/{id}/jobs` | GET | Per-target rows: state, stage progress, counts, note. |
| `/runs/{id}/pause` · `/resume` · `/abort` | POST | Operator+ (own runs), Admin any. |
| `/runs/{id}/resume-blocked` | POST | Admin; body: replacement credential_ref → re-queues blocked jobs (ADR-0008 halt behavior). |
| `/runs/{id}/artifacts` · `/jobs/{id}/artifacts/{kind}` | GET | CKL/HDF download; `?bundle=zip` for the export button. |
| `/runs/{id}/attestations-applied` | GET | Waivers that fired: control, scope, justification, author/version, expired-skips. |

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
`system.notice`. `seq` is monotonic per stream; clients reconnect with
`Last-Event-ID` and the server replays from Postgres. Follow-tail, counters, and every
progress bar in the prototype bind to these six types — anything the UI animates MUST
arrive as an event, not a poll.

## State machines

- **Scan job**: `queued → running → attesting → converting → uploaded` terminal;
  `failed` from any stage; `auth-failed` (counts toward queue halt); `blocked` (halted
  queue; only exit is `resume-blocked` → `queued`). SRG jobs skip `converting/uploaded`
  (HDF-only) → `done`.
- **Run**: `pending → running → completed | completed_with_failures | aborted`;
  `paused` and `blocked` are queue-level flags, not run states.
- **Download**: `queued → downloading → verifying → verified | failed` (checksum
  mismatch ⇒ `failed`, artifact quarantined).

## Postgres schema sketch

`sites` · `targets` (site_id, kind, connection jsonb, credential_id, discovery_status)
· `inventory_items` (target_id, type, parent_id, name, build, maintenance) ·
`credentials` (owner='shared', type, health, rotated_at) · `credential_secrets`
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
