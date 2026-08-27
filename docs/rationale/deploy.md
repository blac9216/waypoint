# Rationale Index — deploy/

This file is the evicted "why" for `deploy/`. Per epic #933, deploy/ code
carries only short section markers and terse one-line warnings — no
issue/ADR/history references live in code. When a warning needs a "why", it
points here: `# why: docs/rationale/deploy.md#<kebab-slug>`. This doc is the
one durable home for that provenance.

## Format

- One `##` section per source file (or small grouped directory) under
  `deploy/`.
- Within a section, one `###` entry per kebab-case slug. The slug is the
  exact anchor a code comment points at.
- **Slugs are unique across the whole file, not just within a section.**
  GitHub anchors are file-global: a duplicate `###` heading anywhere in this
  file silently becomes `#slug-1`, and every `# why:` pointer written against
  the second entry then resolves to the *first* one instead — a failure that
  looks correct in review. Disambiguate by prefixing the slug with the
  service/file it belongs to, e.g. `postgres-healthcheck-start-period` and
  `keycloak-healthcheck-start-period`, never a bare `healthcheck-start-period`
  repeated across sections.
- Entry body: 2–6 lines explaining the why (the reasoning, trade-off, or
  constraint — not a restatement of the code). An entry that would need more
  than 6 lines is a sign it bundles more than one claim — split it instead.
- Entry ends with a `Refs:` line carrying provenance: issue numbers, ADRs,
  PRs. This is the only place that provenance lives — do not duplicate it
  in code comments or in deploy/ markdown.

### Example entry

A filled entry under a `## compose.yaml` section looks like this:

```markdown
### compose-healthcheck-start-period-30s

The backend's first boot runs pending EF Core migrations before it starts
accepting connections, which can take longer than a typical healthcheck
window under cold cache. A short `start_period` produced flapping
"unhealthy" states during normal first-run migrations, not real failures.

Refs: #000 (invented placeholder — not a real issue)
```

The `compose-` prefix disambiguates this slug from, say, a similarly-named
`postgres-healthcheck-start-period-30s` entry under a different section —
see the file-global uniqueness rule above.

And the matching code comment, in `compose.yaml` itself:

```yaml
# why: docs/rationale/deploy.md#compose-healthcheck-start-period-30s
start_period: 30s
```

---

Add a new entry by appending a `###` slug under the relevant `##` file
section, writing the 2–6 line why, and closing with `Refs:`. Point to it
from code with `# why: docs/rationale/deploy.md#<slug>`. If the source file
has no `##` section yet, create one — append it in `deploy/`-tree order —
rather than leaving the entry homeless. This index is deploy-scoped for now;
a repo-wide pointer-integrity check that verifies every `# why:` comment
resolves to a real anchor is tracked separately (#939) and is not built here.

## README.md

### readme-devcontainer-build-up-split

Adding `--build` to the devcontainer/remote-daemon `up` recipe broke it:
build contexts are resolved by the client (this shell), while
`--project-directory` makes bind-mount sources resolve against the host —
one flag can't satisfy both resolution rules at once. `build` (client
paths, no `--project-directory`) and `up -d --no-build` (host project
directory) must run as two commands.

Refs: #955

## config.example/

## compose.yaml

### compose-name-waypoint-project-identity

A real deployment stamping `-dev` onto every network/volume/container name
it creates was an operator-facing oddity left over from before production
and the dev override split. `waypoint` alone is the base's canonical
project identity; concurrent isolated bring-ups still override it with
`-p`, and the persistent dev-stack helper passes the same default
explicitly rather than relying on this file's own name.

Refs: #845, #885

### compose-public-url-single-origin

The backend's token issuer and Keycloak's own hostname used to be two
independently configured settings a real deployment kept in lockstep by
hand. `x-operator-config.public-url` is the one browser-facing HTTPS origin
both now derive from. The fallback is a deliberately nonfunctional
placeholder domain, never `localhost`, so an unconfigured base fails OIDC
login clearly instead of quietly working against an unreachable origin.

