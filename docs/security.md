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
| **Personal** | **Not stored (v1)** — typed at run initiation, held in memory for that run only | Nothing to steal |

Personal credentials are the user's own AD/vCenter password. Ad hoc runs are
interactive by definition and scheduling always uses service credentials, so no
workflow requires a *persisted* personal credential. A later convenience feature may
add passphrase-wrapped personal credential storage (Argon2id-derived key, supplied at
time of use, nothing recoverable server-side); that is explicitly out of v1.

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

Cryptographic tying exists only where the user supplies material the server never
stores — which in v1 is the ephemeral personal-credential flow.

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

   This control governs the envelope-encryption master key (ADR-0005). The current
   transitional Compose stack mounts it only into the combined backend; the runner
   migration expands that mount to the three services named above. The dev-grade
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
- A compromised API or the runner responsible for a job observes ephemeral personal
  credentials of users who run jobs **during** the compromise window — temporal
  exposure only; absent users are safe.
- Memory-scraping a live, privileged API or runner process yields in-flight plaintext —
  out of scope; host compromise defeats any self-hosted design.
