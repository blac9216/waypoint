# Architecture Decision Records

Short, numbered records of decisions that shape Waypoint. Once **Accepted**, an ADR is
not rewritten — a new ADR supersedes it and both note the relationship.

| # | Decision | Status |
|---|---|---|
| [0001](0001-packaging.md) | Docker Compose first; optional OVA wrapper later | Accepted |
| [0002](0002-database-postgres.md) | PostgreSQL for app data, catalogs, queue, Keycloak | Accepted |
| [0003](0003-reverse-proxy-nginx.md) | nginx reverse proxy with operator-provided TLS | Accepted |
| [0004](0004-identity-keycloak.md) | Keycloak as IdP; app is a plain OIDC client | Accepted |
| [0005](0005-secrets.md) | Envelope-encrypted secrets in Postgres (AWX pattern) | Accepted |
| [0006](0006-backend-language.md) | ASP.NET Core backend hosting PowerShell in-process | Accepted |
| [0007](0007-frontend.md) | React + TypeScript PWA, zero external assets | Accepted |
| [0008](0008-job-engine.md) | Central job engine on a Postgres-backed queue | Accepted |
| [0009](0009-self-update.md) | Signed update bundles + updater sidecar | Accepted |
| [0010](0010-deployment-topology.md) | One appliance, connected/disconnected modes | Accepted |
