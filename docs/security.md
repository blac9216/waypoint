# Waypoint — Secrets Threat Model & Leakage Controls

Status: living security requirements. This document states plainly what the secrets
design
([ADR-0005](adr/0005-secrets.md), [ADR-0011](adr/0011-credential-tiers.md),
[ADR-0014](adr/0014-runner-job-ownership.md)) does and
does not protect, and lists the implementation requirements that keep secrets from
leaking. These are requirements, not suggestions — several have CI enforcement.

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
the M2-era in-memory-only handoff (`IEphemeralCredentialCache`, single process) could
not survive an API restart before a runner claimed the job, and a dedicated compliance
runner (ADR-0013/0014) shares no process memory with the API at all. The replacement
(`run_secrets`, one encrypted row per run, migration 0023) keeps ADR-0011's headline
guarantee — **no personal rows in the reusable credential store, ever** — while making
the secret durable enough to survive the handoff between API and runner. It is not a
passphrase-wrapped *long-lived* personal credential store; that convenience feature
remains explicitly out of v1.

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
`deploy/README.md` as a required realm configuration step, not left to be discovered
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
   in URLs. *(Audit the existing remediation module's credential passing at M4.)*
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
   network. nginx and Keycloak never see the key. Plaintext credentials never cross a
   network RPC from the API to a runner. The master key is a mounted file readable
   solely by each service's non-root user — never an environment variable
   (env leaks via `/proc/<pid>/environ`, `docker inspect`, and crash dumps).

   This control governs the envelope-encryption master key (ADR-0005). Compose mounts
   it into all three trusted services named above — the API, `compliance-runner`, and
   `download-runner` (issue #442) — each reading it from its own uid-matched file
   permission (`deploy/README.md` "Bring-up" step 4). The dev-grade
   local-auth admin password hash
   (`LocalAuthOptions.AdminPasswordHash`, issue #62) is a *different* piece of secret
   material this control doesn't literally cover — but the same leakage argument
   applies, so it gets the analogous treatment (issue #333): a mounted file
   (`LocalAuth:AdminPasswordHashFile` / `LocalAuth__AdminPasswordHashFile`), preferred
   over the `LocalAuth__AdminPasswordHash` environment variable, which is kept as a
   deprecated fallback (one-time startup WARN) so existing deployments' admin login
   doesn't break. See `deploy/README.md` "Bring-up" for the operator flow. This entire
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
