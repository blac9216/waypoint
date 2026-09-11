# ADR-0003: nginx reverse proxy with operator-provided TLS

Status: Accepted
Date: 2026-08-02

## Context

The stack needs a single TLS entry point serving the static frontend, proxying `/api`
to the backend and auth paths to Keycloak. Candidates: nginx, Traefik, Caddy.

## Decision Drivers

_Backfilled under ADR-0027 from #2._ The sources record no issue or PR that captures the
original evaluation session — this ADR was authored in the repo's initial skeleton
commit ("Add architecture docs, ADRs, Claude workflow skills, and repo skeleton"),
which predates any tracked issue or PR. #2 is the earliest tracked issue that names
ADR-0003, and confirms the drivers below by consuming them directly. The bullets
themselves are drawn from this ADR's own original Context and Rationale text, not
invented:

- A single TLS entry point is needed, serving the static frontend, proxying `/api` to
  the backend and auth paths to Keycloak (Context, above).
- The target deployment is air-gapped, so a proxy's automatic-ACME capability has no
  path to a certificate authority it can reach.
- The appliance has a fixed topology (not a dynamically-scaled container fleet), so
  container-label-driven service discovery is unneeded indirection.
- The target audience's operators are already familiar with nginx, and DISA publishes
  hardening guidance for it.
- SSE endpoints need response buffering disabled at the proxy.

## Considered Options

_Backfilled under ADR-0027 from #2._ As with Decision Drivers above, no issue or PR
beyond this ADR's own original Rationale text records the comparison; #2 is the
earliest tracked issue naming ADR-0003. nginx was chosen; Traefik and Caddy were
rejected. The bullets below are that original Rationale text, moved verbatim:

- Caddy's headline feature (automatic ACME/Let's Encrypt) is useless air-gapped;
  Traefik's (container label discovery) adds indirection a fixed-topology appliance
  doesn't need.
- nginx is ubiquitous in the target audience's world, and DISA publishes hardening
  guidance for it.

## Decision

nginx, terminating TLS with **operator-provided certificates** (internal CA), serving
the frontend static bundle, and proxying backend + Keycloak. SSE endpoints proxied with
buffering disabled.

## Consequences

- Cert/key rotation is an operator task; the appliance must document (and ideally
  surface in the UI) cert expiry.
- `proxy_buffering off` (or per-location equivalent) required on SSE routes.
- **Backend re-resolution depends on Docker's embedded DNS.** *(Appended after
  acceptance — issue #74, PR #84, 2026-08-03. A consequence of the standing
  decision, not a change to it: Context and Decision above are untouched. See
  `docs/adr/README.md`, "Amending an accepted ADR".)* The compose config
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
  `docs/rationale/deploy.md#nginx-dynamic-backend-resolution` for the porting note.
- **Repo path-space locations carve out of app-path mTLS.** *(Appended after
  acceptance — issue #1043 (design record), #1502, 2026-08-30. A consequence of the
  standing decision, not a change to it.)* This ADR's Decision and Consequences above
  describe nginx's TLS posture for the app paths (`/api/`, `/auth/`, the static
  frontend). The read-only repo path-space (depot/UMDS/Photon/VMTools/VKS/content
  libraries, `deploy/nginx/conf.d/default.conf`) is a distinct set of locations this
  ADR never covered: they carry no client-certificate requirement at all, now or as a
  reserved future toggle — `ssl_verify_client optional` is left commented out and
  explicitly absent on every repo location, documented as the seam a later
  per-location auth toggle attaches to. See
  `docs/rationale/deploy.md#nginx-repo-mtls-carve-out` for the full reasoning.
