# Architecture Decision Records

Short, numbered records of decisions that shape Waypoint. Once **Accepted**, an ADR is
not rewritten — a new ADR supersedes it and both note the relationship.

**Read the index first.** Open only the ADRs whose status is Accepted and whose subject
you need; superseded ADRs are history, cited only to explain a change.

ADR-0027 authorises a one-time normalisation of 0001–0025 into the MADR frame;
backfilled sections are marked as such.

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

<!-- adr-index:start -->
| # | Title | Status | Supersedes | Superseded by | Amends | Amended by | Decision |
|---|---|---|---|---|---|---|---|
| [0001](0001-packaging.md) | Docker Compose first; optional OVA wrapper later | Accepted | - | - | - | - | 1. **v1 ships as a Docker Compose stack.** Air-gapped delivery is a tarball: |
| [0002](0002-database-postgres.md) | PostgreSQL for app data, catalogs, job queue, and Keycloak | Accepted | - | - | - | - | One PostgreSQL instance (16+), with separate databases for the app and Keycloak. |
| [0003](0003-reverse-proxy-nginx.md) | nginx reverse proxy with operator-provided TLS | Accepted | - | - | - | - | nginx, terminating TLS with **operator-provided certificates** (internal CA), serving |
| [0004](0004-identity-keycloak.md) | Keycloak as identity provider; app is a plain OIDC client | Accepted | - | - | - | - | Keycloak, backed by the shared Postgres (ADR-0002). The Waypoint backend and frontend |
| [0005](0005-secrets.md) | Envelope-encrypted secrets in Postgres (AWX pattern) | Accepted | - | - | - | - | Application-managed envelope encryption, the pattern proven by Ansible AWX: |
| [0006](0006-backend-language.md) | ASP.NET Core (C#) backend hosting PowerShell in-process | Superseded | - | - | - | - | **ASP.NET Core (C#)** for the backend. The backend hosts PowerShell **in-process** via |
| [0007](0007-frontend.md) | React + TypeScript PWA with zero external assets | Accepted | - | - | - | - | - **React + TypeScript**, built with Vite to a fully static bundle served by nginx. |
| [0008](0008-job-engine.md) | Central job engine on a Postgres-backed queue | Accepted | - | - | - | - | One **job engine** in the backend serves all job types: |
| [0009](0009-self-update.md) | Signed update bundles applied by a dedicated updater sidecar | Accepted | - | - | - | - | - **Update bundle**: signed tarball — images (`docker save`), compose file, manifest |
| [0010](0010-deployment-topology.md) | One appliance, connected/disconnected modes, bundle-based transfer | Accepted | - | - | - | - | One appliance image, deployed per enclave, with an instance-level **mode**: |
| [0011](0011-credential-tiers.md) | Credential tiers — ephemeral personal credentials in v1 | Accepted | - | - | - | - | Two credential tiers with different storage models: |
| [0012](0012-stage-per-execution-dispatcher.md) | Stage-per-execution dispatcher and resume-from-stage | Accepted | - | - | - | - | 1. **`queued` stays the only claimable, unleased state.** No new job-engine state is |
| [0013](0013-control-plane-and-runners.md) | Separate the control plane from dedicated execution runners | Accepted | - | - | - | - | 1. **ASP.NET is the control plane only.** `Waypoint.Api` owns REST/SSE, authentication |
| [0014](0014-runner-job-ownership.md) | Runners own job leases, execution events, and resource admission | Accepted | - | - | - | - | 1. **Runners claim work directly from PostgreSQL.** The atomic |
| [0015](0015-source-build-and-operator-export.md) | Distribute source; operators build, provision, and export appliances | Accepted | - | - | - | - | 1. **The project publishes source and build definitions, not completed container |
| [0016](0016-run-scoped-personal-credential-persistence.md) | Personal credentials persist encrypted, run-scoped, terminal/expiry bounded | Accepted | - | - | - | - | 1. **Personal credentials persist, encrypted, in a dedicated run-scoped table.** One |
| [0017](0017-compliance-content-runner-placement.md) | Compliance-content pull/import execute in the compliance-runner | Accepted | - | - | - | - | `content-pull` and `content-import` are `compliance-runner` job types, claimed under |
| [0018](0018-shared-capacity-lease-pool.md) | Host-derived capacity discovery, a startup admission invariant, and a shared capacity lease pool | Accepted | - | - | - | - | 1. **Host-derived capacity replaces the 1-CPU/1-GiB fallback when cgroup limits are |
| [0019](0019-global-job-observability.md) | Global job observability with domain-owned results | Accepted | - | - | - | - | 1. **Live Jobs is global operational observability.** A top-level workspace lists |
| [0020](0020-capacity-lease-pool-protocol.md) | Capacity lease pool protocol, recovery, and fairness policy | Accepted | - | - | - | - | 1. **Schema (migration 0036).** A singleton `capacity_pool` row holds the appliance's |
| [0021](0021-credential-purpose-matrix.md) | Credential-purpose matrix — explicit purposes, not numbered slots | Accepted | - | - | - | - | ### 1. Credential purposes are explicit, named identifiers — never numbered slots |
| [0022](0022-compliance-catalog-and-content-lifecycle.md) | Closed compliance catalog and atomic content lifecycle | Accepted | - | - | - | - | ### Catalog authority and exact baselines |
| [0023](0023-compliance-inventory-and-immutable-plans.md) | Stable compliance inventory and immutable component plans | Accepted | - | - | - | - | ### Identity and provenance |
| [0024](0024-compliance-execution-attempts-credentials-and-settings.md) | Compliance execution items, attempts, credentials, and control settings | Accepted | - | - | - | - | ### Component job and ordered-attempt hierarchy |
| [0025](0025-compliance-trust-cleanup-and-evidence.md) | Compliance trust, temporary access cleanup, and evidence lifecycle | Accepted | - | - | - | - | ### Connection-scoped trust |
| [0026](0026-adopt-design-docs-standard.md) | Adopt the design-docs standard | Proposed | - | - | - | - | Waypoint adopts the design-docs standard as its architecture-documentation framework. |
| [0027](0027-normalise-adrs-to-madr.md) | Normalise ADRs 0001–0025 to the MADR frame | Proposed | - | - | - | - | Option 3. This ADR authorises a single, uniform normalisation pass over ADRs 0001–0025: |
<!-- adr-index:end -->

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
