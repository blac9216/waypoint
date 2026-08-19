# ADR-0004: Keycloak as identity provider; app is a plain OIDC client

Status: Accepted

## Context

Waypoint needs authentication/SSO suitable for DoD-style environments: CAC/PIV smart
card (x.509) login, AD/LDAP federation, SAML and OIDC — all self-hosted and
air-gap-friendly. Lighter alternatives (Authentik, Dex, local-only auth) considered.

## Decision

Keycloak, backed by the shared Postgres (ADR-0002). The Waypoint backend and frontend
are **plain OIDC relying parties** — no Keycloak-specific APIs in application code, so
the IdP remains swappable. Application roles (Viewer/Cyber/Operator/Admin — see
`domain-model.md`) map from IdP groups/claims.

## Rationale

- CAC/PIV x.509 auth + LDAP federation + SAML/OIDC in one self-hosted product is
  exactly the DoD requirement set, and Keycloak is the strongest open-source answer.
- The ~1 GB JVM footprint and upgrade cadence are real costs, accepted for the above.

## Rollout note

Local auth (backend-issued sessions) was acceptable for the first development
milestone (see `roadmap.md`). Issue #29 landed the OIDC bearer-validation swap:
Keycloak is now the production sign-in path. Local auth survives only as an
explicit, off-by-default dev-flag (`LocalAuth:Enabled` — see `deploy/README.md`
"Local auth (dev-flag)") for the e2e/smoke-test paths that have not yet moved to a
real interactive OIDC login flow; it is not a supported deployment configuration.

## Consequences

- Realm export/import must be part of install/backup/update bundles.
- Keycloak is on the update treadmill; the update bundle format must carry it.
