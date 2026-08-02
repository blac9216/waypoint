# ADR-0003: nginx reverse proxy with operator-provided TLS

Status: Accepted

## Context

The stack needs a single TLS entry point serving the static frontend, proxying `/api`
to the backend and auth paths to Keycloak. Candidates: nginx, Traefik, Caddy.

## Decision

nginx, terminating TLS with **operator-provided certificates** (internal CA), serving
the frontend static bundle, and proxying backend + Keycloak. SSE endpoints proxied with
buffering disabled.

## Rationale

- Caddy's headline feature (automatic ACME/Let's Encrypt) is useless air-gapped;
  Traefik's (container label discovery) adds indirection a fixed-topology appliance
  doesn't need.
- nginx is ubiquitous in the target audience's world, and DISA publishes hardening
  guidance for it.

## Consequences

- Cert/key rotation is an operator task; the appliance must document (and ideally
  surface in the UI) cert expiry.
- `proxy_buffering off` (or per-location equivalent) required on SSE routes.