Refs: #842

### compose-edge-subnet-pinning

`ForwardedHeaders:KnownNetworks` needs to trust exactly the network nginx
and the backend share, not the whole RFC 1918 space. The chosen /24 sits
outside Docker's default address-pool order and outside the low
`192.168.0.0/16` range a typical LAN or nested Docker host is likely to
already use, so it's unlikely to collide. Only `edge` is pinned; `internal`
has no route out, so a fixed subnet buys it nothing.

Refs: #191

### compose-tls-required-no-fallback

No self-signed fallback ships in the production base by design — TLS
material an operator did not knowingly provide is not something this file
should silently generate. The development-only self-signed pair lives in
compose.override.example.yaml instead.

Refs: ADR-0003, #844, #845

### compose-ports-long-syntax

nginx's `ports:` uses the long mapping syntax so `published:` can alias the
`https-port` operator-config anchor. A YAML alias node can't be spliced
into part of a scalar string, so the short `"HOST:CONTAINER"` form can't
carry an aliased value here.

Refs: #845

### compose-tls-bind-fail-closed

Both TLS bind mounts set `create_host_path: false`, so a missing
`deploy/config/tls/tls.{crt,key}` fails `docker compose up` closed at
container-create time instead of Docker silently creating an empty
directory and nginx failing later with a confusing TLS error.

Refs: #844, #845

### compose-oidc-authority-vs-public-authority

`Oidc__Authority` is the backend's own container-network view of Keycloak
(`http://keycloak:8080/...`), used for token validation only — a browser
can never reach it. `Oidc__PublicAuthority` is the browser-facing value
handed to the SPA via `GET /api/v1/auth/config`. They are deliberately
different values; collapsing them breaks browser login.

Refs: #534

### compose-db-passwordfile-split

`ConnectionStrings__Waypoint` always carries `Password=` empty.
`Database__PasswordFile` is mandatory and always wins once populated —
postgres itself refuses to start without its own `POSTGRES_PASSWORD_FILE`,
so an inline password here would only be an unused literal sitting in
`docker inspect` output. The same pattern repeats for both runners' roles.

Refs: #843, #844

### compose-master-key-file-only

`FileMasterKeyProvider` only ever reads a file, by design — env vars leak
via `/proc/<pid>/environ`, `docker inspect`, and crash dumps. The key is
read lazily on first secret-store operation, not at startup, so a stack
without the mount still comes up healthy; only a credential write fails.

Refs: #405, ADR-0005

### compose-master-key-override-not-uncomment

The commented mount block is the mount SHAPE to copy into an operator's own
`compose.override.yaml` (auto-loaded, no `-f` needed) — not something to
uncomment in this service body directly. Editing the base risks an
operator's override drifting back out of sync on the next pull.

Refs: #405

### compose-db-secret-vs-run-secrets-split

Each trusted service mounts its Postgres role password at
`/run/db-secret/<name>`, deliberately different from `/run/secrets/<name>`.
The development override mounts a read-only `dev-secrets` volume at
`/run/secrets` on these same services, and a read-only mount's tree cannot
host a new nested mountpoint — colliding targets fail `docker compose up`
closed with "read-only file system".

Refs: #844, #845

### compose-backend-artifact-mount-scope

The backend mounts the shared `artifacts` volume read-only: it has no
execution dispatcher and never writes an artifact itself, only serves
already-written scan/download output back to API callers.
compliance-runner and download-runner hold the read-write mount.

Refs: #442 (AC5), ADR-0014 §7

### compose-tool-upload-staging-volume

`tool-upload-staging` is a separate, staging-only volume from the
`managed-tool` store. The backend deliberately does not mount
`managed-tool` at all, so it never gets write access to the verified tool
binary or its trust anchor — only to the staging path where an uploaded
artifact + signature wait for download-runner to claim the install job.

