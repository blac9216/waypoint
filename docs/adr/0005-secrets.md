# ADR-0005: Envelope-encrypted secrets in Postgres (AWX pattern)

Status: Accepted

## Context

Waypoint stores infrastructure credentials (vCenter/NSX/SSH service accounts, users'
personal credentials, Broadcom depot tokens, STIG Manager tokens). HashiCorp Vault was
considered and rejected for v1: unseal-on-restart is a chicken-and-egg problem for an
appliance, and the BSL license complicates redistribution (OpenBao is the open fork).
The predecessor tool uses an `ansible-vault` file with a mounted password file.

## Decision

Application-managed envelope encryption, the pattern proven by Ansible AWX:

- Secrets encrypted at rest in Postgres with **AES-256-GCM**; each secret has its own
  data key, wrapped by a **master key**.
- Master key delivered as a mounted file / Docker secret — the same operator model as
  today's `STIG_VAULT_PASSWORD_FILE`, so migration is conceptually familiar.
- Secrets are **write-only** through the API: overwrite/delete, never read back.
- Credentials are owned objects (personal vs shared/service) — see `domain-model.md`.
- A pluggable **external backend interface** (Vault/OpenBao) is a later option, not v1.

**Scope**: this ADR covers **service/shared** credentials — the tier the system must
decrypt autonomously. Personal credentials are handled differently (not stored in v1):
see [ADR-0011](0011-credential-tiers.md). The full threat model — what this design
does and does not protect — and the mandatory leakage controls live in
[`../security.md`](../security.md).

## Consequences

- Master key loss = secrets unrecoverable; key backup procedure is mandatory install
  documentation.
- Key rotation = re-wrap data keys; design the schema for it (key-id column) from the
  start.
- Migration tooling from `secrets.vault` should be provided for existing users.
