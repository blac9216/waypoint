# deploy

Deployment artifacts: Docker Compose stack, nginx configuration, updater sidecar,
install/upgrade scripts, and (later) bundle tooling + Packer OVA build. See
[ADR-0001](../docs/adr/0001-packaging.md),
[ADR-0009](../docs/adr/0009-self-update.md), and
[ADR-0010](../docs/adr/0010-deployment-topology.md). The approved runner and
source-build direction is recorded in [ADR-0013](../docs/adr/0013-control-plane-and-runners.md),
[ADR-0014](../docs/adr/0014-runner-job-ownership.md), and
[ADR-0015](../docs/adr/0015-source-build-and-operator-export.md).

> **Runner topology landed in issue [#442](https://github.com/blac9216/waypoint/issues/442);
> the control-plane split completed in issue
> [#443](https://github.com/blac9216/waypoint/issues/443).** `backend` is control-plane
> only (ADR-0013 §1) both in mounts (no execution scripts or domain tool stores
> read-write) and in its compiled process: it references neither the PowerShell SDK
> nor any job handler at build time (`backend/Waypoint.Infrastructure.Execution` is a
> separate project only `compliance-runner`/`download-runner` reference), so there is
> no dormant dispatcher registration for a future config change to reactivate.
> `GET /api/v1/system` reports each runner's live heartbeat/capability status
> (migration `0026_worker_registry.sql`), distinguishing API health from
> compliance/download runner availability.

## Dev compose stack (epic [#1](https://github.com/blac9216/waypoint/issues/1), runners issue [#442](https://github.com/blac9216/waypoint/issues/442))

`docker-compose.yml` stands up the development topology from
[`docs/architecture.md`](../docs/architecture.md) (component view),
[ADR-0003](../docs/adr/0003-reverse-proxy-nginx.md), and
[ADR-0013](../docs/adr/0013-control-plane-and-runners.md)/[ADR-0014](../docs/adr/0014-runner-job-ownership.md):

```
browser --TLS--> nginx --/api--> backend --> postgres (internal network only)
                    \--> static frontend bundle (frontend/dist/, bind-mounted)

compliance-runner --> postgres (internal network only; not reachable from nginx/host)
download-runner   --> postgres (internal network only; not reachable from nginx/host)
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
  It owns REST/RBAC/SSE, enqueueing, and run controls — it does not execute jobs
  (ADR-0013 §1). Local auth (ADR-0004 rollout note) is a dev-grade single admin
  user with no compiled-in default password — see "Bring-up" step 3 to enable
  login, and step 4 to enable the credential store's secret encryption.
- **compliance-runner** and **download-runner** are long-lived Generic Host
  services (ADR-0013 §3), each claiming only its allowlisted job types
  (`discover`/`credential-test`/`scan`/later `remediate`, and
  `catalog-index`/`download` respectively) directly from Postgres
  (ADR-0014 §1) and hosting PowerShell in-process. Neither has an HTTP listener
  or a compose `ports:` mapping — both sit on the `internal` network only, never
  reachable from `nginx` or the host. Their Dockerfiles
  (`../runners/compliance-runner/Dockerfile`, `../runners/download-runner/Dockerfile`)
  build from the repo root, same convention as `backend/Dockerfile` (issue #92),
  because each now has its own `dotnet-build` stage that compiles its .NET host
  executable alongside its domain toolchain (PowerCLI/cinc-auditor/SAF for
  compliance; pwsh + vendor scripts for download). One replica of each is the
  default (ADR-0013 §6) — see "Replica-safe scaling" below before setting
  `deploy.replicas` on either.
- **postgres** (16) sits on an `internal: true` compose network with no
  published port — reachable only from `backend` and the two runners, never
  from the host or from `nginx`. Each of the three connects as its own
  least-privilege database role (`waypoint` for the backend,
  `waypoint_compliance_runner`/`waypoint_download_runner` for the runners) —
  see "Database roles" below.

All five services declare a Docker `healthcheck`. The two runners report health
via a file sentinel rather than a loopback HTTP call, since neither has an HTTP
listener to probe (ADR-0013 §1) — see "Runner health checks" below.

### Database roles (issue #442, ADR-0014 §7)

`postgres/initdb/01-runner-roles.sh` runs once, the first time the `pgdata`
volume is initialized (Postgres's own `docker-entrypoint-initdb.d` convention —
it does **not** re-run against an existing volume), and creates two
`NOSUPERUSER NOCREATEDB NOCREATEROLE` login roles: `waypoint_compliance_runner`
and `waypoint_download_runner`. Their passwords come from
`POSTGRES_COMPLIANCE_RUNNER_PASSWORD`/`POSTGRES_DOWNLOAD_RUNNER_PASSWORD`
(`deploy/.env`, gitignored — same convention as `POSTGRES_PASSWORD`; dev-only
defaults apply if unset).

Table-level `GRANT`s for those two roles live in a versioned backend migration —
`backend/Waypoint.Infrastructure/Data/Migrations/0025_runner_db_roles.sql` —
applied by `backend` (the sole migrator, `Database:RunMigrationsOnStartup`)
using its own owning `${POSTGRES_USER}` connection. Splitting role creation
(needs the actual password, so it can't live in a committed `.sql` file) from
grants (versioned alongside schema, so a later migration adding a table also
grants it) means an operator with a pre-#442 `pgdata` volume only needs to
create the two roles once by hand before `backend` next migrates — see that
migration's header comment for the exact tables/columns each role gets and
why. **If `backend` starts before `postgres`'s init scripts have created the
roles** (only possible on a genuinely fresh volume with a misordered manual
`docker exec`, not through normal `docker compose up`), migration 0025 fails
loudly (`role "waypoint_compliance_runner" does not exist`) rather than
silently granting nothing — `docker compose logs backend` names the missing
role.

### Runner health checks (issue #442, ADR-0013 §1)

`compliance-runner` and `download-runner` have no REST/SSE surface, so neither
can answer a `wget --spider`-style probe the way `backend`/`nginx` do. Instead
each runs a background hosted service that re-evaluates domain-specific
readiness on an interval (PowerShell module/content mounts, artifact-store
writability, master-key availability, and for `download-runner` a real
Postgres reachability check) and writes a small JSON report to a fixed path
inside the container; the same binary's `--health-check` flag reads that
report back and exits `0`/`1` — mirroring `backend`'s own
`dotnet Waypoint.Api.dll --health-check` convention, just backed by a file
instead of a loopback HTTP call. A **missing runner dependency** (e.g. no
compliance-content volume populated, or the master key mount left
uncommented-out) makes that runner's own healthcheck fail — Compose reports
that one container `unhealthy` — without affecting `backend`'s or the other
runner's health, since each container's healthcheck is independent and no
other service `depends_on` a runner with `condition: service_healthy`.

### Replica-safe scaling

The queue claim (`FOR UPDATE SKIP LOCKED`, filtered by job-type allowlist) and
worker identity (`{MachineName}:{ProcessId}:{Guid}`, generated per process at
startup — see `JobDispatcherHostedService`) are both already safe under
multiple identical replicas of the same runner: two containers claiming from
the same queue can never claim the same row, and each gets a distinct
`claimed_by` value derived from its own container hostname/pid/random suffix,
with no compose-level identity configuration required. Scaling
(`docker compose up -d --scale compliance-runner=2`, or a compose-file
`deploy.replicas` override) is therefore safe to do, but it does not create
more host CPU/memory — resource-aware admission (ADR-0014 §5,
`RunnerResources` config section) bounds each replica's own concurrency from
its container's cgroup allocation independently, so N replicas compete for the
same underlying host resources rather than multiplying them. It remains an
operator option, not the default (ADR-0013 §6) — one replica of each runner
starts by default.

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

This is a **porting landmine**, and it is worth being precise about which
deployments it actually fires for — the dependency is on **Docker's embedded
DNS**, not on any particular way of shipping the stack.

- **The ADR-0001 OVA path (issue #47) is _not_ affected.**
  [ADR-0001](../docs/adr/0001-packaging.md) describes wrapping "the identical
  compose stack" in a Packer-built OVA on a minimal OS — "a packaging wrapper,
  **not a different architecture**". An OVA that boots Photon OS and runs this
  same `docker compose up` still runs Docker, still gets a user-defined bridge
  network, and still has `127.0.0.11` answering inside it. Nothing here needs
  to change for that path.
- **What _would_ break it is dropping Docker itself**, i.e. any topology where
  nginx no longer resolves `backend` through Docker's embedded DNS:
  - **a different container runtime** — e.g. Podman, whose DNS is
    `aardvark-dns` on its own address, or a Kubernetes-style deployment where
    the service name resolves through the cluster DNS instead;
  - **running the services directly on a host** (systemd units, no containers),
    where `backend` is not a DNS name at all.

  In either case, `resolver 127.0.0.11` must be repointed at that
  environment's real resolver — and the `$backend_host` variable + `resolver`
  pairing kept, since the reason for it (re-resolving an address that changes
  under you on redeploy) survives the move. If the new topology gives the
  backend a stable address instead, static addressing is the simpler
  replacement.

See [ADR-0003](../docs/adr/0003-reverse-proxy-nginx.md#consequences) for the
same note recorded against the reverse-proxy decision.

### Health endpoints: liveness vs. readiness (issue #66)

nginx exposes two container-internal health endpoints on the un-published
`8081` listener (`deploy/nginx/conf.d/healthz.conf`) — pick the one that
matches what you're checking:

- **`/healthz` — liveness.** "Is nginx itself up." A local `return 200`,
  independent of the backend. This is what the Docker Compose
  `healthcheck:` for the `nginx` service polls, and what
  `depends_on: condition: service_healthy` gates on. It deliberately does
  **not** reflect backend reachability: the ADR-0009 self-update flow (and
  ordinary `docker compose restart backend`) recreates the `backend`
  container as routine behavior, and coupling nginx's own liveness to that
  would make nginx report unhealthy — and risk being bounced by an external
  supervisor that treats Docker HEALTHCHECK as a restart signal — for a
  problem that isn't nginx's.
- **`/healthz/upstream` — readiness.** "Is nginx **and** the backend it
  proxies to both reachable." Proxies to the backend's own
  `GET /api/v1/health` with short timeouts (2s connect/send/read) and no
  caching, so it fails fast instead of hanging. A stopped, crash-looping, or
  network-partitioned backend shows up here as `502`/`504` — this is the
  signal that closes the silent-outage window #66 was filed for. Use this
  for operator/monitoring checks and anything that needs to know the
  request path actually works end-to-end, not just that nginx is running.
  Like the `/api/` and SSE locations in `default.conf`, it re-resolves
  `backend` per request via the same `resolver` + `$backend_host` pattern
  from #59, so it recovers on its own once `backend` comes back — no
  `nginx -s reload` needed.

Per-domain runner capability (compliance-runner vs. download-runner) is
**not** part of either endpoint — that distinction surfaces through the
backend's own `GET /system` (issue #443), not through an nginx probe. Use
`/healthz/upstream` to answer "can nginx reach the backend at all," and
`GET /system` (once #443 lands) to answer "which execution domains are
actually available."

Both endpoints are container-internal only (port `8081` is never published
to the host) — reach them from inside the `nginx` container, e.g.:

```bash
docker compose -p <project> exec nginx wget -qO- http://127.0.0.1:8081/healthz
docker compose -p <project> exec nginx wget -qO- http://127.0.0.1:8081/healthz/upstream
```

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

   If you skip this, `docker compose up` **fails immediately** rather than
   coming up half-working:

   ```
   Error response from daemon: invalid mount config for type "bind":
   bind source path does not exist: /path/to/waypoint/frontend/dist
   ```

   That is deliberate. The nginx `frontend/dist` mount uses Compose's long
   syntax with `bind: {create_host_path: false}` (see `docker-compose.yml`),
   which turns what would otherwise be a silent failure into a loud one.
   Docker's *default* behaviour for a missing bind source is to create it —
   as a **root-owned** empty directory — and the resulting stack looks fine:
   every service still reports `healthy`, and `curl -k https://localhost:8443/`
   returns a bare nginx **403 Forbidden** (`try_files` falls through to an
   empty directory with autoindex off), not an empty listing or a 404. Build
   the bundle and bring the stack up again.

   **The guard covers a *missing* `frontend/dist`, not an *empty* one.** If the
   directory exists but has no files in it, the mount succeeds, every service
   still reports `healthy`, and `/` still returns that same bare nginx
   **403 Forbidden** — verified with the guard in place. This state is easy to
   reach by accident: `vite build` empties `outDir` before it writes anything,
   so a build that fails partway leaves exactly an empty `dist/`. If you get a
   403 from a stack that came up cleanly, check `ls frontend/dist` first and
   re-run `npm run build` — reading its exit status this time.

   **If you already have a root-owned `frontend/dist`** — from an older
   revision of this stack, or another tool — `npm run build` fails with
   `EACCES: permission denied`. Remove it and rebuild:

   ```bash
   sudo rm -rf frontend/dist
   cd frontend && npm run build
   ```

3. Compute a dev admin password hash so you can log in, and (optionally)
   override the dev Postgres credentials / published HTTPS port.

   ```bash
   # Prompts for the password (input hidden) and prints a salted PBKDF2 hash to
   # stdout (Pbkdf2PasswordHasher, issue #62). Never pass the password as an
   # argument, and never commit the plaintext password or its hash.
   cd ../backend && dotnet build Waypoint.Api
   dotnet run --project Waypoint.Api --no-launch-profile --no-build -- --hash-password
   cd -
   ```

   Deliver the hash one of two ways (issue #333 — the mounted file is
   preferred; the env var is kept working for existing deployments):

   **Preferred: mounted file.** Write the hash to the repo-root `config/`
   directory (git-ignored — see `.gitignore`'s anchored `/config/` entry
   and CLAUDE.md; `deploy/config/` is *not* covered, so this path matters):

   ```bash
   mkdir -p ../config/local-auth
   # Paste the hash from the --hash-password output above. Trailing newline
   # is fine - LocalAuthOptionsPostConfigure trims it.
   printf '%s' 'paste-the-hash-from-above-here' > ../config/local-auth/admin-password-hash
   ```

   Then uncomment the `local-auth-admin-password-hash` bind mount on the
   `backend` service in `docker-compose.yml`. On startup the backend reads
   the hash from `LocalAuth__AdminPasswordHashFile`
   (`/run/secrets/local-auth-admin-password-hash` in-container) and it takes
   precedence over the env var below.

   **Legacy fallback: env var.** Go in a git-ignored `.env` file next to
   `docker-compose.yml` (`deploy/.env`):

   ```bash
   # deploy/.env
   WAYPOINT_ADMIN_PASSWORD_HASH=paste-the-hash-from-above-here
   POSTGRES_USER=waypoint
   POSTGRES_PASSWORD=waypoint_dev_only
   POSTGRES_DB=waypoint
   # Issue #442: passwords for the two dedicated runner DB roles. Same
   # dev-only-default convention as POSTGRES_PASSWORD above — see "Database
   # roles" and postgres/initdb/01-runner-roles.sh.
   POSTGRES_COMPLIANCE_RUNNER_PASSWORD=waypoint_compliance_runner_dev_only
   POSTGRES_DOWNLOAD_RUNNER_PASSWORD=waypoint_download_runner_dev_only
   WAYPOINT_HTTPS_PORT=8443
   ```

   Only used when the mounted file above isn't present — the backend logs a
   one-time startup WARN when it falls back to this env var, since env vars
   leak via `/proc/<pid>/environ`, `docker inspect`, and crash dumps.

   Leaving both unset is fine — the stack still comes up healthy and serves
   `/api/v1/health`, it just refuses every login (fails closed by design;
   see `backend/README.md` "Run locally").

   Changing `POSTGRES_USER`/`POSTGRES_PASSWORD`/`POSTGRES_DB` here is enough on
   its own — the `backend` service composes its `ConnectionStrings__Waypoint`
   from the same three variables (#103), so there is no separate connection
   string to keep in sync. `POSTGRES_COMPLIANCE_RUNNER_PASSWORD`/
   `POSTGRES_DOWNLOAD_RUNNER_PASSWORD` only take effect on a **fresh**
   `pgdata` volume (`docker-entrypoint-initdb.d` scripts never re-run against
   an existing one — same rule `POSTGRES_PASSWORD` itself already follows);
   changing them against a stack that already has data requires `ALTER ROLE
   ... PASSWORD` by hand, or a fresh volume.

4. Generate a secrets master key (issue #405) so the credential store works.
   ADR-0005's envelope encryption (the AWX pattern) fails closed without
   one: `FileMasterKeyProvider` throws a clear
   `MasterKeyUnavailableException` — "No master key is configured..." — the
   first time anything encrypts, so `POST /api/v1/credentials` (or any other
   secret-bearing write) answers `503 master_key_unavailable` (issue #409;
   `docs/api-contract.md`'s error-code table) until this step is done.
   Everything else in the stack works without it; only the credential store
   depends on this key.

   ```bash
   mkdir -p ../config/secrets
   openssl rand -hex 32 > ../config/secrets/master.key
   ```

   **Never commit this file or its contents.** `../config/` is git-ignored
   (see `.gitignore`'s anchored `/config/` entry and `CLAUDE.md`), and
   losing this key after real credentials have been encrypted with it makes
   those credentials permanently unrecoverable — back it up somewhere
   outside the repo if the stack's credential data matters to you.

   Then uncomment the `waypoint-master-key` bind mount **on all three
   trusted services** — `backend`, `compliance-runner`, and
   `download-runner` — in `docker-compose.yml` (issue #442: every service
   that decrypts a credential needs the same key). Each reads it from
   `WAYPOINT_MASTER_KEY_FILE`
   (`/run/secrets/waypoint-master-key` in-container) — unlike the admin
   password hash above, there is no environment-variable fallback; a
   mounted file is the only delivery path.

   **The key file must be readable by each container's app user.** The
   backend image (`backend/Dockerfile`) runs as the non-root `app` user
   baked into the `mcr.microsoft.com/dotnet/aspnet:8.0` base image — uid
   `1654`. The two runner images (`runners/compliance-runner/Dockerfile`,
   `runners/download-runner/Dockerfile`) run as a `waypoint` user their own
   Dockerfiles create at the same uid, `1654`, specifically so one file
   permission works for all three — not root and not your host user. A key
   file created with a tight `0600` and left owned by your own (or root's)
   uid is exactly the permission trap the admin-hash mount already warns
   about implicitly: none of the three can read it, and
   `FileMasterKeyProvider` reports `Access denied` on first use for
   whichever one tries first. For this dev stack, the practical fix is
   either:

   ```bash
   chmod 644 ../config/secrets/master.key      # simplest: readable by all
   # or, if you know the in-container uid and prefer tighter perms:
   chown 1654 ../config/secrets/master.key && chmod 600 ../config/secrets/master.key
   ```

   `644` on a gitignored, host-local path is the pragmatic default here —
   the file is not reachable outside this host either way, and matching
   ownership across a bind mount from an arbitrary host account is more
   friction than it buys on a dev box. A production/appliance deployment
   with a real operator-managed key should prefer the `chown`/`600` form.

   **A bad key file is cached until restart (issue #407).** Because
   `FileMasterKeyProvider` loads the key lazily and caches the result
   (including a failure) for the process lifetime, fixing permissions
   *after* a service has already attempted and failed a read does not
   help until that container restarts:

   ```bash
   docker compose restart backend compliance-runner download-runner
   ```

   Leaving the mount commented out (or the file absent) is fine on all
   three services — the stack still comes up healthy and serves
   `/api/v1/health` and login; it just fails closed on any secret-bearing
   write or decrypt with `FileMasterKeyProvider`'s own error until this
   step is done.

5. Bring up the stack from `deploy/`:

   ```bash
   cd deploy
   docker compose up -d
   ```

   `up -d` returns once containers are *started*, not once they pass a
   healthcheck. nginx declares `start_period: 5s` / `interval: 10s`, so a
   `docker compose ps` on the very next line reports `health: starting` every
   time — the stack is fine, nginx just needs 3–10 s for its first healthcheck.
   Wait for the condition rather than assuming the timing:

   ```bash
   # Exact-match on the health state: a substring test would also match
   # "unhealthy". All five containers must pass (nginx, backend, postgres,
   # compliance-runner, download-runner - issue #442), not just the first
   # one. docker compose ps -q derives the *actual* container IDs for this
   # project - correct whether you're on the plain default stack or an
   # isolated one (see docs/testing.md "The recipe"), since Compose no
   # longer pins fixed container_name values (issue #68).
   for id in $(docker compose ps -q); do
     for i in $(seq 1 60); do
       [ "$(docker inspect -f '{{.State.Health.Status}}' "$id" 2>/dev/null)" = healthy ] && break
       [ "$i" = 60 ] && { echo "$id never became healthy - check: docker logs $id"; break; }
       sleep 1
     done
   done
   docker compose ps          # all five should report "healthy"
   ```

   (If you are running under the `docs/testing.md` isolation recipe, prefix
   these commands with `docker compose -p wp-$SLUG` to scope them to your
   own project.)

   See [`docs/testing.md`](../docs/testing.md) "`up -d` returning does not mean
   healthy" for the full explanation and paste-safe patterns.

6. Verify:

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

   **Known limitation — the browser UI is not usable yet
   ([#64](https://github.com/blac9216/waypoint/issues/64)). A successful login
   leaves you on the login screen, silently — it is not a bad password.** The
   stack serves the real frontend bundle and the login endpoint above returns
   `200` with a token, but the frontend and backend disagree on the login
   response shape: the backend returns `{token, role, expires_at}` while the
   frontend expects `{token, user: {username, role}}`. `user` therefore comes
   back `undefined`, `App` renders the login screen whenever there is no user,
   and what you see in a browser is that **the login screen stays exactly as it
   was: no navigation chrome, no redirect, and no error message.** Reloading
   leaves you on the login screen too, for the same reason.

   How to tell this apart from a wrong `WAYPOINT_ADMIN_PASSWORD_HASH` before you
   go debugging one — in DevTools, a *correct* password gives you both of:

   - `POST /api/v1/auth/login` → **200** (a wrong password gives `401`
     `invalid_credentials`), and
   - `sessionStorage["waypoint.session"]` set to `{"token":"..."}` — a token and
     **no `user` key**, which is the shape mismatch itself.

   Verify this stack with the `curl` checks above until #64 lands; the bundle
   being served and the login round-trip returning `200` are what this layer is
   responsible for.

7. Tear down:

   ```bash
   docker compose down       # add -v to also drop the named volumes
   ```

**Multiple agents/humans on the same Docker host:** see
[`docs/testing.md`](../docs/testing.md) before running the steps above — it
covers the mandatory isolation recipe (unique project and host port) this repo
requires for concurrent bring-ups.

### Verifying SSE streaming

ADR-0003 requires `proxy_buffering off` on the SSE routes; this is load-bearing
for the product's hero screen (the live run view) and the global job log
drawer. A passing status code on `/api/v1/events` proves routing only, **not**
streaming — buffering left on silently doesn't fail a healthcheck, doesn't
change any status code, and doesn't show up in `curl -o /dev/null`. It only
manifests as a live run whose events never appear in the UI. Verify it two
ways.

Both checks below name your own stack rather than hardcoding a fixed container
name and `8443`. Container names are **global** on this Docker host — per
[`docs/testing.md`](../docs/testing.md), another agent's in-flight stack may own
them, and probing someone else's container yields a plausible wrong result
rather than an error. Set these once, from the same slug and port you brought
your stack up with (`docs/testing.md` "The recipe"), and run every command below
from `deploy/`:

```bash
SLUG=issue60-verify                          # your unique slug
PORT=18443                                   # your WAYPOINT_HTTPS_PORT
NGINX=wp-$SLUG-nginx-1                       # Compose-derived container name (issue #68: no fixed container_name)
PROBE=wp-$SLUG-sse-probe                     # throwaway probe container
DC="docker compose -p wp-$SLUG"
```

(On an unisolated default stack those are `NGINX=waypoint-dev-nginx-1`,
`PORT=8443`, `DC="docker compose"` — but this repo asks you to isolate, so
prefer the above.)

**1. Confirm the directive is actually in the effective config**, not just in
the source file on disk (`nginx -T` prints what nginx actually loaded):

```bash
docker exec "$NGINX" nginx -T | grep -n -B9 -E '^[[:space:]]*proxy_buffering[[:space:]]'
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

# Stop YOUR backend so it doesn't answer instead of the probe, then start
# the probe aliased as "backend" on YOUR stack's edge network (the same
# alias nginx's resolver looks up per request - see "Networking" above).
# $DC, $PROBE and $PORT come from the variable block at the top of this
# section; the edge network is "<project>_edge", i.e. wp-$SLUG_edge here.
$DC stop backend
docker run -d --rm --name "$PROBE" \
  --network "wp-${SLUG}_edge" --network-alias backend \
  -v /tmp/sse-probe.py:/sse-probe.py:ro \
  python:3-alpine python /sse-probe.py

# Wait for the handover before probing, or you get a 502 for the first second
# or two: the probe needs a moment to bind, and nginx caches the "backend"
# lookup for the `resolver ... valid=10s` window (see "Networking"). The probe
# serves no /api/v1/health route, so a 404 there means the probe - not the real
# backend - is what nginx is now reaching.
#
# Bounded to 30 iterations (30 s) so a stuck probe doesn't hang indefinitely
# with no output — if the probe never takes over, the diagnostic names the
# next command to run.
for i in $(seq 1 30); do
  [ "$(curl -k -s -o /dev/null -w '%{http_code}' --max-time 5 \
    "https://localhost:$PORT/api/v1/health")" = "404" ] && break
  [ "$i" = 30 ] && { echo "probe never took over - check: docker logs $PROBE"; break; }
  sleep 1
done

# SSE route (location ~ ...events$, proxy_buffering off): expect each tick
# timestamped roughly 1 second apart.
echo "SSE /api/v1/events:"
curl -k -s -N "https://localhost:$PORT/api/v1/events" \
  | while IFS= read -r line; do printf '%(%H:%M:%S)T  %s\n' -1 "$line"; done

# Ordinary REST route (location /api/, buffering on): expect nothing until
# the upstream closes (~4s later), then all 5 ticks at once.
echo "REST /api/v1/streamtest:"
curl -k -s -N "https://localhost:$PORT/api/v1/streamtest" \
  | while IFS= read -r line; do printf '%(%H:%M:%S)T  %s\n' -1 "$line"; done

# Restore your real backend.
docker stop "$PROBE"
$DC start backend
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

### Compliance content and managed-tool state (issue #442)

The three PowerShell-script bind mounts this section used to document
(`dev/local/`, `WAYPOINT_VCF_SCRIPTS_DIR`, `WAYPOINT_VMWARE_STIG_DOCKER_SCRIPTS_DIR`)
are gone from `backend` — ADR-0013/ADR-0015 moved the project-owned vendor
scripts into `../runners/compliance-runner/powershell/` and
`../runners/download-runner/powershell/`, baked directly into each runner's
image at build time (see each Dockerfile's header comment), so there is
nothing left for `backend` — or an operator — to bind-mount for them.

What operators still provide at runtime, as named volumes rather than host
bind mounts (populate them by `docker cp`-ing content in, or a future
provisioning UI):

- **`compliance-profiles`** (mounted read-only into `compliance-runner` at
  `/opt/waypoint/profiles`) — InSpec compliance content
  (`ScanOptions.ProfilePath`/`NsxProfilePath`/`SrgProfilePath`, all under this
  one root). A scan job fails closed with a clear "not mounted" readiness
  problem (`ComplianceReadinessCheck`) until this is populated; the stack
  still comes up healthy without it.
- **`managed-tool`** (mounted read-write into `download-runner` at
  `/var/lib/waypoint/managed-tool`) — the account-gated `vcf-download-tool`
  executable (ADR-0015 decision 3: operator-installed through the appliance,
  never baked into the image). A `download` job fails closed with a clear
  "tool not installed" error until this is populated; the stack still comes
  up healthy without it.
- **`depot`** (mounted read-write into `download-runner` at `/vcf`) — the
  offline depot share `catalog-index` indexes (`CatalogOptions.DepotPath`).

`dev/local/`'s depot-token/config borrowing convention (gitignored,
never committed — CLAUDE.md) still applies to *credentials*, which travel
through the encrypted credential store (`POST /api/v1/credentials`), not
through a filesystem mount at all — that part of the convention was never
about `backend`'s mounts and is unaffected by this issue.

### Edge hardening baseline (DISA/CIS nginx guidance, issue #52)

`deploy/nginx/conf.d/default.conf` carries a hardening baseline applied on
every bring-up — dev and production alike — rather than bolted on at
packaging time. ADR-0003 picked nginx partly because DISA publishes
hardening guidance for it; this is where that guidance lives.

**Baseline (not operator-tunable without a code change):**

- Plain HTTP (`listen 80`) 301-redirects to HTTPS instead of refusing the
  connection — a static same-host redirect, no ACME/external dependency.
- `server_tokens off;` on both server blocks — the nginx version is not
  advertised in the `Server` header or on error pages.
- An explicit `ssl_ciphers` list (ECDHE + AES-GCM/CHACHA20 only, no
  CBC/RC4/3DES) with `ssl_prefer_server_ciphers on`, plus `ssl_session_cache`
  / `ssl_session_timeout` / `ssl_session_tickets off` — no reliance on
  nginx's compiled-in cipher defaults. `ssl_protocols TLSv1.2 TLSv1.3` was
  already set (PR #49).
- Security response headers on the HTTPS server: `Strict-Transport-Security`
  (`max-age=2592000`, i.e. 30 days — shorter than the public-site convention
  of 1-2 years because this is an operator-administered appliance reached by
  IP/local hostname, not a public domain with a long-lived rotation cadence;
  a shorter pin bounds the blast radius of a botched cert rotation),
  `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, and
  `Referrer-Policy: same-origin`.
- `Content-Security-Policy: default-src 'self'; connect-src 'self';
  frame-ancestors 'none'; base-uri 'self'; form-action 'self'`. The PWA
  (ADR-0007) ships zero external assets and no inline `<style>`/`<script>` —
  verified against `frontend/index.html` and `vite.config.ts` at authoring
  time (no CSS-in-JS, no inline `style=` attributes) — so a strict
  `'self'`-only policy fits without any `'unsafe-inline'`/`'unsafe-eval'`
  relaxation. `connect-src 'self'` covers both ordinary `/api/` REST calls
  and the `/api/v1/events` SSE endpoints (EventSource is governed by
  `connect-src`, same-origin through this proxy either way). **If a future
  screen needs an inline style/script or a cross-origin connection, this
  directive has to change with it** — don't relax CSP anywhere else instead.

- A request-method allowlist (issue #388): `GET, HEAD, POST, PUT, DELETE,
  OPTIONS` — every other method (TRACE, CONNECT, and any other
  WebDAV/exotic verb) gets a `405` before any location is matched. Derived
  from the full resource table in `docs/api-contract.md` — no route there
  uses any method outside that set. See the in-file comment in
  `default.conf` for why a server-level, return-only `if` was chosen over
  `limit_except`.

**Operator-tunable:** cert/key content and paths (`ssl_certificate`,
`ssl_certificate_key` — operator-provided, ADR-0003), and anything in
`deploy/.env` (`WAYPOINT_HTTPS_PORT`, etc.). The cipher list, header set,
and CSP above are the appliance's security baseline and are not meant to be
loosened per-deployment; tightening further (e.g. a shorter HSTS `max-age`,
or extending CSP once new asset types are added) is fine.

### Notes

- The dev TLS cert is self-signed (`CN=localhost`) and meant only for this
  local loop; the operator supplies real certificates from their internal
  CA in any real deployment (ADR-0003).
- `nginx` binds IPv4 only (`listen 443 ssl;`, no `listen [::]:443`) — some
  Docker network configurations (including the sandbox this stack was
  verified in) have no IPv6 route, and nginx refuses to start at all if a
  `listen [::]` directive can't bind.
