# Keycloak realm bootstrap

This directory holds the realm definition Keycloak imports on first boot, and
the scripts that drive the local export/import round-trip. It holds no
secret material — `realm/waypoint-realm.json` is committed and sanitized;
every value that would otherwise be a secret is templated.

## What's here

- `realm/waypoint-realm.json` — the `waypoint` realm: four role groups
  (`Viewer`/`Cyber`/`Operator`/`Admin`), a confidential `waypoint-backend`
  OIDC client the backend uses, and a public `waypoint-frontend` client
  (PKCE, no secret) the React SPA uses. A group-membership mapper on each
  client puts the member group's name on the token as a `role` claim; a
  `waypoint-auth-time` mapper carries the session's real authentication
  instant as `auth_time` (needed for step-up re-authentication).
- Both clients' `rootUrl`/`redirectUris`/`webOrigins` are the literal
  placeholder `${WAYPOINT_PUBLIC_URL}`, substituted at import time from the
  compose-level `WAYPOINT_PUBLIC_URL` value — never a wildcard, since a
  wildcard redirect/origin on a public PKCE client is exactly what to avoid.
  Substitution happens once, at import: changing `WAYPOINT_PUBLIC_URL` after
  the realm already exists in a persisted volume has no effect until a fresh
  volume (see "Round-trip" below).
- The client's `secret` field is the placeholder
  `${WAYPOINT_BACKEND_CLIENT_SECRET}`, resolved the same way from a mounted
  secret file — never a real value in this repo.

## Role groups, not realm roles

Each of the four realm roles is granted through membership in the
identically-named top-level group, not by assigning the role to a user
directly — put a user in exactly one of the four groups.

## CAC/PIV (x.509) login — site enablement, not wired

The shipped realm has no x.509 authenticator configured; it depends on a
per-site CA bundle no default export can contain. To enable on a real
deployment: add an X509/Validate Username Form execution to a duplicated
browser flow, point it at the field carrying the CAC/PIV DoD ID, import the
site's DoD CA chain into Keycloak's truststore
(`KC_TRUSTSTORE_PATHS`), and configure nginx to forward the client
certificate to Keycloak. This is genuinely per-site (different CA chains
and certificate profiles across components), so it stays documented rather
than pre-wired.

## Example LDAP/AD federation (documented, not wired)

Same reasoning — every deployment's tree, bind DN, and attribute mapping
differs. Add via Keycloak admin console → User Federation → Add Ldap
provider (Vendor: Active Directory, `ldaps://` connection URL, a bind
service account, `Import Users: On`, `Edit Mode: READ_ONLY`), then map
incoming LDAP group membership to the four Waypoint groups above via a
`group-ldap-mapper`.

## Round-trip: export/import (local procedure)

A local operator procedure, not a packaged artifact — Keycloak is excluded
from the transfer/update bundle format.

- `deploy/scripts/keycloak-realm-export.sh` — runs `kc.sh export` in a
  throwaway container against the same database as your compose stack's
  `keycloak` service. Use to capture a backup or refresh
  `realm/waypoint-realm.json` after an admin-console change (re-add the
  `${WAYPOINT_BACKEND_CLIENT_SECRET}` placeholder and strip any real secret
  before committing).
- `deploy/scripts/keycloak-realm-import.sh` — substitutes a real client
  secret into a throwaway copy of a realm export, deletes the existing
  `waypoint` realm via the admin API, and re-imports through a throwaway
  server boot against the same database (not `kc.sh import` — that CLI
  reports success without actually persisting on this Keycloak version).
  Restarts the compose-managed service on exit either way.

See `deploy/README.md` for the exact bring-up commands and the
`WAYPOINT_PUBLIC_URL` var-reference entry.
