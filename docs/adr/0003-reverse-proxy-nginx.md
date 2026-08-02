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
- **Backend re-resolution depends on Docker's embedded DNS.** The compose config
  points every `proxy_pass` at a `$backend_host` variable (never a literal
  `backend:8080`) and pairs it with `resolver 127.0.0.11 valid=10s ipv6=off;` so
  nginx re-resolves the backend's container IP on every request instead of caching
  a single lookup for the life of the worker process — required because a backend
  container recreate (ADR-0009 self-update, or any redeploy) gets a new IP on the
  `edge` bridge network, and without re-resolution every route would 502 against
  the stale address until `nginx -s reload`. `127.0.0.11` is Docker-specific: it
  only answers inside a Docker network namespace on a user-defined bridge network.
  The constraint is therefore on **Docker's embedded DNS**, not on the packaging:
  the ADR-0001 OVA wrapper (issue #47) is explicitly "the identical compose stack"
  in a Packer-built appliance — it still runs Docker, so it keeps `127.0.0.11` and
  needs no change here. What does need reworking is any topology that stops using
  Docker networking: a different container runtime (Podman's `aardvark-dns`,
  Kubernetes cluster DNS), or running the services directly on a host under
  systemd where `backend` is not a resolvable name at all. Those must repoint
  `resolver` at that environment's real DNS server — keeping the `$backend_host`
  pairing, since addresses still change on redeploy — or move to static addressing
  if the backend gets a stable address. See
  `deploy/nginx/conf.d/default.conf` for the directive and the full reasoning, and
  `deploy/README.md` for the operator-facing note.
