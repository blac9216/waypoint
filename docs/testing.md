# Testing & running the stack

Read this before running `docker compose` in this repository — especially if you are
an agent working alongside others.

Multiple agents (and humans) run this stack **on the same Docker host at the same
time**. The compose file is not safe to run naively: two concurrent stacks collide,
and the collision is silent. This document is the isolation contract everyone follows.

---

## The rule

**Never run `deploy/docker-compose.yml` without isolating it.** Not "usually", not
"unless you're quick". Every bring-up gets its own project name, its own container
names, and its own host port.

## Why `-p` alone does not work

The natural assumption — `docker compose -p my-name up` gives me my own stack — is
**wrong here**, and that is the trap.

`deploy/docker-compose.yml` sets explicit `container_name:` values
(`waypoint-nginx`, `waypoint-backend`, `waypoint-postgres`). Explicit container names
are **not namespaced by the Compose project**. Networks and named volumes are
(`<project>_edge`, `<project>_pgdata`), so `-p` looks like it worked — but the
containers themselves are global. A second stack reuses and **recreates** the first
one's containers.

What that costs you is worse than an error message: it is a *plausible wrong result*.
Someone else's healthy container answers your probe and you record a pass. Someone
else recreates the backend mid-run and you record a failure that is not yours. Both
look exactly like evidence. This has already happened once between two agents
(issue [#68](https://github.com/blac9216/waypoint/issues/68), which tracks the real
fix; until it lands, the recipe below is the mitigation).

## The recipe

Pick a slug unique to your work — the issue you are on plus your role, e.g.
`issue3-fix`, `review-67`. Pick a host port nobody else is using (the default is
`8443`; pick something well away from it, e.g. `18443`, `19443`).

```bash
SLUG=issue3-fix          # your unique slug
PORT=18443               # your unique host port

# 1. Override file: unique container names. Lives in /tmp, never in the repo.
cat > /tmp/wp-$SLUG.override.yml <<EOF
services:
  nginx:
    container_name: wp-$SLUG-nginx
  backend:
    container_name: wp-$SLUG-backend
  postgres:
    container_name: wp-$SLUG-postgres
EOF

# 2. Bring up: unique project (-p), unique names (override), unique port (env var).
cd deploy
WAYPOINT_HTTPS_PORT=$PORT docker compose -p wp-$SLUG \
  -f docker-compose.yml -f /tmp/wp-$SLUG.override.yml up -d
```

Three independent things are being separated, and you need all three:

| Collides on | Isolated by |
| --- | --- |
| Container names | the override file (Compose does not namespace these) |
| Networks, volumes | `-p wp-$SLUG` |
| Host port `8443` | `WAYPOINT_HTTPS_PORT=$PORT` |

**Use an override file, not `sed` to strip `container_name`.** A stripped *copy* of
the compose file placed outside `deploy/` breaks every relative bind mount
(`./nginx/conf.d`, `./www`, …), because Compose resolves those against the first
compose file's directory. The override approach keeps `deploy/docker-compose.yml`
as the first `-f`, so paths still resolve — and you are testing the real file rather
than a mutated one.

## Verify your isolation before you trust a result

`docker compose config` renders the merged configuration **without starting
anything**. Check it first:

```bash
cd deploy
WAYPOINT_HTTPS_PORT=$PORT docker compose -p wp-$SLUG \
  -f docker-compose.yml -f /tmp/wp-$SLUG.override.yml config \
  | grep -E "^name:|container_name:|published:"
```

Expect your slug in every container name, your project in `name:`, and your port in
`published:`. If you see `waypoint-nginx` or `8443`, stop — you are about to
collide.

Then confirm what is actually running is yours:

```bash
docker ps --format '{{.Names}}\t{{.Ports}}'
```

### `up -d` returning does not mean healthy

`docker compose up -d` returns once containers are *created*, not once they pass a
healthcheck. nginx here has `start_period: 5s` and `interval: 10s`, so a `docker ps`
on the very next line reports `health: starting` and an assertion on `healthy` fails
against a stack that is completely fine three seconds later.

The same shape bites `docker run -d` plus nginx's `resolver … valid=10s`: the
container exists, but nginx is still dialing the previous address, so your first
request 502s. Both defects are invisible if you run the commands by hand — typing
supplies the delay — and appear the moment a reviewer pastes the block. Wait for the
condition, bounded, rather than sleeping a guessed interval:

```bash
for _ in $(seq 60); do
  [ "$(docker inspect -f '{{.State.Health.Status}}' wp-$SLUG-nginx 2>/dev/null)" = healthy ] && break
  sleep 1
done
```

Then run every command block you are going to publish **as a pasted block**, not line
by line. A step that only works at human typing speed is a step that fails for
everyone who trusts it.

## Rules of engagement with other stacks

- **Look before you start.** `docker ps` first. Containers you did not create belong
  to someone else's in-flight verification.
- **Never touch what you did not create.** No `docker rm`, `stop`, `restart`, or
  `compose down` against another slug — you will corrupt a result someone is
  currently recording.
- **Tear down only your own project**, and always:
  ```bash
  cd deploy && docker compose -p wp-$SLUG \
    -f docker-compose.yml -f /tmp/wp-$SLUG.override.yml down -v
  rm -f /tmp/wp-$SLUG.override.yml
  ```
  `-v` drops your named volumes; without it, `pgdata` survives and the next run
  inherits state you did not intend.
- **Never pattern-kill processes.** `pkill -f vite`, `pkill -f node`, `killall dotnet`
  and friends match every agent's dev server, not just yours. Up to half a dozen run
  concurrently on this host. Record your own PIDs when you start something and kill
  those by number:
  ```bash
  npm run dev -- --port $PORT & echo $! > /tmp/wp-$SLUG.pid
  kill "$(cat /tmp/wp-$SLUG.pid)"
  ```
  A pattern kill that happens to hit nothing is luck, not method — and the agent whose
  server you killed will report a failure that never happened.
- **If you collided anyway**, say so plainly in your report and re-run the affected
  verification under isolation. A contaminated result handed onward is worse than no
  result, because the next person treats it as evidence.

## Dev servers collide the same way containers do

Everything above applies to `npm run dev` too, and the failure is sneakier because
there is no container to `docker ps`. Two specific traps, both hit during real work
in this repo:

**`$!` after `npm run dev` is npm's PID, not vite's.** Kill it and you orphan the
`vite` child, which keeps holding the port; your cleanup reports success and the
server survives to confuse the next agent.

**With `--strictPort`, a busy port makes vite exit — and a naive readiness loop then
passes anyway.** `until curl -sf localhost:$PORT` is satisfied by *whoever already
owns that port*, so you probe another agent's app and record the result as your own.
This is exactly the plausible-wrong-result hazard the container section opens with.

Start vite directly, take its real PID, and assert the process is alive **before**
you probe:

```bash
PORT=5873                      # yours; check nothing else is on it first
npx vite --port "$PORT" --strictPort & VITE_PID=$!
sleep 2
kill -0 "$VITE_PID" 2>/dev/null || { echo "vite died - port $PORT taken"; exit 1; }
until curl -sf "http://localhost:$PORT" >/dev/null; do
  kill -0 "$VITE_PID" 2>/dev/null || { echo "vite exited during startup"; exit 1; }
  sleep 0.5
done
# ... your checks ...
kill "$VITE_PID"
```

The liveness check inside the loop is the load-bearing part: without it, "the server
is up" and "a server is up" are indistinguishable.

## Honest verification

Two standing rules for anything you claim in a PR body or review:

1. **Never claim a check you did not execute.** If Docker, the .NET SDK, a browser,
   or anything else is unavailable in your sandbox, put it under a
   `## Verification limits` heading with the exact commands a reviewer with that tool
   can run. An honest gap is useful; a fabricated pass is a defect that outlives you.
2. **Run every command in your Suggested Test Steps exactly as written**, from the
   final rendered body, before you post it. Several PRs have shipped snippets that
   fail or silently no-op for a reviewer who pastes them — `git revert ` with no SHA,
   a `grep -A3` that stops short of the line it claims to show, an `echo` whose
   payload was eaten. A broken step makes a healthy PR look broken.

3. **Read the stored body back after posting, and re-check every command.** This is
   not the same as rule 2, and writing the body correctly is *not sufficient*.
   GitHub strips angle-bracketed text from stored issue and PR bodies, so
   `<link rel=…>` inside an `echo`, or a `<sha>` placeholder in a Rollback line,
   silently becomes nothing — turning a correct command into one that writes an empty
   file or reverts nothing. This has now happened on three separate PRs, twice
   *after* the author verified the text before posting.

   Prefer avoiding `<…>` in prose and commands altogether (use `printf`, a heredoc,
   or a named placeholder like `COMMIT_SHA`), then fetch the body back and confirm
   what a reviewer will actually paste.

   Practical note: there is no `gh` CLI here and the raw GitHub API is blocked, so
   read-back means the MCP tools. `pull_request_read` returns the body **HTML-escaped**
   (`&amp;`, `&gt;`), so unescape before diffing or every command with a redirect will
   look wrong. Diff the command lines programmatically rather than eyeballing them —
   the failure being guarded against is a character that is *silently absent*, which
   is exactly what the eye skips over.

## A real browser is available — do not substitute it away

Chromium **is** installable and runnable in these sandboxes:

```bash
npx playwright install chromium     # succeeds
```

The environment also pre-provisions one (`PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers`);
if a project pins a different Playwright version, launch with
`executablePath: '/opt/pw-browsers/chromium'` rather than re-downloading.

Two PRs have been submitted claiming browser-dependent steps could not be run,
substituting jsdom harnesses and endpoint curls. The substitutions were honest and
clearly disclosed — they were simply **unnecessary**, and a reviewer who tried
harder ran all of them in real Chromium and found things the substitutes could not
show (mid-animation width sampling, computed transition timing, which element keeps
its accent border after a collapse).

Before writing "no browser available" under `## Verification limits`, try to install
one. A disclosed limit is honest; an *unnecessary* disclosed limit still costs the
reviewer the work of closing it, and quietly weakens what the PR proves.

## Sandbox egress: why `dotnet restore` fails in a Docker build

Agent sandboxes reach the internet through a **TLS-terminating proxy bound to
loopback**. Two consequences bite the backend image build, and both look like
repository defects when they are not:

1. `docker build` runs `RUN` steps in a **bridged** network namespace, where
   `127.0.0.1` is the container's own loopback, not the host's. The proxy is simply
   absent, and `RUN dotnet restore` fails with
   `NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json`.
2. Even once reachable, the proxy re-signs TLS, so NuGet rejects the certificate
   unless the proxy CA (`/root/.ccr/ca-bundle.crt`) is trusted inside the build.

**Never hardcode the proxy port** — it changes when the proxy restarts, and
`$HTTPS_PROXY` can go stale in an already-running shell (observed mid-session:
the variable still said `43705` after the proxy had moved to `36133`, so every
request through it failed while the proxy itself was perfectly healthy). Resolve it,
and verify, before you trust it:

```bash
# Authoritative current value; /root/.ccr/README.md also states it in its first line.
PROXY_URL="$(grep -oE 'http://127\.0\.0\.1:[0-9]+' /root/.ccr/README.md | head -1)"
curl -sSf --max-time 10 "$PROXY_URL/__agentproxy/status" >/dev/null \
  && echo "proxy OK at $PROXY_URL" || echo "stale/down - re-read the README"
```

A dead proxy and a policy denial look nothing alike: a denial is a **403/407**, and
per the README it must be reported, not routed around. Everything this repo needs
(nuget.org, mcr.microsoft.com, the Docker registries, npm, GitHub) is reachable — if
you get a connection refused, the port moved; you are not blocked.

**This is an environment limitation, not a defect in `backend/Dockerfile`.** On an
open network the plain `docker compose build` works. Do not file it as a finding, and
**never modify a repository file to work around it** — a committed proxy workaround is
both wrong off-sandbox and a sanitization risk.

Two workarounds are verified to work. Either is fine; neither touches the repo:

```bash
# A. Host networking + trusted CA. --network=host genuinely shares the host's
#    namespace (verified: a container run this way sees the host's lo/eth0/docker0),
#    so the loopback proxy is reachable. The docker bridge gateway is NOT an
#    alternative - the proxy does not listen on it (connection refused).
docker build --network=host \
  --build-arg HTTPS_PROXY="$PROXY_URL" \
  --build-arg SSL_CERT_FILE=/ca/ca-bundle.crt \
  ...

# B. Supply an SDK base image that already trusts the CA
docker build --build-context sdk-with-ca=... ...
```

Quick confirmation that egress works at all, without building the image:

```bash
cd backend && docker run --rm --network=host \
  -e HTTPS_PROXY="$PROXY_URL" -e NO_PROXY=localhost,127.0.0.1 \
  -e SSL_CERT_FILE=/ca/ca-bundle.crt \
  -v /root/.ccr/ca-bundle.crt:/ca/ca-bundle.crt:ro \
  -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet restore Waypoint.sln
```

If you could not build the backend image at all, say so under
`## Verification limits` and state which workaround you tried — do not report a
bring-up as passing when the backend never started, and do not attribute the
failure to the PR under review.

## Per-component test suites

The stack-level contract is above. Each component owns its own suite and documents it
in its own README as it lands:

- **`backend/`** — `dotnet build` / `dotnet test`, plus the image's self-answering
  health probe. See `backend/README.md`.
- **`frontend/`** — `npm run build` (which **must** fail on any external asset, per
  [ADR-0007](adr/0007-frontend.md)) and `npm test`. See `frontend/README.md`.
- **`deploy/`** — bring-up and the SSE `proxy_buffering off` requirement from
  [ADR-0003](adr/0003-reverse-proxy-nginx.md). See `deploy/README.md`.