Refs: #621, #630, ADR-0014 §7

### compose-postgres-healthcheck-wrapper

A bare `pg_isready` passes on a half-initialized cluster (initdb ran, but
`docker-entrypoint-initdb.d` aborted before creating the runner roles or the
`keycloak` database). The wrapper script also asserts both initdb scripts
completed. `start_period: 120s` replaces an old 5s: a socket-only temp
server is already `pg_isready`-positive before the initdb scripts run at
all, and a fresh `up --build` starting six services has live-exceeded 15s.

Refs: #844

### compose-keycloak-hostname-strict

`--hostname-strict=true` is required for `KC_HOSTNAME` to pin one canonical
issuer — `hostname-strict=false` (start-dev's default) was live-verified to
keep deriving the discovery document's `issuer` from whatever `Host` header
arrived. That, plus the explicit `/auth` suffix (hostname-v2 does not
auto-append `KC_HTTP_RELATIVE_PATH`), keeps a browser request and a direct
`backend` request from disagreeing on `iss`.

Refs: #536, #842

### compose-realm-placeholder-substitution

Keycloak's realm-import placeholder substitution (`${WAYPOINT_PUBLIC_URL}`
in `waypoint-realm.json`) is off by default. `JAVA_OPTS_APPEND` turns it on
for the `--import-realm` boot path — live-verified that `KC_*` env vars do
not map to this JVM system property. Without it, Keycloak imports the
literal string `${WAYPOINT_PUBLIC_URL}` instead of the real value.

Refs: #844

### compose-keycloak-relative-path

`KC_HTTP_RELATIVE_PATH=/auth` tells Keycloak's own URL-building to include
the prefix nginx actually proxies it under — without it, every
self-referential URL (discovery document, login-form `action=`) renders
without `/auth` and 404s through nginx, a silent broken-login redirect. This
shifts every Keycloak-served path, including the management health endpoint
(`/auth/health/ready`), live-verified against a real bring-up.

Refs: #534, #536

### compose-module-preload-order

`WaypointLogging` must preload first: the imported vmware-stig-docker
transport files the other modules dot-source expect `Get-LogSplat`/
`Write-Log` already in scope. `ModulePreloadCompletenessTests` fails the
build if a future handler's module ships without an entry here — the guard
exists because `WaypointComplianceContent` was once missing and silently
failed with "term ... is not recognized".

Refs: #579, #613

### compose-runner-egress-topology

Both runners previously sat only on `internal` (no route out). `runner-egress`
is a second, non-`internal:` bridge network giving both a route out while
`postgres` stays on `internal` alone — neither runner's new network reaches
Postgres. Detaching download-runner in an override needs Compose's
`networks: !override [internal]` tag; a plain `networks: [internal]`
union-merges and silently leaves `runner-egress` attached.

Refs: #578

### compose-replica-scaling

One replica of each runner is the default; ADR-0013 decision 6 deliberately
does not multiply services without measured need. Scaling is safe by
construction — each replica claims jobs from Postgres under the same
skip-locked contract, so `--scale compliance-runner=2` cannot double-run a
job — but N replicas compete for one host's CPU/memory, which is the real
reason to measure before raising `deploy.replicas`.

Refs: ADR-0013 (decision 6)

### compose-secrets-fail-closed

