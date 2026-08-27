# deploy

Docker Compose stack: nginx, backend, postgres, keycloak, and the
compliance/download runners. Docker Engine with Compose v2, `openssl`, and
`python3` are the only host prerequisites — the frontend bundle builds into
the `nginx` image, so there's no separate `npm run build` step. `python3`
is required by `generate-dev-stack.sh` (checked up front; used for the
subnet-collision check and hostname parse) and by `init-config.sh
--public-url`. Three supported bring-up paths share one mechanism: a script
writes file-backed secrets and (for the two dev paths) a validated Compose
override, and never starts a container itself.

## Quick start: Production

Operator-provided TLS, Keycloak-only auth, no dev bootstrap.

```bash
cd deploy
./scripts/init-config.sh --public-url https://waypoint.example.internal
# place your own CA-issued cert/key at config/tls/tls.crt and config/tls/tls.key
WAYPOINT_PUBLIC_URL=https://waypoint.example.internal docker compose up -d
```

`init-config.sh` generates the six base secrets (random, never printed, never
overwritten on re-run) and validates any TLS already present; it never
generates TLS material itself — a missing cert/key fails `docker compose up`
closed, the same as a missing secret. The reference tables below say what
each value gates.

## Production only: secrets master key

Both dev paths generate this key; `compose.yaml` alone ships none — it is
real key material for real credentials, so only an operator supplies it.
Until it is mounted the stack starts and reports healthy, but every
secret-bearing write (`POST /api/v1/credentials`) answers
`503 master_key_unavailable`.

```bash
cd deploy && mkdir -p config/secrets
openssl rand -hex 32 > config/secrets/waypoint-master-key
chmod 644 config/secrets/waypoint-master-key
```

`0644` per the mount convention below (`chown 1654 … && chmod 600` works
too). Never commit it — losing it makes already-encrypted credentials
unrecoverable. Mount it on the three trusted services from
`deploy/compose.override.yaml`, which the production `docker compose up -d`
auto-loads; don't edit `compose.yaml`'s service bodies. The load-bearing name
is the in-container `target`, which `WAYPOINT_MASTER_KEY_FILE` reads:

```yaml
# deploy/compose.override.yaml -- production only (never in a dev checkout,
# where both dev modes own this filename).
services:
  backend: &waypoint-master-key-mount
    volumes:
      - type: bind
        source: ./config/secrets/waypoint-master-key
        target: /run/secrets/waypoint-master-key
        read_only: true
        bind:
          create_host_path: false
  compliance-runner: *waypoint-master-key-mount
  download-runner: *waypoint-master-key-mount
```

Then `docker compose restart backend compliance-runner download-runner` —
`FileMasterKeyProvider` caches a missing-key failure for the process
lifetime, so the file and mount alone do not clear the 503.

## Quick start: Persistent development

The one recurring human dev loop, real Keycloak login included.

```bash
cd deploy
./scripts/generate-dev-stack.sh --mode persistent
docker compose -p waypoint -f compose.yaml -f compose.override.yaml --env-file .env up -d
```

The override's `dev-bootstrap` service provisions a throwaway self-signed
TLS pair and a random envelope-encryption master key on first `up`, so
there's no manual master-key step. Wait for health (a still-failing check
right after `up` is usually migrations — see the FAQ's first entry), then
open `https://localhost:8443` and log in through Keycloak's real
authorization-code + PKCE flow as `developer` — password is the contents of
`config/secrets/dev-admin-password`. `docker compose down` (no `-v`)
preserves Postgres and the Keycloak realm; add `-v` to reset everything (see
"Changing `WAYPOINT_PUBLIC_URL`" below).

In a devcontainer or against a remote Docker daemon, the `up` above fails on
the first secret's bind source without `--project-directory` — see
Troubleshooting's first entry before you start.

## Quick start: Isolated agent stacks

For a throwaway agent/CI bring-up, or a second stack alongside someone
else's on the same Docker host.

```bash
cd deploy
SLUG=issue3-fix PORT=18443 SUBNET=192.0.2.0/24   # yours, unique on the host
./scripts/generate-dev-stack.sh --mode agent --slug "$SLUG" \
  --public-url "https://localhost:$PORT" --port "$PORT" --subnet "$SUBNET"
```

Secrets, TLS, and a self-contained override are written entirely under
`deploy/.generated/<slug>/` — never `deploy/config/` or
`compose.override.yaml` — after checking for port/subnet/project collisions
against what's already running, and the command prints the exact `up`/`down`
lines to paste next. See [`docs/testing.md`](../docs/testing.md) for the full
isolation recipe concurrent agents/humans need on one host.

## Var reference

The `x-operator-config` anchor block at the top of `compose.yaml`:

