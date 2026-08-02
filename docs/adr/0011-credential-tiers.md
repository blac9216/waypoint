# ADR-0011: Credential tiers — ephemeral personal credentials in v1

Status: Accepted

## Context

[ADR-0005](0005-secrets.md) settled envelope-encrypted secrets in Postgres. Its honest
limit: the backend must decrypt **service** credentials autonomously (scheduled 3am
scans), so a full backend compromise exposes them — a property shared by every
autonomous-automation design, Vault included. The question arose whether Keycloak
login could make decryption of **personal** credentials impossible without the user.
OIDC provides assertions, not key material (and CAC flows carry no password), so
login can *gate* decryption but cannot cryptographically bind it. Only user-supplied
material the server never stores can do that.

## Decision

Two credential tiers with different storage models:

1. **Service/shared credentials** — envelope-encrypted in Postgres per ADR-0005.
   Decryptable autonomously; compensated by decrypt auditing, gating, and containment
   (see [`../security.md`](../security.md)).
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
