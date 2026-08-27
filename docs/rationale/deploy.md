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
  constraint — not a restatement of the code).
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
independently configured settings that a real deployment had to keep in
lockstep by hand. `x-operator-config.public-url` is the one browser-facing
HTTPS origin both are now derived from, so they cannot drift out of sync.
The fallback is a deliberately nonfunctional placeholder domain, never
`localhost`, so an unconfigured base fails OIDC discovery/login clearly
instead of quietly working against a loopback origin nothing external can
reach.

Refs: #842

### compose-edge-subnet-pinning

`ForwardedHeaders:KnownNetworks` needs to trust exactly the network nginx
and the backend share, not the whole RFC 1918 space. The chosen /24 sits
outside Docker's default address-pool allocation order and outside the
low `192.168.0.0/16` range a typical home/office LAN or nested Docker host
is likely to already use, so a fixed subnet is unlikely to collide on a
shared or nested host. Only `edge` is pinned; `internal` has no route out,
so a fixed subnet buys it nothing.

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
directory at that path and nginx failing later with a confusing TLS error.

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
`Database__PasswordFile`, resolved by `DatabaseConnectionStringResolver`,
is mandatory and always wins once populated — postgres itself now refuses
to start without its own `POSTGRES_PASSWORD_FILE`, so an inline password
here would only ever be an unused literal sitting in `docker inspect`
output. The same pattern repeats for both runners' own least-privilege
roles.

Refs: #843, #844

### compose-master-key-file-only

`FileMasterKeyProvider` only ever reads a file, by design — env vars leak
via `/proc/<pid>/environ`, `docker inspect`, and crash dumps. The key is
read lazily on first secret-store operation, not at startup, so a stack
without the mount still comes up healthy; only a credential write fails,
with `FileMasterKeyProvider`'s own "No master key is configured" error.

Refs: #405, ADR-0005

### compose-master-key-override-not-uncomment

The commented mount block is the mount SHAPE to copy into an operator's
own `compose.override.yaml` (auto-loaded, no `-f` needed) — not something
to uncomment in this service body directly. Editing the base risks an
operator's override drifting back out of sync on the next pull; the
override is the one durable place for a real deployment's secret paths.

Refs: #405

### compose-db-secret-vs-run-secrets-split

Each trusted service mounts its Postgres role password at
`/run/db-secret/<name>`, a deliberately different target than the default
`/run/secrets/<name>`. The development override mounts a read-only
`dev-secrets` named volume at `/run/secrets` on these same services, and a
read-only mount's directory tree cannot host a new nested mountpoint —
`docker compose up` fails closed with "read-only file system" if both
targets collide.

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
binary or the RSA release-key trust anchor — only to the staging path
where an uploaded artifact + signature wait for download-runner to claim
the tool-install job that reads them back.

Refs: #621, #630, ADR-0014 §7

### compose-postgres-healthcheck-wrapper

A bare `pg_isready` passes on a half-initialized cluster (initdb ran, but
`docker-entrypoint-initdb.d` aborted before creating the runner roles or
the `keycloak` database the rest of the stack depends on). The wrapper
script also asserts both initdb scripts completed. `start_period: 120s` is
deliberately longer than the old 5s: a socket-only temp server is already
pg_isready-positive partway through `initdb`, before the initdb scripts
have run at all, and a fresh `up --build` starting six services at once
has been live-observed exceeding a 15s window.

Refs: #844

### compose-keycloak-hostname-strict

