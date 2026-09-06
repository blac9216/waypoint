# ADR-0031: Repo serving surface — per-location independent auth, Waypoint-managed credentials

Status: Accepted
Date: 2026-09-06

## Context

Waypoint's app paths are protected by Keycloak-issued OIDC/CAC-mTLS (ADR-0004); the
depot/repo paths served for VCF/SDDC Manager, vLCM, vCenter subscribed libraries, and
`tdnf`/package-manager consumers are a different trust boundary entirely — those
consumers are machines, not interactive users, and each has its own fixed
authentication capability that Waypoint does not control. Research (#1027, ratified
2026-08-29, revising owner grill decision 15/R2-7) found the consumers are not
uniform: vLCM's patch URL is **anonymous-only**; vCenter subscribed content libraries
accept **Basic-or-none** and never present a client certificate on repo paths; SDDC
Manager 9.1 is Basic-over-HTTPS/anonymous with a first-class `basePath`. A single
serving auth policy across every store would either lock out an anonymous-only
consumer or leave a Basic-capable one unnecessarily open.

## Decision Drivers

- CAC/mTLS enforcement on application paths must never bleed into repo paths — a
  machine consumer that cannot present a client certificate must not be locked out by
  the app's own auth mechanism reaching further than intended.
- Each store's achievable auth level is a fact about its consumer, not a Waypoint
  preference — the serving layer must be configurable per store, not fixed globally.
- Keycloak already anchors interactive app identity (ADR-0004); introducing it into
  machine-to-repo auth would require every consumer to speak OIDC, which none of them
  do.
- An operator must be able to see, per store, whether the configured auth level is
  actually usable by that store's real consumer, before a working integration breaks
  silently against a policy the consumer cannot satisfy.

## Considered Options

1. **One global repo auth policy** (e.g., Basic everywhere, or anonymous everywhere).
   Simplest to implement and reason about, but forces either the anonymous-only
   vLCM consumer to be excluded from any protected store, or every other store down to
   the weakest consumer's capability.
2. **Route repo auth through Keycloak, same as the app.** Reuses existing
   infrastructure, but no repo consumer here (vLCM, subscribed libraries, SDDC
   Manager, `tdnf`) speaks OIDC; this would require a translation layer with no
   research-confirmed target to translate to.
3. **Per-location independent auth dials, enforced at nginx, Waypoint-managed repo
   credentials as the default** (this decision). Each store/location gets its own
   auth setting, from anonymous to Basic-authed, driven by Waypoint-issued repo
   users/tokens rather than Keycloak; a store whose consumer cannot authenticate
   ships anonymous by default with a warning badge; a blocking rule requires Basic
   only ever be offered over HTTPS.

## Decision

nginx enforces auth per `location` (one per store, ADR-0029), independent of the app's
CAC/mTLS handling (`ssl_verify_client optional` at the server level, enforced only on
app-path locations). Default credential model is **Waypoint-managed repo users/tokens**,
issued and rotated by the appliance and enforced at nginx — Keycloak stays out of the
serving path entirely (ADR-0004 is unaffected: it continues to govern interactive app
sign-in only). Every store's auth dial is independently configurable from anonymous to
Basic-authed; a store whose known consumer cannot authenticate at all (the ESX/patch
store's vLCM consumer) ships anonymous by default. The UI surfaces a warning badge on
any store configured with an auth level its known consumer cannot satisfy, and shows
the thumbprint/cert-chain for any store advertising HTTPS-authenticated access.
Basic auth is never offered over plain HTTP — a store must be HTTPS before Basic can be
enabled on it. This is the serving-surface half of Epic #16 decision 15, as ratified
and amended by research (#1027); issue #1043 is its implementation.

## Consequences

- Repo credential management (issuance, rotation, per-store binding) is new Waypoint
  application state distinct from both Keycloak accounts and the legacy Download
  Token — it needs its own storage and RBAC (Admin-only, per decision 8/ADR pending on
  RBAC in #1034).
- A store cannot be assumed authenticated just because "the appliance has auth" — every
  consumer integration must be validated against that specific store's configured dial,
  which is why the warning badge exists.
- Anonymous-by-default stores (ESX/patch for vLCM) remain a real, intentional exposure
  inside the served repo path-space; operators relying on network-layer isolation for
  those stores must configure it themselves — Waypoint does not add an auth layer a
  real consumer cannot use.
