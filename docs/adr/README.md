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
| [0011](0011-credential-tiers.md) | Credential tiers; ephemeral personal credentials in v1 | Accepted |
| [0012](0012-stage-per-execution-dispatcher.md) | Stage-per-execution dispatcher; resume-from-stage | Accepted |
| [0013](0013-control-plane-and-runners.md) | ASP.NET control plane + dedicated .NET execution runners | Accepted |
| [0014](0014-runner-job-ownership.md) | Runner-owned leases, events, secrets, and resource admission | Accepted |
| [0015](0015-source-build-and-operator-export.md) | Source distribution + operator-built/exported appliances | Accepted |
