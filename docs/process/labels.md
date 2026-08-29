# Labels

The canonical set (types, severity, priority, concern, regression, backlog, deferred, help, question, documentation) is provisioned by the configure-workflow skill and is a closed set — never invent a label. This file owns the repo-specific **area** set: what part of the codebase an issue touches, so orchestrators can deconflict parallel work. At least one per issue; several when the work is cross-cutting. Keep it coarse.

| Label | Colour | Covers |
|---|---|---|
| area:backend | d1e8ff | `backend/` control plane — Waypoint.Api, Core, Infrastructure |
| area:compliance-runner | d1e8ff | Waypoint.ComplianceRunner, `runners/compliance-runner/`, its PowerShell modules |
| area:download-runner | d1e8ff | Waypoint.DownloadRunner, `runners/download-runner/` |
| area:db | d1e8ff | migrations, schema ledger, role grants — a shared sequence resource; serialise |
| area:frontend | d1e8ff | `frontend/` |
| area:deploy | d1e8ff | `deploy/`, compose, nginx, keycloak, `dev/` |
| area:ci | d1e8ff | `.github/` workflows, `scripts/`, tooling and format debt |
| area:docs | d1e8ff | `docs/`, AGENTS.md, README, process |
| area:tests | d1e8ff | test infrastructure and flakes not owned by one component — Waypoint.Tests fixtures, e2e harness |

Adding an area: add a row here, then run `configure-workflow/scripts/labels.sh`.