| Anchor / env var | Production default | Dev override default | Drives |
| --- | --- | --- | --- |
| `public-url` / `WAYPOINT_PUBLIC_URL` | nonfunctional placeholder domain (must be set) | `https://localhost:${WAYPOINT_HTTPS_PORT:-8443}` | Keycloak's `KC_HOSTNAME`, the backend's token issuer, and the realm's `rootUrl`/`redirectUris`/`webOrigins` — one browser-facing origin for all of it |
| `https-port` / `WAYPOINT_HTTPS_PORT` | `8443` | `8443` | nginx's published HTTPS port |
| `edge-subnet` / `WAYPOINT_EDGE_SUBNET` | `192.168.240.0/24` | `192.168.240.0/24` | the `edge` bridge network's subnet |
| `tls-cert-file` / `tls-key-file` | `config/tls/tls.{crt,key}` (operator-provided) | dev-bootstrap-generated, mounted from a named volume | nginx's TLS listener |

Other env vars an operator may set on a trusted service in
`compose.override.yaml`: `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` (runner egress
through a site proxy).

## Secrets & config layout

Files under `deploy/config/secrets/` (gitignored) — `deploy/config.example/`
shows this layout with invented placeholder values:

| File | Consumer | Generated by |
| --- | --- | --- |
| `postgres-owner-password` | postgres, backend | `init-config.sh`, `generate-dev-stack.sh` (either mode) |
| `postgres-compliance-runner-password` | postgres, compliance-runner | same |
| `postgres-download-runner-password` | postgres, download-runner | same |
| `postgres-keycloak-password` | postgres, keycloak | same |
| `keycloak-bootstrap-admin-password` | keycloak (master-realm admin) | same |
| `keycloak-backend-client-secret` | keycloak (`waypoint-backend` client) | same |
| `dev-admin-password` | keycloak-dev-admin (dev-only `developer` user) | `generate-dev-stack.sh` only |
| `waypoint-master-key` | backend, compliance-runner, download-runner (envelope encryption) | production: operator-supplied, never generated (see "Production only: secrets master key" above); both dev modes generate their own (agent: `deploy/.generated/<slug>/secrets/`; persistent: the override's `dev-bootstrap` service, into a named volume) |

Agent-mode stacks write their own copies of all of the above, plus generated
TLS, under `deploy/.generated/<slug>/` instead.

Each file's whole content (minus a trailing newline) is the raw value — no
quoting, no `key=value` shape — bind-mounted `0644`, verbatim, matching the
uid the images run as. A missing file fails `docker compose up` closed at
container creation; an empty/unreadable one is caught by the consuming
service's own entrypoint wrapper before anything is initialized.

Also in the config layout: `deploy/keycloak/realm/waypoint-realm.json` (realm
definition, see `deploy/keycloak/README.md`) and `deploy/config/tls/tls.{crt,key}`
(operator certificate, production only).

## Troubleshooting

**From a devcontainer or remote-Docker-daemon shell, `docker compose up`
fails at container creation with "bind source path does not exist" on the
first secret file.** The daemon resolves relative bind-mount sources against
the **host** filesystem, not the shell's own. Pass `--project-directory
<host-side deploy path>`, keeping `-f`/`--env-file` as documented:

```bash
docker compose -p waypoint -f compose.yaml -f compose.override.yaml \
  --env-file .env --project-directory /host/path/to/deploy up -d
```

Find the host-side path with `docker inspect "$(hostname)" --format
'{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'` and match
the entry whose destination is your workspace mount — see
[`docs/testing.md`](../docs/testing.md) "Devcontainer bind mounts" for the
full explanation. `-f`/`--env-file` stay relative to the shell you're in;
only `--project-directory` needs the host-side translation.

**Changing `WAYPOINT_PUBLIC_URL` requires `down -v`.** The realm's
`rootUrl`/`redirectUris`/`webOrigins` placeholders substitute only once, at
Keycloak's import time — a persisted realm in an existing `pgdata` volume is
never reconciled against a later change. `down -v` resets that volume so the
next `up` re-imports with the new value; `deploy/config/` (secrets, TLS) is
untouched by `-v`, so the same dev login comes back.

## FAQ

**`docker compose up -d` returned, but a health check is still failing — did
something break?** Not necessarily. The backend's first boot runs pending
database migrations before it accepts connections, and a cold build/pull can
push that past a short health-check window. Give it a minute and re-run
`docker compose ps`.

**Where's the dev admin password?** `deploy/config/secrets/dev-admin-password`
(persistent mode) or `deploy/.generated/<slug>/secrets/dev-admin-password`
(agent mode) — plain text, no quoting. Username is `developer` unless
`--username` was passed to `generate-dev-stack.sh`.

**`down` vs `down -v` vs `stop`?** `stop` leaves containers in place (fastest
restart, still holds resources). `down` (no flags) removes containers and
networks but keeps named volumes — Postgres data, the Keycloak realm, and
generated dev TLS/master-key material all survive. `down -v` additionally
removes those volumes — a full reset, and the only way to pick up a changed
`WAYPOINT_PUBLIC_URL` (see above).
