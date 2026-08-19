# Keycloak realm bootstrap (issue #28, ADR-0004)

This directory holds the realm definition Keycloak imports on first boot, and the
scripts that drive the local export/import round-trip. It does **not** hold any
secret material — `waypoint-realm.json` is committed and sanitized; every value that
would otherwise be a secret is templated.

## What's here

- `realm/waypoint-realm.json` — the `waypoint` realm: the four role groups
  (`Viewer`/`Cyber`/`Operator`/`Admin`, matching `docs/domain-model.md` "Roles" and
  the exact PascalCase wire values in `docs/api-contract.md`), one confidential OIDC
  client (`waypoint-backend`) the ASP.NET Core backend will use once #29 swaps local
  auth for the real OIDC flow, a group-membership protocol mapper that puts the
  member group's name on the token as a `role` claim — the same field name and value
  set the API contract already promises, so the eventual Keycloak-backed `/auth/me`
  needs no reshaping — and (issue #521) a `waypoint-auth-time` mapper
  (`oidc-usersessionmodel-note-mapper` reading the `AUTH_TIME` session note) that puts
  the session's real authentication instant on the token as the standard `auth_time`
  claim. Step-up re-authentication (`docs/security.md` "Step-up re-authentication")
  depends on this mapper existing on the access token specifically — without it every
  OIDC-authenticated request fails closed as `step_up_required` on the
  credential-overwrite path, since a missing `auth_time` claim is never treated as
  fresh.
- The client's `secret` field is the literal placeholder `__WAYPOINT_BACKEND_CLIENT_SECRET__` —
  never a real value. `deploy/scripts/keycloak-realm-import.sh` substitutes the real
  secret from `KEYCLOAK_BACKEND_CLIENT_SECRET` (`deploy/.env`, gitignored) into a
  throwaway copy at import time; the templated file in this repo is never edited
  in place.

## Role groups, not realm roles, are what a user is assigned

Each of the four realm roles (`Viewer`/`Cyber`/`Operator`/`Admin`) is granted through
membership in the identically-named top-level group, not by assigning the realm role
to a user directly. Put a user in exactly one of the four groups; that group's single
realm role becomes the `role` claim on their token. This mirrors the strictly-ordered,
single-role-per-user model in `docs/domain-model.md` — there is no user with more than
one of the four roles, so a flat one-group-per-user assignment is sufficient and
deliberately does not model role combination.

## CAC/PIV (x.509) login — site enablement, documented not wired

Waypoint's realm ships with no x.509 authenticator configured, because it depends on
a per-site CA bundle no default realm export can contain. To enable CAC/PIV login on
a real deployment:

1. In the Keycloak admin console, add an **X509/Validate Username Form** execution to
   the realm's browser authentication flow (duplicate "browser" first — don't edit
   the built-in flow in place).
2. Set the certificate identity source to the field the CAC/PIV cert's DoD ID is
   carried in (typically Subject Alternative Name → `otherName`, or a specific
   Subject DN attribute depending on the certificate profile).
3. Import the site's DoD CA certificate chain into Keycloak's truststore
   (`--truststore-paths` / `KC_TRUSTSTORE_PATHS`, mounted read-only — same convention
   as nginx's operator-provided TLS certs in `deploy/nginx/certs`).
4. nginx must be configured to request and forward the client certificate to
   Keycloak (`ssl_verify_client optional_no_ca` + `proxy_set_header
   X-SSL-CERT $ssl_client_cert` or Keycloak's `X509ClientCertificateLookup` via
   the `ssl_client_certificate` header, depending on TLS termination point) — this
   is deployment-specific edge configuration, not part of the realm export, and is
   left for the site to configure alongside their real certificates (same "operator
   supplies real certs from their internal CA" note as `deploy/README.md`'s TLS
   section).

This is genuinely a **per-site** configuration step (different DoD CA chains, PKI
policies, and certificate profiles across DoD components) — it is documented here
rather than pre-wired into the shipped realm so a fresh `compose up` never silently
requires a CA bundle that doesn't exist yet.

## Example LDAP/AD federation config (documented, not wired)

Also not part of the shipped realm, for the same reason — every deployment's LDAP/AD
tree, bind DN, and attribute mapping differs. Example, adapt per site (Keycloak admin
console → User Federation → Add Ldap provider, or the equivalent Admin REST/CLI call):

```
Vendor:                Active Directory
Connection URL:        ldaps://ad.example.internal:636
Bind DN:                CN=svc-waypoint-bind,OU=Service Accounts,DC=example,DC=internal
Bind Credential:        <bind account password -- never committed, deliver via the
                         Keycloak admin console or an operator-held vault, exactly
                         like the master key/admin password conventions in
                         deploy/README.md>
Users DN:               OU=Users,DC=example,DC=internal
Username LDAP attribute: sAMAccountName
RDN LDAP attribute:      cn
UUID LDAP attribute:     objectGUID
User Object Classes:     person, organizationalPerson, user
Sync Registrations:      Off (Waypoint does not create LDAP accounts)
Import Users:            On
Edit Mode:               READ_ONLY
```

After adding the federation provider, map incoming LDAP group membership to the same
four top-level Keycloak groups above (User Federation → your provider → Mappers → add
a `group-ldap-mapper` pointing at the site's AD security groups, or assign users to
the Waypoint groups manually if the site prefers not to mirror AD group structure).

## Round-trip: export/import (local procedure, issue #28 acceptance criteria)

This round-trip is a **local operator procedure**, not a packaged artifact — ADR-0015
explicitly excludes Keycloak from the transfer/update bundle format (that lands with
the M6 bundle format; see the comment on issue #43). The scripts below prove the
export/import path works; they do not produce anything this repository ships.

- `deploy/scripts/keycloak-realm-export.sh` — runs `kc.sh export` in a throwaway
  container against the same database as your compose stack's `keycloak` service and
  copies the result out to a local path you choose. Use this to capture a backup, or
  to refresh `deploy/keycloak/realm/waypoint-realm.json` after an admin-console change
  (re-add the `__WAYPOINT_BACKEND_CLIENT_SECRET__` placeholder and strip any real
  secret before committing — see that script's own output for the reminder).
- `deploy/scripts/keycloak-realm-import.sh` — substitutes a real client secret into a
  throwaway copy of a realm export, deletes the existing `waypoint` realm via the
  admin API, stops the compose-managed `keycloak` service, and re-imports through a
  throwaway server boot against the same database (**not** `kc.sh import` — see the
  script's header comment: that standalone CLI reports success in this Keycloak
  version without actually persisting anything, verified empirically while authoring
  this issue). Restarts the compose-managed service on exit either way. Used both to
  bootstrap a real usable client secret and to prove a prior export re-imports
  cleanly (issue #28's "realm export/import round-trips" acceptance criterion).

See `deploy/README.md` "Keycloak" for the exact bring-up commands.
