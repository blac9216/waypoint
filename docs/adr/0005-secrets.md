# ADR-0005: Envelope-encrypted secrets in Postgres (AWX pattern)

Status: Accepted
Date: 2026-08-02

## Context

Waypoint stores infrastructure credentials (vCenter/NSX/SSH service accounts, users'
personal credentials, Broadcom depot tokens, STIG Manager tokens). HashiCorp Vault was
considered and rejected for v1: unseal-on-restart is a chicken-and-egg problem for an
appliance, and the BSL license complicates redistribution (OpenBao is the open fork).
The predecessor tool uses an `ansible-vault` file with a mounted password file.

## Decision Drivers

_Backfilled under ADR-0027 from #8._ No separate issue or PR debates this choice —
ADR-0005 was written directly into the repository's foundational commit, which
predates this repository's issue/PR-tracked workflow. The drivers below are drawn from
the ADR's own Context and Decision text above, corroborated by #8 (the issue that
implements "the ADR-0005 subset" and operationalizes the master-key-file and
write-only requirements named there):

- Must decrypt autonomously on every restart with no operator present — rules out a
  design with a manual unseal step.
- Must not complicate redistributing the appliance — rules out a dependency under a
  license that is a problem for that.
- Master-key delivery should match the operator model the predecessor tool's
  mounted-password-file convention already established, so migration is conceptually
  familiar.
- Stored secret material must never be readable back through the API once written.
- The design should not foreclose adopting an external secrets backend later if needs
  grow.

## Considered Options

_Backfilled under ADR-0027 from #8._ The sources record one rejected alternative,
named in Context above; they do not record the predecessor's `ansible-vault` file
having been evaluated as a competing option in its own right rather than as the
operational precedent the chosen master-key delivery model follows, so it is not
listed as a considered option here.

- **Application-managed envelope encryption (AWX pattern) — chosen.** Secrets
  encrypted at rest with a per-secret data key wrapped by a master key delivered as a
  mounted file; no external service to unseal, no additional license to redistribute.
- **HashiCorp Vault — rejected.** Purpose-built secret management, but unseal-on-restart
  is a chicken-and-egg problem for an unattended appliance, and its BSL license
  complicates redistribution; OpenBao (the open fork) is noted as the workaround but was
  not itself evaluated for v1.

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
[`../explanation/security.md`](../explanation/security.md).

## Consequences

- Master key loss = secrets unrecoverable; key backup procedure is mandatory install
  documentation.
- Key rotation = re-wrap data keys; design the schema for it (key-id column) from the
  start.
- Migration tooling from `secrets.vault` should be provided for existing users.
- _(Appended 2026-08-08.)_ **Withdrawn:** the above migration-tooling consequence no
  longer holds. Waypoint will **not** import predecessor `secrets.vault`/`site.json`
  config; all credentials and sites/targets are configured fresh in the UI. Waypoint
  replicates the sibling repos' functionality and borrows code where sensible, but is
  not tied to their data formats. The envelope-encryption decision itself is unchanged;
  only the import path is dropped (tracking issue #246 closed won't-do).
