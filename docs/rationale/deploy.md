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

## compose.override.example.yaml

## scripts/generate-dev-stack.sh

## scripts/init-config.sh

## scripts/fresh-stack-smoke-test.sh

## scripts/e2e-playwright.sh

## scripts/keycloak-realm-import.sh

## scripts/keycloak-realm-export.sh

## nginx/

### nginx-tls-fail-closed

The production compose base bind-mounts the operator's own certificate/key
onto `tls.crt`/`tls.key` and fails closed at container creation if either is
absent. The dev override replaces the same two mount points with
dev-bootstrap's throwaway self-signed pair. Either way this config reads the
SAME two filenames, so it never changes per deployment and there is no
in-config branch on connected/disconnected or dev/prod.

Refs: #844

### nginx-dynamic-backend-resolution

A literal `proxy_pass http://backend:8080;` target is resolved once at
config load and cached for the worker's lifetime. Recreating the `backend`
or `keycloak` container (self-update, plain restart) gives it a new IP on
the bridge network, and nginx keeps dialling the stale address until
`nginx -s reload`. The fix is a `resolver` directive plus a proxy_pass
target that carries a *variable* (`$backend_host`/`$keycloak_host`, never
the literal service name), so nginx re-resolves per request instead of
caching one lookup forever. `127.0.0.11` is Docker's embedded DNS, present
on every container on a user-defined bridge network.

Refs: #59

### nginx-request-method-allowlist

Rejects methods this stack never serves (TRACE/CONNECT/etc., DISA/CIS
guidance) with a single `if ($request_method !~ ...) { return 405; }` at
server level rather than `limit_except` per location, which would have to
be repeated and kept in sync across every `location` block in this file.
The allowed set (GET/HEAD/POST/PUT/DELETE/OPTIONS) is derived from
docs/api-contract.md's full resource table.

Refs: #388, #52

### nginx-csp-same-origin

The PWA is a zero-external-asset bundle (no CDN fonts/images, no inline
style/script), so `default-src 'self'` covers scripts, styles, images, and
fonts without any `unsafe-inline`/`unsafe-eval` relaxation, and
`connect-src 'self'` covers both REST and SSE traffic through this same
proxy. If a future screen needs an inline style/script or a cross-origin
connection, this directive has to change with it.

Refs: #52

### nginx-upload-size-override

The vcf-download-tool artifact runs hundreds of MB, well past nginx's 1 MB
default `client_max_body_size`. This is scoped to the single upload route
(exact-match location, evaluated before the general `/api/` prefix) rather
than raised globally, so every other `/api/` request keeps the small
default body cap. Must be changed together with
`ManagedToolController.MaxUploadBytes` and the action's
`FormOptions.MultipartBodyLengthLimit` — nginx's cap alone is not enough.

Refs: #620, #641

### nginx-auth-relative-path-passthrough

Keycloak is configured with `KC_HTTP_RELATIVE_PATH=/auth` and itself serves
at that same prefix, so the raw request URI is forwarded unmodified — no
prefix stripping here. An earlier version tried the trailing-slash
`proxy_pass .../;` stripping trick, but that only works for a *literal*
proxy_pass target; once the target became a resolver-driven variable (see
nginx-dynamic-backend-resolution), no stripping ever happened and Keycloak
rendered every self-referential URL without `/auth`, breaking the whole
login flow.

Refs: #28, #534

### nginx-healthz-split

`/healthz` (liveness: nginx itself is up) and `/healthz/upstream`
(readiness: nginx AND the backend it proxies to are reachable) are kept as
distinct endpoints, and this file stays separate from `default.conf`,
rather than folding backend reachability into the container HEALTHCHECK.
Folding them together would make nginx report unhealthy every time
`backend` is legitimately recreated (self-update, `docker compose restart
backend`), and anything treating Docker HEALTHCHECK as a liveness signal
would then bounce or block on nginx for a problem that isn't nginx's.

Refs: #66

## postgres/

### postgres-poisoned-volume-fail-closed

The initdb scripts' own `[ -s ... ]` checks run from
`docker_process_init_files`, which the stock entrypoint invokes AFTER
`initdb` has already created and populated the data directory — an empty or
unreadable secret file used to fail at a point where the damage was already
done: the entrypoint aborted, `restart: unless-stopped` restarted the
container, the second boot found a non-empty data directory and skipped
initialization, and postgres reported HEALTHY with no runner roles and no
`keycloak` role/database. The pgdata volume was permanently poisoned (initdb
never re-runs) and only `down -v` recovered it. Validating every mounted
secret file in a wrapper BEFORE `exec`ing the stock entrypoint means a bad
secret file aborts the container before initdb ever touches the data
directory: the container restart-loops on the same clean error, the volume
stays pristine, and fixing the file lets the very next restart initialize
the same volume correctly.

Refs: #844, PR #860

### postgres-runtime-user-readability-check

