# deploy

Deployment artifacts: Docker Compose stack, nginx configuration, updater sidecar,
install/upgrade scripts, and (later) bundle tooling + Packer OVA build. See
[ADR-0001](../docs/adr/0001-packaging.md),
[ADR-0009](../docs/adr/0009-self-update.md), and
[ADR-0010](../docs/adr/0010-deployment-topology.md).

## Dev compose stack (M1, epic [#1](https://github.com/blac9216/waypoint/issues/1))

`docker-compose.yml` stands up the M1 development topology from
[`docs/architecture.md`](../docs/architecture.md) (component view) and
[ADR-0003](../docs/adr/0003-reverse-proxy-nginx.md):

```
browser --TLS--> nginx --/api--> backend --> postgres (internal network only)
                    \--> static frontend bundle (deploy/www/)
```

- **nginx** terminates dev TLS, serves the frontend static bundle from
  `deploy/www/`, and reverse-proxies `/api/` to the backend. SSE routes
  (`/api/v1/events`, `/api/v1/runs/{id}/events` — see
  [`docs/api-contract.md`](../docs/api-contract.md#event-streams-sse)) are
  proxied with `proxy_buffering off` per ADR-0003, so the browser's
  `EventSource` sees events as they're written instead of once nginx's
  buffer fills or the upstream closes.
- **backend** is currently a **placeholder** (see "Backend stub" below) —
  the real ASP.NET Core backend lands in issue
  [#3](https://github.com/blac9216/waypoint/issues/3).
- **postgres** (16) sits on an `internal: true` compose network with no
  published port — reachable only from `backend`, never from the host or
  from `nginx`.

All three services declare a Docker `healthcheck`.

### Backend stub (TODO: issue #3)

`backend-stub/conf.d/default.conf` runs plain nginx as a stand-in backend so
the compose topology, healthchecks, and the `/api` proxy path can be proven
today without the real backend existing yet. It answers:

- `GET /api/v1/health` → `200 {"status":"ok","service":"backend-stub"}`
- any other `/api/...` path → `501 {"error":"not_implemented", ...}`

When issue #3 lands, swap the `backend` service's `image`/`build` in
`docker-compose.yml` for the real backend image — its service name
(`backend`), the `/api/v1/health` contract, and its position on the `edge`
+ `internal` networks are the interface the rest of the stack depends on;
keep those stable across the swap.

### Bring-up

Prerequisites: Docker Engine + Compose v2, `openssl`.

1. Generate a dev-only self-signed TLS cert (git-ignored output):

   ```bash
   deploy/nginx/certs/generate-dev-certs.sh
   ```

2. (Optional) override the dev Postgres credentials / published HTTPS port.
   These are throwaway values for a database that's reachable only on the
   internal compose network — copy them into a git-ignored `.env` file next
   to `docker-compose.yml` (`deploy/.env`) if you want non-default values:

   ```bash
   # deploy/.env
   POSTGRES_USER=waypoint
   POSTGRES_PASSWORD=waypoint_dev_only
   POSTGRES_DB=waypoint
   WAYPOINT_HTTPS_PORT=8443
   ```

3. Bring up the stack from `deploy/`:

   ```bash
   cd deploy
   docker compose up -d
   docker compose ps          # all three should report "healthy"
   ```

4. Verify:

   ```bash
   # Placeholder frontend page, over TLS (dev cert is self-signed -> -k)
   curl -k https://localhost:8443/

   # /api/v1/health, proxied through nginx to the backend stub
   curl -k https://localhost:8443/api/v1/health

   # Postgres is not reachable from the host at all (connection refused)
   curl http://localhost:5432/
   ```

5. Tear down:

   ```bash
   docker compose down       # add -v to also drop the named volumes
   ```

### `dev/local/` convention (gitignored)

Per `CLAUDE.md`'s local-testing rule, this repo's own test depot
token/config and the hand-provisioned `vcf-download-tool` binary are
**never** committed here. They're borrowed at runtime from the private
sibling repo (`vcf-docker-download`) by mounting them from a repo-root
`dev/local/` directory, which is git-ignored (see the root `.gitignore`
entry `dev/local/`) and not created by this repo — you populate it
yourself, locally, from your own copy of the sibling repo.

The convention: `dev/local/` is the mount point a Waypoint backend
container uses to reach that borrowed material, e.g.

```
dev/local/
├── depot-token          # borrowed Broadcom depot token, dev/test use only
├── depot-config.json    # borrowed depot/site config
└── vcf-download-tool     # hand-provisioned binary (never bundled - see
                          # CLAUDE.md License & Borrowing Policy)
```

The current `backend` service is a placeholder that doesn't consume any of
this yet, so `docker-compose.yml` has the mount commented out. Once issue
[#3](https://github.com/blac9216/waypoint/issues/3) (and, later, the
download-job work in epic #1) lands, uncomment the `backend` service's
`../dev/local:/dev/local:ro` volume line.

**Never** copy anything out of `dev/local/` into a committed file, fixture,
log, or doc — see the sanitization policy in the repo root `CLAUDE.md`.

### Notes

- The dev TLS cert is self-signed (`CN=localhost`) and meant only for this
  local loop; the operator supplies real certificates from their internal
  CA in any real deployment (ADR-0003).
- `nginx` binds IPv4 only (`listen 443 ssl;`, no `listen [::]:443`) — some
  Docker network configurations (including the sandbox this stack was
  verified in) have no IPv6 route, and nginx refuses to start at all if a
  `listen [::]` directive can't bind.
