# ADR-0011: Credential tiers — ephemeral personal credentials in v1

Status: Accepted
Amended-by: 0016
Date: 2026-08-02

## Context

[ADR-0005](0005-secrets.md) settled envelope-encrypted secrets in Postgres. Its honest
limit: the backend must decrypt **service** credentials autonomously (scheduled 3am
scans), so a full backend compromise exposes them — a property shared by every
autonomous-automation design, Vault included. The question arose whether Keycloak
login could make decryption of **personal** credentials impossible without the user.
OIDC provides assertions, not key material (and CAC flows carry no password), so
login can *gate* decryption but cannot cryptographically bind it. Only user-supplied
material the server never stores can do that.

## Decision Drivers

_Backfilled under ADR-0027 from #8._ The sources record no issue or PR that captures
the original evaluation session — this ADR was authored in the repo's initial skeleton
commit ("Add architecture docs, ADRs, Claude workflow skills, and repo skeleton"),
which predates any tracked issue or PR. #8 is the earliest tracked issue that names
ADR-0011 (`Refs: ADR-0005, ADR-0011, docs/security.md`), and confirms this ADR's
service/shared tier by building the envelope-encrypted secrets store against it. The
bullets themselves are drawn from this ADR's own original Context text, not invented:

- ADR-0005 settled envelope-encrypted secrets in Postgres, but its honest limit is
  that the backend must decrypt **service** credentials autonomously (scheduled 3am
  scans) — a property shared by every autonomous-automation design, Vault included.
- The open question was whether Keycloak login could make decryption of **personal**
  credentials impossible without the user present.
- OIDC provides assertions, not key material, and CAC flows carry no password — so
  login can *gate* decryption but cannot cryptographically bind it.
- Only user-supplied material the server never stores can achieve that binding.

## Considered Options

_Backfilled under ADR-0027 from #8._ As with Decision Drivers above, no issue or PR
beyond this ADR's own original Decision and Rationale text records the comparison. The
bullets below are drawn from that text:

- **Envelope-encrypt personal credentials the same as service credentials (per
  ADR-0005) — rejected.** This is the tier ADR-0005 already covers; it does not answer
  the open question above, since the backend could still decrypt them autonomously.
- **Keycloak-login-gated decryption of stored personal credentials — rejected.** OIDC
  provides assertions, not key material, so login can gate decryption but cannot
  cryptographically bind it to the user; a full backend compromise would still expose
  the stored material.
- **Personal credentials are not stored in v1 (chosen).** An ad hoc run using "my
  credentials" prompts the user at run initiation; the value is held in memory for
  that run only and never persisted. Converts "how do we protect stored personal
  creds from the app itself?" into "that data does not exist," at the UX cost of one
  password field on a screen the user is already interacting with.
- **Passphrase-wrapped personal storage (Argon2id key derived from a passphrase
  supplied at time of use; nothing recoverable server-side) — explicitly deferred, not
  designed.** Noted as a possible later convenience feature, not evaluated further
  here.

## Decision

Two credential tiers with different storage models:

1. **Service/shared credentials** — envelope-encrypted in Postgres per ADR-0005.
   Decryptable autonomously; compensated by decrypt auditing, gating, and containment
   (see [`../explanation/security.md`](../explanation/security.md)).
2. **Personal credentials are not stored in v1.** An ad hoc run using "my credentials"
   prompts the user at run initiation; the value is held in memory for that run only
   and never persisted. Scheduling always uses service credentials (already decided),
   so no workflow needs a persisted personal credential.

A later convenience feature may add passphrase-wrapped personal storage (Argon2id key
derived from a passphrase supplied at time of use; nothing recoverable server-side).
That is explicitly deferred, not designed.

## Rationale

- Converts the hardest question ("how do we protect stored personal creds from the
  app itself?") into "that data does not exist."
- Cryptographic user-binding without stored material: absent users' credentials are
  not merely protected — they are absent.
- UX cost is one password field on a screen the user is already interacting with.

## Consequences

- The start-a-run flow includes a credential prompt when "use my credentials" is
  selected (design brief updated).
- Personal-credential CRUD screens are cut from v1 scope.
- The credential store schema models only service/shared credentials until the
  deferred feature lands.