Compose `secrets:` with a `file:` source is a plain bind mount of the HOST
file — there is no 0444 re-materialization the way Swarm secrets do it. The
stock entrypoint drops to the `postgres` user before running
docker-entrypoint-initdb.d, so a host file only root (or only the
operator's own uid) can read is readable in this root-run wrapper and NOT
readable by the scripts that actually consume it. Live-observed exactly
that failure after initdb had already created the data directory — the
poisoned-volume trap again — so readability is checked as the user the
server will actually run as (`su-exec "$RUNTIME_USER" sh -c 'test -r ...'`).

Refs: #844, PR #860

### postgres-role-asserting-healthcheck

`pg_isready` alone answers "the server accepts connections", which a
half-initialized cluster answers too. If initdb ran but
docker-entrypoint-initdb.d aborted partway, `pg_isready` reports healthy
while none of the roles or databases the rest of the stack depends on
exist — a backend migration then fails on a missing role and Keycloak
cannot log in at all, both AFTER `depends_on: service_healthy` said go. The
healthcheck therefore also asserts both initdb scripts actually completed
(four catalog lookups over the local unix socket). This pairs with a 120s
`start_period` on the compose healthcheck itself (see compose.yaml's
section) so a cold-cache first boot has room to finish initdb before the
first probe counts against it.

Refs: #844

### postgres-secret-file-password-source

Runner and Keycloak DB passwords are file-backed
(`*_PASSWORD_FILE`, Compose `secrets:`-mounted), not inline env vars, so a
password never appears in `docker inspect`/`docker compose config` output
or a committed migration file. A missing file is caught by the Docker
daemon at container-create time; an empty or unreadable one is caught by
the entrypoint wrapper before initdb runs at all (see
postgres-poisoned-volume-fail-closed), so the initdb scripts' own `[ -s
... ]` checks are only a last-ditch defence-in-depth layer. Values are
never echoed or logged — psql `-v` substitution keeps them out of this
script's own output and out of shell history inside the container.

Refs: #844, #442, #28

## keycloak/

### keycloak-wrapper-file-loading

Live-verified against quay.io/keycloak/keycloak:25.0: a `--vault=file
--vault-dir` setup with `KC_DB_PASSWORD='${vault.db-password}'` still fails
datasource startup with "The server requested SCRAM-based authentication,
but no password was provided" — kc.sh's vault substitution does not apply
to `db-password` (or, by the same server-bootstrap-ordering reasoning, to
the bootstrap admin password) in this version. `--db-password`/
`KEYCLOAK_ADMIN_PASSWORD` have no built-in `_FILE` indirection either
(unlike postgres:16-alpine's `POSTGRES_PASSWORD_FILE`). A thin wrapper that
reads the mounted files and exports the plain env vars before handing off
to `kc.sh` is therefore the only available fail-closed mechanism for those
two values.

Refs: #844

### keycloak-realm-placeholder-substitution

The realm client secret is handled differently from the DB/admin
passwords: `waypoint-realm.json`'s `secret` field is
`${WAYPOINT_BACKEND_CLIENT_SECRET}`, using Keycloak's own
`keycloak.migration.replace-placeholders` substitution at import time
(the same mechanism already proven for `rootUrl`/`redirectUris`/
`webOrigins` via `WAYPOINT_PUBLIC_URL`). The wrapper's only job for this
one value is to export it into the environment Keycloak's own substitution
engine reads — it does not need the vault-workaround treatment the other
two secrets need, because placeholder substitution already works for it.

Refs: #842, #844

## keycloak-dev-admin/

### kcdevadmin-secret-passing-design

`curl`+`jq` against the Admin REST API directly, not `kcadm.sh`: kcadm's
own `config credentials` step takes `--password` only as a CLI flag (no
file-based or stdin-based indirection in this image), which is exactly the
argv/`docker top` exposure this script exists to avoid. Every curl call
that carries a secret goes through a `-K` config file instead of `-d`/`-H`
on the command line; the one place a secret has to reach an external
binary (`jq`, building the reset-password JSON) uses `--rawfile` so only
the file path is an argument. Net effect: neither password nor the
short-lived bearer token ever appears in this container's argv.

Refs: #846, epic #841

### kcdevadmin-verify-profile-requirement

Keycloak's default declarative user profile marks `email` (and
`firstName`/`lastName`) required; a user missing them gets a silent
`VERIFY_PROFILE` required-action injected at the next login (the user
representation itself shows `requiredActions: []` even though login
redirects to a profile-completion form) — live-verified against this
stack. That would break the direct-login acceptance criterion, so the
script derives and sets all three on every reconcile pass rather than
leaving them to Keycloak's default.

Refs: #846, #890

### kcdevadmin-urlencode-semantics

Pure-shell percent-encoding (not an external tool) so operator-settable
values (username, group name) never become an external command's argument
and can't be mistaken for shell metacharacters. Forces `LC_ALL=C` for the
whole script: `${_rest#?}` is locale-aware and consumes a whole multi-byte
character under a UTF-8 locale, while `printf '%%%02X' "'$_ch"` only
encodes the first byte — under UTF-8 those two disagree and silently drop
bytes. Under `LC_ALL=C` both operate one byte at a time and agree.

Refs: #890

### kcdevadmin-rename-semantics

Find-or-create keys on the username, so changing
`WAYPOINT_DEV_ADMIN_USERNAME` provisions a brand-new user rather than
renaming the existing one — the previous user is left enabled, still in
the Admin group, still holding the reconciled password. This script never
deletes accounts: for a dev-only one-shot provisioner, silently deleting on
a config change would be a surprising, unrecoverable side effect. The
default email is derived from the username (rather than a fixed literal)
so this same reconcile-on-every-run guarantee holds for email too — a
default tied to the OLD username would collide with the still-present old
user instead of provisioning the renamed one.

Refs: #846, #890

## dev-bootstrap/
