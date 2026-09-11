# ADR-0004: Keycloak as identity provider; app is a plain OIDC client

Status: Accepted
Date: 2026-08-02

## Context

Waypoint needs authentication/SSO suitable for DoD-style environments: CAC/PIV smart
card (x.509) login, AD/LDAP federation, SAML and OIDC — all self-hosted and
air-gap-friendly. Lighter alternatives (Authentik, Dex, local-only auth) considered.

## Decision Drivers

_Backfilled under ADR-0027 from #28, #29._

- CAC/PIV x.509 smart-card login, AD/LDAP federation, and SAML/OIDC support in one
  self-hosted, air-gap-friendly product — the DoD-style requirement set named in
  Context above.
- The application must stay a plain OIDC relying party — no Keycloak-specific APIs in
  application code — so the IdP remains swappable. Confirmed downstream by #29
  (PR #527): the OIDC swap added only the JWT validation and claims-mapping layer;
  the existing `[Require*Role]` guard layer was left untouched.
- Keycloak has to run as an ordinary compose service with its own owned database, no
  master-key mount, and no runner database roles (#28).

## Considered Options

_Backfilled under ADR-0027 from #28, #29._

1. **Keycloak (chosen)** — the only option considered that covers CAC/PIV x.509 login,
   AD/LDAP federation and SAML/OIDC in one self-hosted product; the ~1 GB JVM footprint
   and update cadence are accepted costs (see Decision and Consequences below).
2. **Authentik** — named in the ADR's own Context (above) as a lighter-weight
   alternative. The sources record no further comparison: no issue or PR discusses why
   it was rejected in favor of Keycloak.
3. **Dex** — named alongside Authentik as a lighter alternative in Context; likewise no
   sourced comparison beyond the naming.
4. **Local-only auth** — the option actually run first: acceptable for the M1
   development milestone per the Rollout note below, then replaced as the production
   sign-in path by #29 (PR #527), which kept local auth only as an explicit,
   off-by-default dev-flag (`LocalAuth:Enabled`) for e2e/smoke-test paths that have not
   moved to a real interactive OIDC login flow.

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
explicit, off-by-default dev-flag (`LocalAuth:Enabled` — see
`docs/rationale/deploy.md#override-dev-bootstrap-local-auth-design`) for the e2e/smoke-test paths that have not yet moved to a
real interactive OIDC login flow; it is not a supported deployment configuration.

## Consequences

- Realm export/import must be part of install/backup/update bundles.
- Keycloak is on the update treadmill; the update bundle format must carry it.
