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
                    \--> static frontend bundle (frontend/dist/, bind-mounted)
```

- **nginx** terminates dev TLS, serves the frontend static bundle bind-mounted
  from `../frontend/dist` (the real Vite build output — see "Bring-up" below),
  and reverse-proxies `/api/` to the backend. SSE routes (`/api/v1/events`,
  `/api/v1/runs/{id}/events` — see
  [`docs/api-contract.md`](../docs/api-contract.md#event-streams-sse)) are
  proxied with `proxy_buffering off` per ADR-0003, so the browser's
  `EventSource` sees events as they're written instead of once nginx's
  buffer fills or the upstream closes. See "Verifying SSE streaming" below
  for a durable way to check this holds.
- **backend** is the real ASP.NET Core API (issue #3), built from `../backend`.
  Local auth (ADR-0004 rollout note) is a dev-grade single admin user with no
  compiled-in default password — see "Bring-up" step 2 to enable login.
- **postgres** (16) sits on an `internal: true` compose network with no
  published port — reachable only from `backend`, never from the host or
  from `nginx`.

All three services declare a Docker `healthcheck`.

### Networking: nginx depends on Docker's embedded DNS

`deploy/nginx/conf.d/default.conf` re-resolves the `backend` container's
address on every request via `resolver 127.0.0.11 valid=10s ipv6=off;` paired
with a `$backend_host` variable in every `proxy_pass` (see the config file's
own comment for the full mechanics). This matters beyond nginx internals for
one reason: `127.0.0.11` is **Docker's embedded DNS server** — it only exists
inside a Docker network namespace on a user-defined bridge network like this
stack's `edge` network. It is not optional: without per-request
re-resolution, recreating the `backend` container (a redeploy, or the
ADR-0009 self-update flow) gives it a new IP on `edge`, and nginx keeps
dialling the stale one — every route 502s until `nginx -s reload`. The
`valid=10s` TTL is what bounds that window; drop it and a stale-address 502
persists indefinitely.

This is a **porting landmine** for any deployment mode that isn't Docker
Compose. ADR-0001 anticipates a Packer-built OVA wrapper (issue #47) running
this same compose stack on Photon OS under systemd — that environment has no
`127.0.0.11` to ask. Reworking the network layer outside Docker (a different
container runtime, or the OVA path) must point `resolver` at that host's real
DNS resolver, or replace the dynamic-target approach with static addressing.
See [ADR-0003](../docs/adr/0003-reverse-proxy-nginx.md#consequences) for the
same note recorded against the reverse-proxy decision.

### Bring-up

Prerequisites: Docker Engine + Compose v2, `openssl`, Node.js/npm (to build
the frontend bundle).

1. Generate a dev-only self-signed TLS cert (git-ignored output):

   ```bash
   deploy/nginx/certs/generate-dev-certs.sh
   ```

2. Build the frontend static bundle — nginx bind-mounts `frontend/dist`
   directly, so it must exist before `up`:

   ```bash
   cd frontend
   npm ci
   npm run build
   cd ..
   ```

   If you skip this, Docker silently creates an empty `frontend/dist`
   directory the first time you bring the stack up, and nginx serves an
   empty listing instead of failing loudly — if `curl -k https://.../` in
   step 5 doesn't return the app shell, check this first.

3. Compute a dev admin password hash so you can log in, and (optionally)
   override the dev Postgres credentials / published HTTPS port. These all
   go in a git-ignored `.env` file next to `docker-compose.yml`
   (`deploy/.env`):

   ```bash
   # Never commit the plaintext password or its hash.
   printf 'a-dev-password-of-your-choosing' | sha256sum | awk '{print $1}'
   ```

   ```bash
   # deploy/.env
   WAYPOINT_ADMIN_PASSWORD_HASH=paste-the-hash-from-above-here
   POSTGRES_USER=waypoint
   POSTGRES_PASSWORD=waypoint_dev_only
   POSTGRES_DB=waypoint
   WAYPOINT_HTTPS_PORT=8443
   ```

   Leaving `WAYPOINT_ADMIN_PASSWORD_HASH` unset is fine — the stack still
   comes up healthy and serves `/api/v1/health`, it just refuses every login
   (fails closed by design; see `backend/README.md` "Run locally").

4. Bring up the stack from `deploy/`:

   ```bash
   cd deploy
   docker compose up -d
   docker compose ps          # all three should report "healthy"
   ```

5. Verify:

   ```bash
   # Real frontend app shell, over TLS (dev cert is self-signed -> -k)
   curl -k https://localhost:8443/

   # /api/v1/health, proxied through nginx to the backend
   curl -k https://localhost:8443/api/v1/health

   # Login round-trip (only succeeds if WAYPOINT_ADMIN_PASSWORD_HASH was set)
   curl -k -X POST https://localhost:8443/api/v1/auth/login \
     -H 'Content-Type: application/json' \
     -d '{"username":"admin","password":"a-dev-password-of-your-choosing"}'

   # Postgres is not reachable from the host at all (connection refused)
   curl http://localhost:5432/
   ```

