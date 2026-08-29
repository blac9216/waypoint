# Waypoint — Secrets Threat Model & Leakage Controls

Status: living security requirements. This document states plainly what the secrets
design
([ADR-0005](adr/0005-secrets.md), [ADR-0011](adr/0011-credential-tiers.md),
[ADR-0014](adr/0014-runner-job-ownership.md)) does and
does not protect, and lists the implementation requirements that keep secrets from
leaking. These are requirements, not suggestions — several have CI enforcement.
Sections marked 📋 **Planned** extend this contract for epic
[#726](https://github.com/blac9216/waypoint/issues/726) (compliance feature parity,
ADRs 0022–0025) and remain design intent until their referenced issues land.

## Threat model

### What envelope encryption defends against

Secrets are stored in Postgres encrypted (AES-256-GCM, per-secret data keys wrapped by
a master key held only by the trusted control-plane and runner services). An attacker
who obtains **ciphertext without the key** gets nothing:

- Stolen database backups or dumps (the most common credential-store leak in practice)
- Compromise of the Postgres container alone
- Disk/volume snapshots leaving custody
- SQL injection or any read-path bug that exposes table contents
- Database administrators who should not see infrastructure credentials

### What it does not defend against — and why nothing can

An attacker with code execution **inside the API or either runner** may have access to
the master key and ciphertext. The API requires the key to encrypt credential writes;
a runner requires it to decrypt credentials for jobs that runner has claimed. This is
not a flaw of the chosen pattern; it is a property of any system that performs
autonomous automation. A scheduled 3am scan must
decrypt a vCenter service credential with no human present — therefore the system can
decrypt service credentials by itself, and so can an attacker who fully controls it.
Vault does not change this: once unsealed, the application's token retrieves secrets
exactly as an attacker-in-the-app would.

The design goals that follow are:

1. **Shrink what a partial compromise yields** (encrypt at rest; strict containment).
2. **Keep the fully-autonomous tier as small as possible** (credential tiers, below).
3. **Detect use** (decrypt audit trail).

## Credential tiers ([ADR-0011](adr/0011-credential-tiers.md))