Compose's own file-secret mechanism (not Swarm secrets). `docker compose
config` does NOT fail on a missing `file:` source — it renders and exits 0,
which is why CI can validate this file with no `deploy/config/` present. On
`docker compose up`, the daemon rejects the bind at container-create time,
so no service ever starts: fail-closed, but the enforcement point is the
daemon at create time, not the client at parse time.

Refs: #844, PR #860

### compose-secrets-mode-0644-convention

A `file:` source is bind-mounted verbatim — the container sees the host
file's ownership/mode, with no Swarm-style `0444` re-materialization — so
`0644` is this repo's convention for mounted secret material. An
empty/unreadable file passes the fail-closed check above; each consumer
(postgres/keycloak entrypoint wrappers) validates its own content first.

Refs: #844, PR #860

## compose.override.example.yaml

### override-dev-bootstrap-local-auth-design

`dev-bootstrap` generates a throwaway TLS pair, master key, and (via
`dev-auth-bootstrap`) a local-auth admin password hash into named volumes,
replacing the base's operator-provided equivalents for a local loop.
`dev-auth-bootstrap` runs the backend image itself in a one-shot,
non-networked mode purely to reuse its `--hash-password` tool. The
`LocalAuth__*` trio must never be set on a real deployment.

Refs: #845, #29, #333, #62

### override-volume-subpath-replacement

Compose merges `volumes:` entries by target: an entry here whose `target:`
exactly matches the base's replaces it rather than adding a second mount at
the same path. The base's TLS mounts are mandatory with no host file
present by default (`create_host_path: false`), so without this override
nginx never starts; with it, these two entries point at subpaths of the
dev-bootstrap-generated `dev-tls` volume instead.

Refs: #845

### override-dev-admin-idempotent-provisioning

`keycloak-dev-admin` creates-or-finds the user by username, then reconciles
the password (non-temporary) and Admin-group membership on every run, so a
changed password file or removed group membership is restored on the next
`up`. Changing `WAYPOINT_DEV_ADMIN_USERNAME` provisions a NEW user rather
than renaming the old one — the provisioner never deletes accounts.

Refs: #846

### override-public-url-localhost-default

The base's `public-url` anchor falls back to a deliberately nonfunctional
placeholder domain. This override replaces it with a working `localhost`
default sized to `WAYPOINT_HTTPS_PORT`, so a fresh `docker compose up`
gets a real login with no manual step — `keycloak`'s own
`WAYPOINT_PUBLIC_URL`/`KC_HOSTNAME` carry the same expression on purpose.

Refs: #845, #842

## scripts/generate-dev-stack.sh

### gen-persistent-project-name

Persistent mode uses the Compose project name `waypoint`, matching
compose.yaml's own `name: waypoint`, rather than a second "-dev" identity.
Persistent mode is the one recurring human dev loop against that same base,
so the two should never diverge.

Refs: #885

### gen-host-path-translation

A bind-mount `source:` this script writes into a compose override is
resolved by the Docker daemon against the HOST filesystem, not this
process's own. Inside a devcontainer whose workspace is itself a bind
mount, the daemon-visible path differs, so every source this script emits
is translated via the container's own inspected mounts before use.

Refs: #847

### gen-project-ownership-discriminator

A Compose project name is claimed by RUNNING containers carrying that
project label, not by the mere existence of a state directory — a bare
`mkdir` would otherwise let any caller silently claim a project name.
Ownership is decided from `com.docker.compose.project.working_dir`
(accepting both in-container and host-side spellings), with the
generator's own artifact file as a fallback.

Refs: #847

### gen-local-auth-hash-copy

The caller-supplied admin password hash is copied into this run's own state
directory, and the copy is what the generated override mounts, so the slug
directory stays self-contained (one `rm -rf` removes everything) and
callers never have to pre-create the state directory to stage the hash.

Refs: #847

### gen-secret-reuse-never-overwrite

Existing secrets and TLS material are reused, never regenerated, once
present. For a TLS pair specifically, a partial pair (one file present, the
other missing or empty) must never be silently completed — regenerating
both would overwrite the survivor. Reuse requires both files non-empty.

Refs: #847

### gen-env-file-merge

deploy/.env is merged, not overwritten: only the keys this script owns are
added or replaced in place, and any other line (an operator's own
addition) is left exactly as-is, in its original position.

Refs: #847

### gen-env-file-list-merge

Agent mode drives port/subnet/public-url through a slug-scoped `--env-file`
and compose.yaml's own operator-config anchors, not `ports:`/`networks:`
overrides. Compose's merge semantics APPEND list entries under those keys
rather than replacing by target/subnet — verified live: an override with
its own `ports:` published both the base's default port and its own, side
by side.

Refs: #847

### gen-build-context-not-host-path

Unlike a bind-mount `source:` (resolved by the daemon against the host
filesystem), `build: context:` is resolved by the buildx client from
wherever `docker compose build` runs. Passing the host-path-translated
absolute path broke with "unable to prepare context: path not found", so
the keycloak-dev-admin build context stays relative (`./keycloak-dev-admin`).

Refs: #847, #846

### gen-devcontainer-build-up-split

When devcontainer bind-mount indirection is detected (`HOST_PREFIX !=
REPO_ROOT`), a single `up -d --build` fails: `build` contexts are resolved
client-side, but the bind-mount sources this override writes need
`--project-directory` resolved against the host path. The printed commands
split into a client-path `build` (no `--project-directory`) and a
host-path `up -d --no-build`, matching the split documented in
`deploy/README.md`'s troubleshooting entry.

Refs: #955

## scripts/init-config.sh

### initcfg-secret-file-mode-0644

Secret files are written 0644, not 0600: Compose bind-mounts a `file:`
secret source verbatim (host uid/mode preserved), and postgres's initdb
scripts read their three files as the in-container `postgres` user, neither
root nor this script's uid. 0644 is this repo's convention for every
mounted secret file.

Refs: #844

## scripts/fresh-stack-smoke-test.sh

### smoke-in-network-helper

HTTP checks route through a helper container on the stack's own `edge`
network via `docker exec`, rather than the host-published port directly. A
published container port is not reliably reachable from the test process's
own network namespace in every environment (devcontainer / remote-daemon
setups) — a throwaway container on the same network reaches nginx fine
where the process itself gets ECONNREFUSED/timeout.

Refs: #219, #444

### smoke-hash-stage-separation

The admin-password hash is computed and staged in a separate scratch
directory, never inside the generator's own state directory. This script
must not create the generator's state directory behind its back: the
generator refuses a foreign stack that has already claimed this project
name, and a caller that pre-creates state must not influence that decision.

Refs: #847

### smoke-seeding-preconditions

Two preconditions are seeded before a scan job can honestly fail against
its invented unreachable target instead of failing earlier at a readiness
gate: the three expected profile-path subdirectories are pre-created on the
fresh `compliance-profiles` volume, and a resolvable `srg-ssh`
credential-purpose binding is created for the just-created target before
the run is submitted.

Refs: #444, #733, #726, #882, #639

### smoke-cancel-pause-race

Cancelling a job racing a fast-failing invented-unreachable target
intermittently lost the race against the runner claiming and failing it
first. The run is paused before the cancel request so the dispatcher never
claims the still-queued job — cancel then deterministically moves it to
`cancelled`. A run/job already terminal before the pause lands is accepted
as correct product behavior, not a test failure.

Refs: #498

### smoke-credential-owner-shared

`owner` must be `'shared'` on every credential this script creates: there
is no per-user credential ownership model in v1, so any other value is a
400 validation_error.

Refs: ADR-0011

## scripts/e2e-playwright.sh

### e2e-npm-ci-stamp

`[[ ! -d node_modules ]]` only tests whether the directory exists, not
whether it matches package-lock.json — a checkout whose node_modules
predates a devDependency addition satisfies that guard and skips `npm ci`.
Install correctness is instead tracked with a stamp file, written only on
a successful `npm ci`, containing the lockfile's hash.

Refs: #906

### e2e-reachability-probe

Whether a docker-published host port is reachable from this process's own
network namespace is a property of the environment, not of this stack —
so it is probed once, generically, with a disposable helper container
BEFORE the stack is even generated. That lets `--public-url` already be
the real navigation origin instead of guessing "localhost" and discovering
only after bring-up that the browser can't reach it.

Refs: #896, #904

### e2e-reachability-probe-retry-bound

The retry loop is bounded because with `userland-proxy=false` a connect
landing before `nc` finishes binding is refused even though the port
genuinely works once listening — an unbounded retry would mask a probe
that never actually succeeds.

Refs: #896, #904

### e2e-playwright-exit-capture

`set -euo pipefail` would otherwise kill the script the instant the
Playwright subshell exits nonzero, swallowing a failed run as whatever the
EXIT trap's cleanup produced — before the exit code is captured or the
zero-tests-executed guard runs. `set +e`/`set -e` bracket exactly the
subshell so errexit is suspended only long enough to capture the real code.

Refs: #848, #500

## scripts/keycloak-realm-import.sh

### realmimport-host-path-translation

A bind-mount SOURCE given to a plain `docker run -v` (unlike `docker
compose`'s own bind mounts) is not translated inside a devcontainer whose
workspace is itself a bind mount — the daemon looks for that literal path
on the host, silently mounting an empty directory. The scratch dir's
host-side path is resolved from the container's own inspected mounts;
`deploy/` is used rather than `/tmp` because container `/tmp` isn't shared.

Refs: #28

### realmimport-python-not-sed

sed's replacement side has its own metacharacters (`&`, `\`, and any
delimiter appearing in the value), so a generated secret containing them
was silently mangled. A literal, non-regex replacement in python3 has no
metacharacters and also JSON-escapes the value. The secret is passed
through the environment, never argv, since argv is visible in `ps`/
`/proc/<pid>/cmdline` to anything else on the host.

Refs: #844, #860

### realmimport-stop-before-reimport

Running the throwaway import alongside a live `keycloak` service against
the same database silently no-ops: the realm delete appears to succeed, but
the throwaway boot's `IGNORE_EXISTING` import then also silently does
nothing. Two Keycloak processes clustering against one DB was never a
supported concurrent-write scenario, so the compose-managed service is
stopped first and restarted at the end via a trap.

Refs: #28

### realmimport-server-boot-not-cli

Keycloak 25's standalone `import --override true` CLI logs success and
exits 0 against an already-initialized database, but the realm silently
does not land — reproduced against both a stopped and a running `keycloak`
service. Keycloak's own server-boot import path (`start-dev
--import-realm`, `IGNORE_EXISTING`) reliably creates the realm when it does
not already exist.

Refs: #28

### realmimport-delete-then-reboot-strategy

This script therefore deletes the existing realm via the admin REST API,
then boots a throwaway `keycloak` container against the same database with
`--import-realm` — slower than a bare `kc.sh import`, but the only path
actually proven to persist (see realmimport-server-boot-not-cli).

Refs: #28

## scripts/keycloak-realm-export.sh

### realmexport-host-path-translation

A bind-mount SOURCE given to a plain `docker run -v` is resolved by the
Docker daemon against the HOST filesystem, with no translation from inside
a devcontainer whose workspace is itself a bind mount — an un-translated
path silently exports into an empty directory the daemon creates, while
the export command still reports success. The output directory's
host-side path is resolved from the container's own inspected mounts.

Refs: #28

### realmexport-throwaway-container

`kc.sh export` requires exclusive DB access via its own embedded
connection, not available while `start-dev` owns the process. A throwaway
container runs `export` against the same database instead — Keycloak's own
docs support this, since export/import both connect to `KC_DB_URL`
directly.

Refs: #28

## nginx/

### nginx-tls-fail-closed

The production compose base bind-mounts the operator's own certificate/key
onto `tls.crt`/`tls.key` and fails closed at container creation if either is
absent. The dev override replaces the same two mount points with
dev-bootstrap's throwaway self-signed pair — this config reads the same two
filenames either way, with no in-config branch on dev/prod.

Refs: #844

### nginx-dynamic-backend-resolution

A literal `proxy_pass http://backend:8080;` target is resolved once at
config load and cached for the worker's lifetime. Recreating the `backend`
or `keycloak` container gives it a new IP on the bridge network, and nginx
keeps dialling the stale address until `nginx -s reload`. The fix is a
`resolver` directive plus a proxy_pass target that carries a *variable*
(`$backend_host`/`$keycloak_host`) so nginx re-resolves per request.