6. Tear down:

   ```bash
   docker compose down       # add -v to also drop the named volumes
   ```

**Multiple agents/humans on the same Docker host:** see
[`docs/testing.md`](../docs/testing.md) before running the steps above — it
covers the mandatory isolation recipe (unique project, container names, and
host port) this repo requires for concurrent bring-ups.

### Verifying SSE streaming

ADR-0003 requires `proxy_buffering off` on the SSE routes; this is load-bearing
for the product's hero screen (the live run view) and the global job log
drawer. A passing status code on `/api/v1/events` proves routing only, **not**
streaming — buffering left on silently doesn't fail a healthcheck, doesn't
change any status code, and doesn't show up in `curl -o /dev/null`. It only
manifests as a live run whose events never appear in the UI. Verify it two
ways.

**1. Confirm the directive is actually in the effective config**, not just in
the source file on disk (`nginx -T` prints what nginx actually loaded):

```bash
docker exec waypoint-nginx nginx -T | grep -n -B9 -E '^[[:space:]]*proxy_buffering[[:space:]]'
```

Expect exactly one match, and the 9 lines of context above it should show it
sitting inside `location ~ ^/api/v1/(events|runs/[^/]+/events)$ { ... }` — not
inside the ordinary `location /api/` block below it. (The anchored pattern
matters: a plain `grep proxy_buffering` also matches this same file's
explanatory comment line above the directive, so it reports two hits instead
of one. And a shallower context, e.g. `grep -A3 'location ~'`, stops short of
the `proxy_buffering off;` line entirely and reports a false negative on a
healthy config — don't use either.)

**2. Prove streaming actually behaves differently**, with a throwaway
slow-drip upstream. This is the real proof; a config grep only shows what
nginx parsed, not what a client actually receives. Bring the dev stack up
first (see "Bring-up"), then:

```bash
# Save as sse-probe.py. Emits 5 ticks, 1 second apart, on both an
# SSE-content-typed path and a plain path with an identical write pattern —
# so any timing difference a client sees is nginx's doing, not the
# upstream's.
cat > /tmp/sse-probe.py <<'PYEOF'
import http.server, time

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _drip(self, content_type):
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Connection", "close")
        self.end_headers()
        for i in range(5):
            self.wfile.write(("data: tick %d\n\n" % i).encode())
            self.wfile.flush()
            time.sleep(1)

    def do_GET(self):
        if self.path == "/api/v1/events":
            self._drip("text/event-stream")
        elif self.path == "/api/v1/streamtest":
            self._drip("text/plain")
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, fmt, *args):
        pass

http.server.HTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
PYEOF

# Stop the real backend so it doesn't answer instead of the probe, then
# start the probe aliased as "backend" on the stack's edge network (the
# same alias nginx's resolver looks up per request - see "Networking"
# above). Replace PROJECT with your compose project name (the default
# stack is "waypoint-dev"; docs/testing.md's isolated stacks use "wp-$SLUG").
docker compose stop backend
docker run -d --rm --name sse-probe \
  --network PROJECT_edge --network-alias backend \
  -v /tmp/sse-probe.py:/sse-probe.py:ro \
  python:3-alpine python /sse-probe.py

# SSE route (location ~ ...events$, proxy_buffering off): expect each tick
# timestamped roughly 1 second apart.
echo "SSE /api/v1/events:"
curl -k -s -N https://localhost:8443/api/v1/events \
  | while IFS= read -r line; do printf '%(%H:%M:%S)T  %s\n' -1 "$line"; done

# Ordinary REST route (location /api/, buffering on): expect nothing until
# the upstream closes (~4s later), then all 5 ticks at once.
echo "REST /api/v1/streamtest:"
curl -k -s -N https://localhost:8443/api/v1/streamtest \
  | while IFS= read -r line; do printf '%(%H:%M:%S)T  %s\n' -1 "$line"; done

# Restore the real backend.
docker stop sse-probe
docker compose start backend
```

Expected shape of the output — timestamps spread out on the SSE route,
clustered together on the REST route:

```
SSE /api/v1/events:
16:29:49  data: tick 0
16:29:50  data: tick 1
16:29:51  data: tick 2
16:29:52  data: tick 3
16:29:53  data: tick 4
REST /api/v1/streamtest:
16:29:58  data: tick 0
16:29:58  data: tick 1
16:29:58  data: tick 2
16:29:58  data: tick 3
16:29:58  data: tick 4
```

If the REST route instead shows spread-out timestamps too, `proxy_buffering`
has regressed for the SSE location (or been left off somewhere it shouldn't
be) — that is the regression this check exists to catch.

This is docs-only until a real automated test harness lands (the backend SSE
work is issue #7); revisit then.

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

The `backend` service doesn't consume any of this yet, so
`docker-compose.yml` has the mount commented out. Once the download-job work
in epic #1 lands, uncomment the `backend` service's
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