| Tier | Storage | Blast radius if DB + master key are stolen |
|---|---|---|
| **Service/shared** | Envelope-encrypted in Postgres | Exposed — accepted, compensated by audit + containment |
| **Personal** | Envelope-encrypted in Postgres, **run-scoped, terminal/expiry bounded** ([ADR-0011](adr/0011-credential-tiers.md), issue #434) — never a row in the reusable `credentials`/`credential_secrets` store | Exposed only while an ad hoc run has not yet reached a terminal state (deleted on completion/abort; a bounded expiry sweep removes abandoned rows) — same compensating controls as service/shared for that window, nothing to steal outside it |

Personal credentials are the user's own AD/vCenter password. Ad hoc runs are
interactive by definition and scheduling always uses service credentials, so no
workflow ever schedules a persisted personal credential. What changed under issue #434:
the scan-slice-era in-memory-only handoff (`IEphemeralCredentialCache`, single process) could
not survive an API restart before a runner claimed the job, and a dedicated compliance
runner (ADR-0013/0014) shares no process memory with the API at all. The replacement
(`run_secrets`, one encrypted row per run, migration 0023) keeps ADR-0011's headline
guarantee — **no personal rows in the reusable credential store, ever** — while making
the secret durable enough to survive the handoff between API and runner. It is not a
passphrase-wrapped *long-lived* personal credential store; that convenience feature
remains explicitly out of v1.

Issue #586 (epic #582) re-keyed `run_secrets` from one row per run to one row per
`(run, target, purpose)` (migration 0045), so a heterogeneous multi-target scan can
carry a distinct ad hoc credential per target/purpose without one target's operator
ever being able to decrypt another's. Every decrypt is scoped to exactly the (run,
target, purpose) key a claimed job's own `job_credential_bindings` snapshot names
(never a sibling row on the same run) and audited with that full attribution
(`run_id`/`job_id` plus `target_id`/`purpose` in `audit_log.detail`) — the same
least-privilege, fail-closed-audit discipline the legacy one-row-per-run shape already
had, generalized to N keys. Cleanup is unchanged in mechanism: the same unconditional
per-terminal-completion `DELETE FROM run_secrets WHERE run_id = $1`
(`JobQueueRepository.DeleteRunSecretIfPresentAsync`) removes every row for the run
regardless of shape, and the expiry sweep (`RunSecretStore.DeleteExpiredAsync`) treats
`expires_at` as a per-row property, so one target's ad hoc credential expiring does not
touch a sibling target's still-active one. The flat legacy shape (one row, `target_id
IS NULL`) remains fully supported for wire compat — the inline `credential` field on
`POST /runs` still maps to it.

## Authorization gating vs cryptographic tying

Keycloak provides **assertions, not key material** — OIDC tokens cannot make
decryption cryptographically impossible without a login (and CAC flows carry no
password to derive keys from). What login checks provide is **gating**: policy the
API and runners enforce before choosing to decrypt. Required gates:

- The API validates a fresh token and authorization before enqueueing every
  interactive action; a runner revalidates the persisted authorization context before
  decrypting for the claimed job.
- Sensitive operations (remediation, credential overwrite, update apply) require
  **step-up re-authentication**.
- Scheduled/system decrypts are gated by the schedule's existence and recorded as such.

### Step-up re-authentication (issue #521, AC3 of #29)

A plain OIDC relying party (ADR-0004) has no login UI and no Keycloak admin API to
call to force a fresh credential prompt — Waypoint never proxies Keycloak's own
authorization-code/token endpoints. "Step-up" therefore cannot mean anything
Keycloak-specific; it has to be built entirely out of what a relying party already
gets from a standard token.

**Freshness signal: the token's `auth_time` claim.** OIDC defines `auth_time` as the
Unix-epoch-seconds instant the End-User actually authenticated at the IdP — distinct
from `iat` (when *this token* was issued/refreshed) and `exp`. A relying party that
requests `max_age` in its authorization request is guaranteed `auth_time` back in the
ID token (OIDC Core section 2 and the authentication-request parameters in section
3.1.2, item 1); this backend does not mint or see ID tokens itself
(it only validates the access/bearer token `AddJwtBearer` receives), so the contract
here depends on **Keycloak also copying `auth_time` onto the access token** it issues
(a realm protocol mapper — the same mechanism `deploy/keycloak/realm/waypoint-realm.json`
already uses for the `role` claim; standard Keycloak has a hardcoded `Authentication
Time` mapper for this purpose). Given that, freshness reduces to one comparison the
API can make locally, with no network call to Keycloak: `now - auth_time <=
StepUpAuth:FreshnessWindow` (default 5 minutes — short enough that a walked-away
session can't be used, long enough that the redirect round trip and the subsequent
API call both comfortably land inside it).

**Triggering a fresh `auth_time`.** The frontend re-runs the authorization-code flow
immediately before a gated call, passing `prompt=login` (Keycloak: force the login
screen even with an active SSO session) or `max_age=0` (spec-standard: demand
`auth_time` be "now") on the `/authorize` redirect. Either directive makes Keycloak
re-assert the user's credential (CAC/PIV re-tap or LDAP re-prompt, per however the
realm is federated) and mint a new `auth_time`. This is a real redirect round trip,
not a silent background refresh — the whole point is a human re-proves presence.

**Backend guard.** `[RequireFreshAuth]` (`Waypoint.Core.Authorization`) is an
`AuthorizeAttribute` subclass mapped to a `StepUpAuth` policy, structurally the same
seam as `RequireRoleAttribute`/`MinimumRoleRequirement`/`MinimumRoleAuthorizationHandler`:
a requirement object, a handler that inspects the principal's claims, `AddPolicy`
registration in `Program.cs`. On an action that is unconditionally sensitive, it stacks
alongside `[RequireAdminRole]` on the same action — ASP.NET Core ANDs multiple
`[Authorize]` policies on one endpoint, so both must pass. Unlike the role guard, a
failed freshness check does not want a bare 403: the caller needs to distinguish "you
are not allowed to do this" from "you are allowed, but re-authenticate first and
retry" — the two demand different UI responses. So `FreshAuthAuthorizationHandler`
does not fail the requirement itself (an unconditional endpoint's `[RequireFreshAuth]`
is a defense-in-depth backstop, not the enforcement path); `RequireFreshAuthAttribute`
instead exposes a static `Check` helper the action calls inline, throwing
`ApiException(403, "step_up_required", ...)` through the same `ErrorHandlingMiddleware`
path `master_key_unavailable`/`auth_not_ready` already use.

Credential overwrite specifically is **conditional**, not unconditional: `PUT
/credentials/{id}` also handles rename and `sudo_enabled` flips, neither of which
touches key material and neither of which should demand step-up. A declarative
`[Authorize]` policy runs before model binding, so it cannot see whether *this*
request's body sets `secret` — applying `[RequireFreshAuth]` at the method level would
gate every PUT indiscriminately, including a bare rename. `CredentialsController.Update`
therefore does not carry the attribute at all; it calls `RequireFreshAuthAttribute.Check`
directly, and only in the branch where `request.Secret` is non-empty, checked before any
field (including the non-secret ones in the same request) is written.

**Fails closed on OIDC.** A token with no `auth_time` claim at all — a Keycloak realm
without the protocol mapper configured, or any other IdP — is treated as never fresh:
`step_up_required` every time, not "trust it because we can't check." This is the same
philosophy `OidcClaimsMappingOptionsSetup` and `MinimumRoleAuthorizationHandler` already
apply to a missing role claim. It means step-up literally does not work until the
mapper is deployed — documented in `deploy/keycloak/realm/waypoint-realm.json` and
`deploy/keycloak/README.md` as a required realm configuration step, not left to be discovered
as a silent bypass.

**Dev-flag local auth is explicit, not fail-closed.** `LocalSessionAuthenticationHandler`
(the `LocalAuth:Enabled` escape hatch, off by default, never a production identity
path) issues a session claims set with no `auth_time` at all — there is no IdP behind
it to re-prompt, so "redirect back through Keycloak with `prompt=login`" has nothing to
do. Rather than have the same missing-claim fail-closed rule silently make step-up
permanently unsatisfiable for every local-auth session (locking e2e/dev testing out of
the credential-overwrite path for a reason unrelated to what this control defends
against), `FreshAuthAuthorizationHandler`/the inline check special-case the
`LocalSession` authentication scheme: every local-auth-authenticated request is treated
as fresh. This is a deliberate, narrow carve-out for a mechanism that is already
documented as dev-only and already logs a startup Warning when enabled — it does not
weaken the OIDC path, and it is the same shape as `LocalAuthOptions.AdminPasswordHash`'s
treatment elsewhere in this document: local auth accepts a lower bar than production
by design, not by omission.

**Scope today.** Applied to `PUT /credentials/{id}` only when the request body
overwrites secret material (a non-empty `secret` field) — renaming a credential or
flipping `sudo_enabled` is not gated, since neither touches key material. `POST
/credentials` (initial creation) is unaffected: there is no existing secret being
displaced, so nothing is being "overwritten." Remediation and update-apply call sites
get the same `[RequireFreshAuth]` treatment when those endpoints land (`docs/roadmap.md`);
this issue (#521) covers only the credential-overwrite case since it is the only one
that exists today.

Cryptographic tying exists only where the user supplies material the server never
*permanently* stores — which in v1 is the run-scoped ad hoc personal-credential flow:
the encrypted row is bounded to one run's lifetime (terminal completion or expiry),
never carried forward into the reusable credential store the way a service credential
is.

The expiry window **slides on use** (issue #469): every successful decrypt extends
`expires_at` by the configured `RunSecrets:Expiry` (default 8h), so an active run keeps
its secret alive while a genuinely abandoned run's row stops sliding and is swept
within one window of its last activity. One narrow, deliberately accepted residual
(issue #476): only the InSpec stage decrypts — attest/convert work from the on-disk
report — so a *single* InSpec pass that runs longer than one full window without
re-decrypting, and then fails and retries, will find its row already swept and the
retry fails secret-not-found. This is fail-closed (nothing leaks; the run errors
honestly), realistic scan stages finish in minutes-to-hours, and operators running
exceptionally long scans can raise `RunSecrets:Expiry`.

## Leakage controls (implementation requirements)

Databases are where people look for leaks; **logs are where leaks live**. In order of
real-world importance:

1. **Log scrubbing at the sink.** The logging pipeline maintains the set of secret
   values currently in play (it encrypted or decrypted them) and redacts every
   occurrence before any line reaches a sink — console, file, or Postgres. This covers worker output:
   InSpec, PowerCLI, and Ansible will echo connection strings in stack traces.
   Ansible tasks that handle credentials set `no_log: true`.
2. **Never in process arguments.** Anything in argv is world-readable via
   `/proc/<pid>/cmdline`. Remediation's child `pwsh` processes (and any other child)
   receive secrets via stdin or an inherited file descriptor — never argv, and never
   in URLs. *(Audit the existing remediation module's credential passing in the *Remediation* story.)*
3. **Write-only API, enforced at the serialization layer.** Secret material is absent
   from every response DTO — not masked; masking means the value entered the response
   pipeline. The UI renders metadata only: name, type, owner, last-rotated.
4. **Decrypt audit trail.** Every decryption event is recorded: credential, job/run,
   initiating identity (user or schedule), timestamp. This is the detection mechanism
   for the app-compromise case and a first-class feature for the compliance audience.
5. **CI canary test.** CI seeds a fake credential with a distinctive value, runs a
   scan pipeline against a mock target, then greps every sink — logs, artifacts, API
   responses, job output — for the canary. The build fails on any hit. The scrubbing
   claim is re-proven on every build, not asserted once.
6. **Containment.** Only the API, `compliance-runner`, and `download-runner` receive
   the master key. Each gets narrowly scoped database credentials: the API owns
   control-plane writes, while runners may claim only their allowlisted job types and
   write associated state and events. Postgres listens only on an internal compose
   network (`internal: true`, no gateway, no published port) — issue #578's
   `runner-egress` network (outbound-only reachability for the two runners, see
   `docs/rationale/deploy.md#compose-runner-egress-topology`) does not attach to Postgres and does not
   change this: the runners reach Postgres exclusively over `internal`, same as
   before, and nothing else gains a route to it. nginx and Keycloak never see the
   key, and neither is attached to `runner-egress` either. Plaintext credentials
   never cross a network RPC from the API to a runner. The master key is a mounted
   file readable solely by each service's non-root user — never an environment
   variable (env leaks via `/proc/<pid>/environ`, `docker inspect`, and crash
   dumps).

   This control governs the envelope-encryption master key (ADR-0005). Compose mounts
   it into all three trusted services named above — the API, `compliance-runner`, and
   `download-runner` (issue #442) — each reading it from its own uid-matched file
   permission (`deploy/README.md` "Production only: secrets master key"). The dev-grade
   local-auth admin password hash
   (`LocalAuthOptions.AdminPasswordHash`, issue #62) is a *different* piece of secret
   material this control doesn't literally cover — but the same leakage argument
   applies, so it gets the analogous treatment (issue #333): a mounted file
   (`LocalAuth:AdminPasswordHashFile` / `LocalAuth__AdminPasswordHashFile`), preferred
   over the `LocalAuth__AdminPasswordHash` environment variable, which is kept as a
   deprecated fallback (one-time startup WARN) so existing deployments' admin login
   doesn't break. See `deploy/config.example/README.md` for the file's place in the
   config layout, and `deploy/compose.override.example.yaml`'s `LocalAuth__*` block
   for how the dev stack wires it. This entire
   mechanism — and the control-6 analogy it stands on — is moot once Keycloak (#29)
   replaces local auth.
7. **Plaintext lifetime.** Decrypted values live as briefly as practical and are not
   cached. Stated honestly: .NET cannot guarantee zeroed memory, so the achievable bar
   is *short-lived, uncached, audited* — not *never in RAM*.

## Key management

- **Master key loss = service credentials unrecoverable.** Key backup procedure is
  mandatory install documentation ([ADR-0005](adr/0005-secrets.md)).
- **Rotation** re-wraps data keys under a new master key; the schema carries a key-id
  column from day one so rotation is an online operation.
- Update/transfer **bundle signing keys** are a separate concern ([ADR-0009](adr/0009-self-update.md))
  and are never stored in the appliance database.

## Global job observability and destructive actions

The global Live Jobs surface does not make authorization global. List, stream, and
historical-log endpoints filter server-side to work the caller may observe; hiding a
row in the SPA is not an access control. Persisted history is scrubbed at the sink and
must not expose raw handler payloads that can carry credentials. Copy/export actions
operate only on the already-redacted representation.

Observation and control are separate permissions. A caller able to view a download
or scan log does not thereby gain permission to retry, abort, remediate, download an
artifact, or mutate its owning domain. Those actions retain their existing job-type
and domain role checks.

Operational-history deletion never implies domain deletion. Explicit domain purges
require their own authorization and destructive confirmation, use server-derived
artifact paths, record retryable partial failure, and retain a non-secret append-only
audit tombstone ([ADR-0019](adr/0019-global-job-observability.md)). Credential deletion
is a separate lifecycle operation and never requires erasing history merely to remove
encrypted secret material.

## Compliance managed trust and scoped TLS bypass (planned, epic #726, ADR-0025)

TLS verification is enabled by default for every HTTPS target/service connection
this feature touches. There is no process-global or appliance-wide implicit bypass,
and never will be — a connection either verifies against a managed trust bundle or
carries an explicit, narrowly scoped bypass, and both states are visible.

- **Managed CA trust is public material, not a secret.** Admin-uploaded CA
  certificates/chains (`/trust/bundles`, `docs/api-contract.md`) are versioned and
  validated (format, size, chain, duplicates, expiry, safe storage paths) at
  ingestion and fail closed on any defect. They are stored and transferred
  separately from encrypted credentials — a trust bundle leaking is not a
  credential-disclosure event, and this document's threat model for secret material
  does not apply to it.
- **Scoped bypass is Admin-only, reasoned, versioned, and audited.** An Admin may
  authorize certificate-verification bypass for exactly one named target/service
  connection (`PUT /connections/{id}/trust-policy`, `mode: "bypass"`). The API
  rejects a bypass request with no `bypass_reason`. Every bypass is recorded with
  actor/time/version and produces a **prominent** warning everywhere that
  connection's readiness or evidence is displayed — in the plan preview, the frozen
  plan, the run detail, and the resulting evidence graph. It is never inherited by
  another connection and never becomes a default.
- **Planning freezes the policy identity/version, not live state.** A
  `PlannedComponentItem` references the trust-policy identity/version in effect at
  plan time (`docs/api-contract.md`'s `/runs/{id}/plan`). A later trust-policy edit
  never silently changes an in-flight or already-created run's behavior — this is
  the same "later target edits cannot change an in-flight run" property ADR-0021 §5
  already established for credentials, extended to trust.
- **A runner never mutates process-global trust.** The compliance-runner
  materializes a policy decision as a per-client/session verification context for
  the one connection it is servicing. It must never install a process-wide
  certificate validation callback or otherwise change verification behavior for a
  concurrent job's unrelated connection — a bug here would let one target's
  Admin-authorized bypass silently leak into a sibling target's supposedly-verified
  connection in the same runner process. This is a fail-closed isolation
  requirement, not an optimization.
- **Certificate failure is isolated.** A verification failure on a managed-trust
  connection is a component/connection-scoped readiness failure (`docs/domain-model.md`
  coverage-omission model), never a whole-run halt.

## Temporary SSH enablement cleanup obligations (planned, epic #726, ADR-0025)

Scans are read-only by default. The one narrow, deliberate exception is Admin-opted
temporary SSH enablement for a catalog-declared capability, and its security
obligation is unconditional restoration:

- **Opt-in requires both policy and capability.** An Admin must explicitly enable
  this per target (`PUT /components/{id}/ssh-enablement`), and the exact catalog
  product/version must declare a reviewed inspect/enable/restore capability
  provider reachable through an already-authorized management path. Absent either,
  disabled SSH is a named coverage failure for only the dependent components — it is
  never silently worked around by a generic shell fallback, and there is no
  appliance-wide switch.
- **Original state is captured immediately before mutation, durably, before
  changing anything.** The runner re-observes the service's actual current state at
  mutation time (not the plan-time observation, which can be stale) and durably
  records that original state, its provenance, and the cleanup obligation *before*
  issuing the enable call. An originally enabled service is never disabled by this
  mechanism; only an originally disabled service is restored to disabled.
- **Restoration is mandatory, durable, idempotent, and retryable across every
  terminal path** — success, failure, cancellation, timeout, runner restart, and
  lease recovery all must reach restoration or leave the obligation openly
  `cleanup_failed`, never silently dropped. An attempt is not cleanly terminal until
  restoration succeeds or that failure state is durably recorded.
- **`cleanup_failed` is a security alert, not a scan note.** It raises a prominent,
  persistent alert (`ssh_cleanup_failed`, `docs/api-contract.md` Alerts) that remains
  visible and actionable until reconciled — starting a new scan attempt against that
  service never clears or supersedes it. Before another attempt may mutate the same
  service, the unresolved obligation must be explicitly reconciled or the service
  safely re-observed and re-established under the existing obligation
  (`POST /components/{id}/ssh-enablement/reconcile`). The gate is scoped to that one
  service — it does not block independent sibling services or components, and
  restore ownership never forks into independent sibling obligations that could each
  be partially resolved while the union stays inconsistent.
- **This is the sole exception to the read-only claim.** Every other scan behavior —
  checks, attestation resolution, HDF/CKL generation — remains strictly read-only.
  The SSH-enablement mutation is separately authorized, separately audited, and
  bounded to the execution window; it is not a remediation capability and does not
  authorize any other configuration change.

## Secret boundaries for content, candidate, and credential flows (planned, epic #726)

The additive content-ingestion pipeline (ADR-0022) and per-component credential
resolution (ADR-0024) introduce new data that must not become a secret-leakage
surface even though neither carries traditional infrastructure credentials:

- **Content artifacts (vendor profiles, XCCDF, mappings) are not secrets** and are
  stored/transferred as reviewed, versioned, content-addressed data — the write-only
  and containment controls above do not apply to them. They do, however, carry
  **provenance** that must remain trustworthy: source, digest, and acquisition
  identity are immutable once staged, so a compromised sync source cannot
  retroactively rewrite what an approver actually reviewed.
- **Candidate-execution evidence is scoped like production scan evidence.**
  Admin-only candidate test runs (`POST /candidate-content/{id}/test-run`,
  `docs/api-contract.md`) resolve credentials through the exact same purpose/binding
  mechanism as a production scan (ADR-0024) — there is no separate, looser
  credential path "because it's just a test." A candidate run against a production
  target uses that target's real bindings and is subject to the same decrypt-audit
  trail as any other job.
- **Per-control settings values follow the credential secrecy model when marked
  secret.** An Input setting flagged as secret is stored the same way a credential
  is (envelope-encrypted, write-only through the API) and is referenced from a
  `PlannedComponentItem`'s frozen snapshot by digest/reference — never by embedding
  the plaintext value in the plan, a log line, or a diagnostic. A non-secret Input
  (e.g. a numeric threshold) is not gated by this rule; the API and UI distinguish
  the two at the settings-schema level, not by convention.
- **Credential repair audits both sides of the swap.** An audited credential repair
  (`POST /jobs/{id}/repair-credential`) records old/new credential *attribution*
  (which named credential or "an ad hoc secret was used by user X"), never the
  secret values themselves, alongside actor/time/reason — the same non-secret
  audit-trail shape ADR-0014/ADR-0016 already require for every decrypt.
- **Readiness diagnostics for a missing/incompatible credential or input name the
  component and purpose, never the secret.** This extends the existing "no secret
  value enters errors, events, logs, or results" rule (`docs/architecture.md`) to
  the new per-component readiness-failure surface — a `readiness_failed` job's
  safe reason is always shaped like "vcsa-ssh binding missing for component X," never
  an echo of a submitted (and rejected) credential value.

## Audit and immutable provenance for compliance decisions (planned, epic #726)

Every gated decision in the catalog/content/execution lifecycle produces a durable,
non-secret audit record — this generalizes the existing decrypt-audit-trail
principle (control 4, above) to the new class of *content and access decisions*
epic #726 introduces, none of which existed when that control was first written:

| Decision | Audited fact |
|---|---|
| Content conflict resolution (`/candidate-content/{id}/conflicts/{id}/resolve`) | selected artifact, actor, time, reason |
| Control approval / test waiver (`/candidate-content/{id}/controls/{id}/approve`) | control, closure/baseline identity, test-run reference or waiver reason, actor, time |
| Baseline activation / rollback | baseline identity, prior active baseline, actor, time |
| Trust-policy bypass authorization | connection, reason, version, actor, time |
| SSH temporary-enablement authorization and every cleanup outcome | component, provider, original/restored state, actor/system, time |
| Credential repair | component, purpose, old/new attribution (non-secret), actor, time, reason |
| Retention policy change / graph purge | policy version or run identity, actor/system trigger, time, outcome |
| Alert acknowledgement | alert identity, actor, time |

Every row above is append-only, attributable to a real actor (human or a named
system trigger such as a schedule or retention sweep — never an anonymous "system"),
and readable by Cyber+ through `/audit` (`docs/api-contract.md`) alongside the
existing decrypt/config-version/run-initiation events. None of these records contain
secret material; several (trust bypass, SSH cleanup failure) additionally surface as
persistent in-app alerts because they represent an ongoing risk posture, not merely
a historical fact.

## RBAC reconciliation (epic #726)

`docs/domain-model.md`'s Roles table and `docs/api-contract.md`'s RBAC summary are
the wire-facing source of truth; this section states the security rationale for
where epic #726 narrows or clarifies existing role boundaries. It widens nothing
beyond what those two documents already specify.

- **Viewer remains strictly read-only** across every new resource this epic
  introduces (catalog, content sources, candidates, baselines, components,
  discovery refreshes, plans, attempts, alerts, trust policy, retention status).
  Read access to a resource's existence and history is not itself a sensitive
  action; every value that would be sensitive (secret Input references, credential
  material) is redacted at the same serialization layer that already protects
  stored credentials.
- **Cyber+ gets a scan-specific interactive subset, not a general job-control
  grant.** A Cyber-or-higher user may initiate an arbitrary interactive scan subset
  and control (pause/resume/abort/cancel/retry/request credential repair) only the
  scans **they personally initiated**; Admin retains control of any scan regardless
  of initiator. This rule is deliberately narrow: it grants nothing toward
  `download`, `bundle-import`, `update`, or any other job family's control
  actions — the existing ownership-scoped checks on those endpoints
  (`docs/api-contract.md`) are unchanged. Cyber+ also gets review authority over
  content diffs and control approval (a review action, not an activation action),
  matching ADR-0022's "Cyber-level approval" for changed/unknown controls.
- **Admin-only actions are exactly the destructive, appliance-wide, or
  trust-affecting ones**: recurring scan schedule management, content
  activation/rollback, target/component persistent configuration (including
  `configured_fact` writes and retired-component purge), trust bundle management and
  scoped TLS bypass authorization, temporary SSH enablement authorization and
  reconciliation, retention policy changes and graph purge, and alert
  acknowledgement. None of these were previously ambiguous under the shipped
  domain-model Roles table (they fall under "Admin: everything: sites, targets,
  shared credentials... remediation, updates, transfer"); epic #726 makes each one
  an explicit named permission rather than leaving it implied by "everything else."
- **Alert acknowledgement is deliberately Admin-only and deliberately inert.**
  Acknowledging an alert records awareness for audit purposes; it never resolves,
  clears, or hides the underlying condition (a `cleanup_failed` SSH obligation, an
  active trust bypass, a failed discovery boundary). This prevents acknowledgement
  from being used as an informal "dismiss" action that could reduce visibility into
  an unresolved risk — a distinction worth stating explicitly because a naive
  reading of "acknowledge" elsewhere in the industry often means "resolve."
- **No two-person rule for content approval/activation.** ADR-0022 explicitly
  allows Admin to inherit Cyber's approval authority and both approve and activate
  the same baseline as separate audited events. This is a deliberate scope decision
  (single-appliance operational reality, not a large approval bureaucracy), not an
  oversight — flagged here so a reviewer does not mistake its absence for a gap.

## Residual risks (accepted, documented)

- Full compromise of the API or an authorized runner can expose service credentials
  available to that service — mitigated by job allowlists, narrow database roles,
  detection (audit trail), containment, and the small autonomous tier; not eliminable.
- A compromised API or the runner responsible for a job can decrypt run-scoped personal
  credentials of users whose ad hoc run has not yet reached a terminal state **during**
  the compromise window — temporal and run-scoped exposure only; a completed/aborted
  run's secret is already deleted, and absent users (no ad hoc run in flight) are safe.
- Memory-scraping a live, privileged API or runner process yields in-flight plaintext —
  out of scope; host compromise defeats any self-hosted design.
- **(Planned, epic #726) An Admin-authorized scoped TLS bypass is an accepted,
  narrow, audited risk for one connection** — it is not eliminable without removing
  the feature entirely (some lab/legacy endpoints genuinely cannot present a
  verifiable chain), so the compensating controls are visibility (prominent
  warnings everywhere that connection's evidence appears) and non-inheritance
  (scoped to exactly one connection, never a default).
- **(Planned, epic #726) A `cleanup_failed` SSH-restoration obligation is a real,
  disclosed residual state**, not a false "success" — the design accepts that
  restoration can fail (runner crash mid-mutation, target becomes unreachable) and
  compensates with mandatory alerting and a retryable reconciliation path rather
  than pretending failure cannot happen or silently leaving the target's access
  state undocumented.