`--hostname-strict=true` is required for `KC_HOSTNAME` to actually pin one
canonical issuer — `hostname-strict=false` (start-dev's implicit default)
was live-verified to keep deriving the discovery document's `issuer` from
whatever `Host` header the request arrived with, even with `KC_HOSTNAME`
set. Without both this flag and the explicit `/auth` suffix on
`KC_HOSTNAME` (hostname-v2 does not auto-append
`KC_HTTP_RELATIVE_PATH`), a browser request through nginx and a direct
container-network request from `backend` would disagree on `iss`, and no
single `ValidIssuer` on the backend could match both.

Refs: #536, #842

### compose-realm-placeholder-substitution

Keycloak's realm-import placeholder substitution
(`${WAYPOINT_PUBLIC_URL}` in `waypoint-realm.json`) is off by default.
`JAVA_OPTS_APPEND` turns it on for the `--import-realm` boot path —
live-verified that `KC_*` env vars do not map to this JVM system property,
it must go through `JAVA_OPTS_APPEND`. Without it, Keycloak imports the
literal string `${WAYPOINT_PUBLIC_URL}` as the client's redirect
URI/origin instead of substituting the real value.

Refs: #844

### compose-keycloak-relative-path

`KC_HTTP_RELATIVE_PATH=/auth` tells Keycloak's own URL-building to include
the prefix nginx actually proxies it under — without it, every
self-referential URL (discovery document endpoints, login-form `action=`)
renders without `/auth` and 404s through nginx, a silent broken-login
redirect rather than an obvious error. This shifts EVERY Keycloak-served
path, including the management port's own health endpoint
(`/auth/health/ready`, not `/health/ready`), live-verified against a real
bring-up.

Refs: #534, #536

### compose-module-preload-order

`WaypointLogging` must preload first: the imported vmware-stig-docker
transport files the other modules dot-source expect
`Get-LogSplat`/`Write-Log` already in scope. `ModulePreloadCompletenessTests`
fails the build if a future handler's module ships without an entry in
this list — the guard exists because `WaypointComplianceContent` was once
missing here and `Invoke-WaypointComplianceContentPull` silently failed
with "term ... is not recognized".

Refs: #579, #613

### compose-runner-egress-topology

Both runners previously sat only on `internal` (no route out), so neither
could reach anything outside the compose stack — compliance-runner
couldn't reach scan targets/STIG Manager, download-runner couldn't reach
the Broadcom depot. `runner-egress` is a second, non-`internal:` bridge
network that gives both runners a route out while `postgres` stays on
`internal` alone: neither runner's new network reaches Postgres, and
nothing on `edge`/`internal` gains a route out through this change. An
operator running disconnected/air-gapped can detach download-runner from
it, or override the network's driver options, in their own override.

Refs: #578

### compose-replica-scaling

One replica of each runner is the default; ADR-0013 decision 6
deliberately does not multiply services without measured need. See
deploy/README.md's replica-safe scaling note before setting
`deploy.replicas` > 1 on either runner.

Refs: ADR-0013 (decision 6)

### compose-secrets-fail-closed

Compose's own file-secret mechanism (not Swarm secrets — this also works
for plain `docker compose up`). `docker compose config` does NOT fail on a
missing `file:` source; it renders and exits 0, which is why CI can
validate this file with no `deploy/config/` present. On `docker compose
up`, the daemon rejects the bind at container-create time, so no service
ever starts — fail-closed, but the enforcement point is the daemon at
create time, not the client at parse time. A file that exists but is
empty/unreadable gets past that layer entirely, so each consumer (the
postgres and keycloak entrypoint wrappers) validates its own before doing
anything destructive. A `file:` source is bind-mounted verbatim — the
container sees the host file's ownership/mode, there is no 0444
re-materialization — so 0644 is this repo's convention for mounted secret
material.

Refs: #844, PR #860

## compose.override.example.yaml

### override-dev-bootstrap-local-auth-design

`dev-bootstrap` generates a throwaway TLS pair, master key, and (via
`dev-auth-bootstrap`) a local-auth admin password hash into named volumes,
replacing the base's operator-provided equivalents for a local loop.
`dev-auth-bootstrap` runs the backend image itself in a one-shot,
non-networked mode purely to reuse its own `--hash-password` tool — it is
never reachable as a service. The backend's `LocalAuth__*` trio
(`LocalAuthOptions`, `InMemoryLocalAuthenticationService`) must never be
set on a real deployment; the base ships none of them, so an unconfigured
production stack 404s `AuthController.Login` outright.

Refs: #845, #29, #333, #62

### override-volume-subpath-replacement

Compose merges `volumes:` entries by target (verified): an entry here
whose `target:` exactly matches one of the base's replaces it rather than
adding a second mount at the same path. The base's TLS mounts are
mandatory bind mounts with no host file present by default
(`create_host_path: false`), so without this override nginx never starts;
with it, these two entries take over both exact targets and point them at
subpaths of the dev-bootstrap-generated `dev-tls` named volume instead. A
directory-level mount at `/etc/nginx/certs` would coexist with, not
replace, the base's per-file mounts and still fail on the missing bind
sources — it must be these same two targets.

Refs: #845

### override-dev-admin-idempotent-provisioning

`keycloak-dev-admin` creates-or-finds the user by username, then
reconciles the password (non-temporary) and Admin-group membership on
every run, so a changed password file or a manually-removed group
membership is restored on the next `up`. Changing
`WAYPOINT_DEV_ADMIN_USERNAME` provisions a NEW user rather than renaming
the old one — the provisioner never deletes accounts (deliberate for a
dev-only tool) — so the previously provisioned account stays enabled and
usable until removed by hand or via `docker compose down -v`.

Refs: #846

### override-public-url-localhost-default

The base's `public-url` anchor falls back to a deliberately nonfunctional
placeholder domain. This override replaces it with a working `localhost`
default sized to the same `WAYPOINT_HTTPS_PORT` the base's nginx `ports:`
mapping uses, so a fresh `docker compose up` gets a real login with no
manual `WAYPOINT_PUBLIC_URL` step. `keycloak`'s `WAYPOINT_PUBLIC_URL`/
`KC_HOSTNAME` carry the identical expression on purpose — the two values
must never drift apart (see compose-public-url-single-origin).

Refs: #845, #842

## scripts/generate-dev-stack.sh

## scripts/init-config.sh

## scripts/fresh-stack-smoke-test.sh

## scripts/e2e-playwright.sh

## scripts/keycloak-realm-import.sh

## scripts/keycloak-realm-export.sh

## nginx/

## postgres/

## keycloak/

## keycloak-dev-admin/

## dev-bootstrap/