Refs: #59

### nginx-request-method-allowlist

Rejects methods this stack never serves (TRACE/CONNECT/etc., DISA/CIS
guidance) with a single `if ($request_method !~ ...) { return 405; }` at
server level rather than `limit_except` per location, which would have to
be repeated across every `location` block. The allowed set is derived from
docs/api-contract.md's full resource table.

Refs: #388, #52

### nginx-csp-same-origin

The PWA is a zero-external-asset bundle (no CDN fonts/images, no inline
style/script), so `default-src 'self'` covers scripts/styles/images/fonts
with no relaxation, and `connect-src 'self'` covers both REST and SSE
traffic through this same proxy. A future screen needing an inline
style/script or cross-origin connection must change this directive with it.

Refs: #52

### nginx-upload-size-override

The vcf-download-tool artifact runs hundreds of MB, well past nginx's 1 MB
default `client_max_body_size`. This is scoped to the single upload route
rather than raised globally, so every other `/api/` request keeps the small
default cap. Must change together with `ManagedToolController.MaxUploadBytes`
and `FormOptions.MultipartBodyLengthLimit` — nginx's cap alone isn't enough.

Refs: #620, #641

### nginx-auth-relative-path-passthrough

Keycloak is configured with `KC_HTTP_RELATIVE_PATH=/auth` and itself serves
at that same prefix, so the raw request URI is forwarded unmodified — no
prefix stripping here. An earlier trailing-slash `proxy_pass .../;` trick
only works for a *literal* target; once the target became a resolver-driven
variable (nginx-dynamic-backend-resolution), stripping stopped happening
and Keycloak broke the whole login flow.

