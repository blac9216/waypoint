# Architecture Decision Records

Short, numbered records of decisions that shape Waypoint. Once **Accepted**, an ADR is
not rewritten — a new ADR supersedes it and both note the relationship.

## Amending an accepted ADR

"Not rewritten" governs the **Context** and **Decision** sections. Those record what was
decided and why; editing them falsifies history. If the decision itself changes,
supersede it.

**Consequences may be appended to.** A consequence is something you *learn* by living
with a decision, so an accepted ADR that can never gain one goes stale by design — and
superseding an ADR merely to record a discovered implication fills the log with noise
that hides the real reversals.

An append to Consequences must:

- leave Context and Decision byte-identical;
- record a consequence *of* the standing decision, never a new or narrowed one — if you
  are writing "instead" or "no longer", you need a superseding ADR;
- be attributable in the PR to the issue or person that established it. Do not cite a
  reviewer as endorsing a convention unless they actually did: a reviewer raising a
  question is not a reviewer answering it.

| # | Decision | Status |
|---|---|---|
| [0001](0001-packaging.md) | Docker Compose first; optional OVA wrapper later | Accepted; delivery superseded by 0015 |
| [0002](0002-database-postgres.md) | PostgreSQL for app data, catalogs, queue, Keycloak | Accepted |
| [0003](0003-reverse-proxy-nginx.md) | nginx reverse proxy with operator-provided TLS | Accepted |
| [0004](0004-identity-keycloak.md) | Keycloak as IdP; app is a plain OIDC client | Accepted |
| [0005](0005-secrets.md) | Envelope-encrypted secrets in Postgres (AWX pattern) | Accepted |
| [0006](0006-backend-language.md) | ASP.NET Core backend hosting PowerShell in-process | Superseded by 0013 |
| [0007](0007-frontend.md) | React + TypeScript PWA, zero external assets | Accepted |
| [0008](0008-job-engine.md) | Central job engine on a Postgres-backed queue | Accepted; worker ownership superseded by 0013/0014 |
| [0009](0009-self-update.md) | Signed update bundles + updater sidecar | Accepted; clarified by 0015 |
| [0010](0010-deployment-topology.md) | One appliance, connected/disconnected modes | Accepted; clarified by 0015 |
| [0011](0011-credential-tiers.md) | Credential tiers; ephemeral personal credentials in v1 | Accepted; storage model superseded by 0016 |
| [0012](0012-stage-per-execution-dispatcher.md) | Stage-per-execution dispatcher; resume-from-stage | Accepted |
| [0013](0013-control-plane-and-runners.md) | ASP.NET control plane + dedicated .NET execution runners | Accepted; §2 job-type placement for content-pull/content-import superseded by 0017 |
| [0014](0014-runner-job-ownership.md) | Runner-owned leases, events, secrets, and resource admission | Accepted; §5 per-runner admission superseded by 0018 |
| [0015](0015-source-build-and-operator-export.md) | Source distribution + operator-built/exported appliances | Accepted |
| [0016](0016-run-scoped-personal-credential-persistence.md) | Personal credentials persist encrypted, run-scoped, terminal/expiry bounded | Accepted; one-row-per-run particular superseded by 0021 |
| [0017](0017-compliance-content-runner-placement.md) | `content-pull`/`content-import` run in compliance-runner, not download-runner | Accepted |
| [0018](0018-shared-capacity-lease-pool.md) | Host-derived capacity discovery, startup admission invariant, shared capacity lease pool (design) | Accepted |
| [0019](0019-global-job-observability.md) | Global Live Jobs observability with domain-owned results | Accepted |
| [0020](0020-capacity-lease-pool-protocol.md) | Capacity lease pool protocol, recovery, and fairness policy | Accepted |
| [0021](0021-credential-purpose-matrix.md) | Credential-purpose matrix — explicit purposes, not numbered slots | Accepted; §§4–7 narrowed/superseded by 0024 |
| [0022](0022-compliance-catalog-and-content-lifecycle.md) | Closed compliance catalog and atomic content lifecycle | Accepted (planned) |
| [0023](0023-compliance-inventory-and-immutable-plans.md) | Stable compliance inventory and immutable component plans | Accepted (planned) |
| [0024](0024-compliance-execution-attempts-credentials-and-settings.md) | Compliance execution items, attempts, credentials, and control settings | Accepted (planned); supersedes parts of 0021 |
| [0025](0025-compliance-trust-cleanup-and-evidence.md) | Compliance trust, temporary access cleanup, and evidence lifecycle | Accepted (planned) |

## Former milestone numbering

Accepted ADRs are immutable, so the ones written before 2026-08-29 still refer to
milestones by number. The stories they mean (see
[`../process/work-tracking.md`](../process/work-tracking.md) and the GitHub milestones):

| Old label | Delivery story |
|---|---|
| M0 | Design & contracts (planning phase; no milestone) |
| M1 | [Foundation & download slice](https://github.com/blac9216/waypoint/milestone/3) |
| M2 | [Sites, credentials & STIG scan slice](https://github.com/blac9216/waypoint/milestone/4) — followed by [Runner realignment](https://github.com/blac9216/waypoint/milestone/5) |
| M3 | [Identity, RBAC & scheduling](https://github.com/blac9216/waypoint/milestone/6) |
| M4 | [Remediation](https://github.com/blac9216/waypoint/milestone/12) |
| M5 | [Download & depot parity](https://github.com/blac9216/waypoint/milestone/11) |
| M6 | [Transfer & enclave modes](https://github.com/blac9216/waypoint/milestone/13) |
| M7 | [Self-update & appliance packaging](https://github.com/blac9216/waypoint/milestone/14) |