Refs: #28, #534

### nginx-healthz-split

`/healthz` (liveness) and `/healthz/upstream` (readiness: nginx AND the
backend are reachable) are kept as distinct endpoints, and this file stays
separate from `default.conf`, rather than folding backend reachability into
the container HEALTHCHECK. Folding them would make nginx report unhealthy
every time `backend` is legitimately recreated (self-update, `restart`).

Refs: #66

## postgres/

### postgres-poisoned-volume-fail-closed

The initdb scripts' own checks run AFTER `initdb` has already created and
populated the data directory — an empty/unreadable secret file used to fail
at a point where the damage was already done: the container restarted, the
second boot found a non-empty data directory and skipped initialization,
and postgres reported HEALTHY with no runner roles. Only `down -v`
recovered a volume poisoned this way.

Refs: #844, PR #860

### postgres-wrapper-validates-before-initdb

Validating every mounted secret file in a wrapper BEFORE `exec`ing the
stock entrypoint means a bad file aborts the container before initdb ever
touches the data directory: it restart-loops on a clean error, the volume
stays pristine, and fixing the file lets the next restart initialize it
correctly (see postgres-poisoned-volume-fail-closed).

Refs: #844, PR #860

### postgres-runtime-user-readability-check

Compose `secrets:` with a `file:` source is a plain bind mount of the HOST
file — no 0444 re-materialization the way Swarm secrets do it. The stock
entrypoint drops to the `postgres` user before running initdb scripts, so a
host file only root can read is readable in this root-run wrapper and NOT
by the scripts that consume it — live-observed after initdb had already
created the data directory. Readability is checked as the runtime user.

Refs: #844, PR #860

### postgres-role-asserting-healthcheck

`pg_isready` alone answers "the server accepts connections", which a
half-initialized cluster answers too — a backend migration then fails on a
missing role, and Keycloak can't log in, both AFTER `depends_on:
service_healthy` said go. The healthcheck therefore also asserts both
initdb scripts completed. This pairs with the 120s `start_period` on the
compose healthcheck so a cold-cache first boot has room to finish initdb.

Refs: #844

### postgres-secret-file-password-source

Runner and Keycloak DB passwords are file-backed (`*_PASSWORD_FILE`,
Compose `secrets:`-mounted), not inline env vars, so a password never
appears in `docker inspect`/`docker compose config` output. A missing file
is caught by the daemon at create time; an empty/unreadable one is caught
by the entrypoint wrapper (see postgres-poisoned-volume-fail-closed).

Refs: #844, #442, #28

## keycloak/

### keycloak-wrapper-file-loading

Live-verified against quay.io/keycloak/keycloak:25.0: a `--vault=file
--vault-dir` setup with `KC_DB_PASSWORD='${vault.db-password}'` still fails
datasource startup — kc.sh's vault substitution doesn't apply to
`db-password` or the bootstrap admin password in this version, and neither
has a built-in `_FILE` indirection. A thin wrapper exporting the mounted
files as plain env vars before `kc.sh` is the only fail-closed option.

Refs: #844

### keycloak-realm-placeholder-substitution

Unlike the DB/admin passwords, the realm client secret uses Keycloak's own
`keycloak.migration.replace-placeholders` substitution at import time (same
mechanism as `rootUrl`/`redirectUris`/`webOrigins`): `waypoint-realm.json`'s
`secret` field is the placeholder `${WAYPOINT_BACKEND_CLIENT_SECRET}`. The
wrapper's only job is exporting the value for that engine to read.

Refs: #842, #844

## keycloak-dev-admin/

### kcdevadmin-secret-passing-design

`curl`+`jq` against the Admin REST API directly, not `kcadm.sh`: kcadm's
own `config credentials` step takes `--password` only as a CLI flag — the
argv/`docker top` exposure this script exists to avoid. Every curl call
carrying a secret goes through a `-K` config file instead of `-d`/`-H`; the
one place a secret must reach an external binary (`jq`) uses `--rawfile`
so only the file path is an argument.

Refs: #846, epic #841

### kcdevadmin-verify-profile-requirement

Keycloak's default declarative user profile marks `email`/`firstName`/
`lastName` required; a user missing them gets a silent `VERIFY_PROFILE`
required-action injected at the next login (the user representation itself
shows `requiredActions: []`) — live-verified against this stack. That would
break the direct-login acceptance criterion, so the script sets all three
on every reconcile pass.

Refs: #846, #890

### kcdevadmin-urlencode-semantics

Pure-shell percent-encoding (not an external tool) so operator-settable
values never become an external command's argument. Forces `LC_ALL=C`:
`${_rest#?}` is locale-aware and consumes a whole multi-byte character
under UTF-8, while `printf '%%%02X' "'$_ch"` only encodes the first byte —
those two disagree and silently drop bytes unless both operate one byte at
a time under `LC_ALL=C`.

Refs: #890

### kcdevadmin-rename-semantics

Find-or-create keys on the username, so changing
`WAYPOINT_DEV_ADMIN_USERNAME` provisions a brand-new user rather than
renaming the existing one — the previous user is left enabled, still in the
Admin group. This script never deletes accounts: silently deleting on a
config change would be a surprising, unrecoverable side effect for a
dev-only provisioner.

Refs: #846, #890

### kcdevadmin-default-email-derivation

The default email is derived from the username, not a fixed literal, so
the reconcile-on-every-run guarantee holds for email too — a default tied
to the OLD username would collide with the still-present old user instead
of provisioning the renamed one.

Refs: #846, #890

## dev-bootstrap/
