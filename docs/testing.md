# Testing & running the stack

Read this before running `docker compose` in this repository — especially if you are
an agent working alongside others.

Multiple agents (and humans) run this stack **on the same Docker host at the same
time**. The compose file is not safe to run naively: two concurrent stacks collide,
and the collision is silent. This document is the isolation contract everyone follows.

## Postgres test fixture — remote Docker daemon

The Postgres integration test fixture (`PostgresFixture`) starts a disposable
PostgreSQL container via `docker run` and connects to it through a published host
port. By default it assumes the Docker daemon and test process share the same
network namespace, so it connects to `127.0.0.1`.

If you are running tests against a **remote Docker daemon** or a **mounted host
Docker socket** (e.g. from a devcontainer), the published port belongs to the
daemon host — not the test process's loopback — and every Postgres test will
time out after 60 seconds.

Set `WAYPOINT_TEST_PG_HOST` to the reachable address of the Docker daemon host:

```powershell
$env:WAYPOINT_TEST_PG_HOST = "<daemon-host-ip>"
dotnet test backend/Waypoint.sln
```

```bash
WAYPOINT_TEST_PG_HOST="<daemon-host-ip>" dotnet test backend/Waypoint.sln
```

The default remains `127.0.0.1` — local Docker users need not change anything.
The configured address is only used in the connection string; it is never logged.

### Bridge-networked test process (published ports unreachable at all)

If the test process itself runs in a container on its **own bridge network**
(e.g. a compose-managed devcontainer with the host's Docker socket mounted),
`WAYPOINT_TEST_PG_HOST` may not help either: inter-bridge isolation and the
host's DNAT rules can block the published port from **every** address —
loopback *and* the daemon-host gateway (verified empirically; issue #219).

Set `WAYPOINT_TEST_PG_NETWORK` to the network the test process is attached to.
The fixture then starts Postgres on that network (no published port) and
connects to the container's own address on 5432. Each fixture container gets a
unique address, so concurrent test runs on the same host cannot collide.

```bash
# Find your own network, then run the suite on it:
docker inspect "$(hostname)" --format '{{range $k,$v := .NetworkSettings.Networks}}{{$k}}{{end}}'
WAYPOINT_TEST_PG_NETWORK="<that-network>" dotnet test backend/Waypoint.sln
```

`WAYPOINT_TEST_PG_NETWORK` takes precedence over `WAYPOINT_TEST_PG_HOST`.
Addresses in this section are Docker-internal RFC 1918 values — never real lab
identifiers.

---

## Devcontainer bind mounts: source paths resolve on the host, not in the container

If you are running `docker compose` (or plain `docker run -v`) from inside the
devcontainer, this is the other trap that shares the same root cause as the two
sections above: **the Docker daemon you are talking to is the host's daemon**, reached
through the mounted `/var/run/docker.sock`. The daemon has never entered the
container's filesystem namespace, so every bind-mount **source** path in a `-v` flag
or a compose `volumes:` entry is resolved by the daemon **against the host
filesystem**, regardless of what container issued the command.

That matters because the devcontainer's workspace path is not the host's workspace
path. The devcontainer mounts the repo checkout at a container-side path (typically
under `/workspaces/...`), but the host-side directory backing that mount lives
somewhere else entirely — under the invoking user's home directory. The two paths
share no prefix, and nothing on the container filesystem tells you what the host-side
path is.

**Find the mapping yourself — do not guess or hardcode it.** `docker inspect` the
running devcontainer and read its own mounts, the same way the network-discovery
snippet above inspects `"$(hostname)"`:

```bash
docker inspect "$(hostname)" --format '{{range .Mounts}}{{.Source}} -> {{.Destination}}{{"\n"}}{{end}}'
```

The output is a table of `HOST_SIDE_PATH -> CONTAINER_SIDE_PATH` pairs. Find the entry
whose destination is your workspace mount (e.g. `/workspaces/YOUR_FOLDER`) — its source
is the host path you need for any new bind mount you construct from inside the
container. This mapping is specific to the machine and the user who started the
devcontainer; never copy a path you see in someone else's output into a script or
doc, and never commit a resolved path — always re-derive it with the command above.

### The failure mode: a silent, empty, root-owned directory

If a bind-mount source you construct does not exist on the **host**, the daemon does
not error. It silently creates an empty directory at that host path (owned by
`root`, since the daemon runs as root) and mounts *that* — empty — into the
container. Nothing in the compose or `docker run` output flags this; the container
starts, "succeeds", and simply has no data where you expected it. This was
misdiagnosed twice as unrelated problems (stale daemon state; a missing config
file) before the actual cause — a workspace-relative source path that only exists
inside the container — was identified.

### `/mnt` is shared 1:1; container `/tmp` is not shared at all

Two paths behave predictably, and knowing them turns this from a guessing game into
a checklist:

- **`/mnt` is bind-mounted host-to-container 1:1** (`/mnt:/mnt`) in this project's
  devcontainer. A path under `/mnt` means the same thing on both sides — safe to use
  as a bind-mount source from inside the container with no translation.
- **Container `/tmp` is not shared with the host at all.** It is the container's own
  filesystem layer. A source path you construct under `/tmp` inside the container
  does not exist on the host under that name, so it always hits the empty-directory
  trap above — this is why ad hoc scratch files under `/tmp` silently fail to appear
  in bind-mounted containers, and why this repo's own scratch-directory guidance for
  agents steers new temp files to a workspace or `/mnt` location instead.

### Two workarounds

1. **Stage the deploy tree under a both-sides-identical path.** Copy or check out
   the files you need to bind-mount into a directory under `/mnt` (a scratch
   subdirectory works well) instead of the devcontainer's workspace path. Any
   compose file or `-v` flag that references that `/mnt` path needs no translation.
2. **Translate sources to host-absolute paths.** Take the host-side path for your
   workspace mount from the `docker inspect` output above, and substitute it for the
   container-side workspace prefix in any bind-mount source you construct — by hand,
   or with a small `sed`/shell substitution keyed off the mapping you just looked up.

**The shipped defaults are correct on a real appliance host.** `deploy/docker-compose.yml`'s
relative bind-mount sources work as-is when Compose runs directly on the appliance
host (no devcontainer, no mounted-socket indirection — the daemon and the compose
invocation share one filesystem). This trap is specific to running the deploy compose
file *from inside* the devcontainer; do not "fix" the shipped compose file to work
around it.

### Symptom → cause

| Symptom | Likely cause |
| --- | --- |
| A bind-mounted config/data directory is empty inside the container, but you know the host-side content exists | Not daemon staleness — the bind-mount **source** path does not exist on the host; see above |
| nginx starts but serves no config (no server block) | Same: `conf.d` (or similar) was bind-mounted from a source path that only existed inside the devcontainer, so the daemon mounted an empty directory |
| A newly-created scratch file under `/tmp` is invisible to a container you just bind-mounted it into | Container `/tmp` is not shared with the host; use `/mnt` or a workspace path instead |
| The empty directory that got mounted is owned by `root` and you cannot delete it as yourself | Expected — the daemon (running as root) created it; remove it with `docker run --rm -v PARENT_DIR:/x alpine rm -rf /x/DIR_NAME` (substitute your own paths) or ask an operator with host access |

## The rule

**Never run `deploy/docker-compose.yml` without isolating it.** Not "usually", not
"unless you're quick". Every bring-up gets its own project name and its own host
port.

## Why `-p` alone used to not work — and now does

The natural assumption — `docker compose -p my-name up` gives me my own stack — used
to be **wrong here**, and it was a trap: `deploy/docker-compose.yml` used to set
explicit `container_name:` values (`waypoint-nginx`, `waypoint-backend`,
`waypoint-postgres`). Explicit container names are **not namespaced by the Compose
project**. Networks and named volumes are (`<project>_edge`, `<project>_pgdata`), so
`-p` looked like it worked — but the containers themselves were global, and a second
stack would reuse and **recreate** the first one's containers.

What that cost you was worse than an error message: it was a *plausible wrong
result*. Someone else's healthy container answers your probe and you record a pass.
Someone else recreates the backend mid-run and you record a failure that is not
yours. Both look exactly like evidence. This happened for real between two agents —
issue [#68](https://github.com/blac9216/waypoint/issues/68).

**#68 is fixed: `deploy/docker-compose.yml` no longer sets `container_name:` on any
service.** Without it, Compose derives container names from the project
(`<project>-<service>-<n>`, e.g. `wp-issue3-fix-nginx-1`), which **is** namespaced by
`-p`. `-p` alone now isolates containers, networks, and volumes together — the
override-file workaround below is no longer needed and should not be reintroduced.
Service-to-service traffic (nginx → `backend`, backend → `postgres`) was never
affected either way: nginx resolves the Compose **service** name via Docker's
embedded DNS (`resolver 127.0.0.11`, see `deploy/README.md` "Networking"), which has
always been per-project, never per-`container_name`.

## The recipe

If you are bringing this stack up **from inside the devcontainer**, first read
[Devcontainer bind mounts: source paths resolve on the host, not in the
container](#devcontainer-bind-mounts-source-paths-resolve-on-the-host-not-in-the-container)
above — a workspace-relative bind-mount source silently mounts empty, which looks
like the stack came up fine right up until nginx or the backend has no config.

Pick a slug unique to your work — the issue you are on plus your role, e.g.
`issue3-fix`, `review-67`. Pick a host port nobody else is using (the default is
`8443`; pick something well away from it, e.g. `18443`, `19443`).

```bash
SLUG=issue3-fix          # your unique slug
PORT=18443               # your unique host port

# Bring up: unique project (-p) + unique port (env var) is now sufficient.
cd deploy
WAYPOINT_HTTPS_PORT=$PORT docker compose -p wp-$SLUG up -d
```

Two independent things are being separated, and `-p` alone now covers both container
identity and the rest of the project's namespaced resources:

| Collides on | Isolated by |
| --- | --- |
| Container names, networks, volumes | `-p wp-$SLUG` |
| Host port `8443` | `WAYPOINT_HTTPS_PORT=$PORT` |

## Verify your isolation before you trust a result

`docker compose config` renders the merged configuration **without starting
anything**. Check it first:

```bash
cd deploy
WAYPOINT_HTTPS_PORT=$PORT docker compose -p wp-$SLUG config \
  | grep -E "^name:|published:"
```

Expect your project in `name:` and your port in `published:`. If you see `8443`
where you expected your own port, stop — you are about to collide.

Then confirm what is actually running is yours — Compose-derived names carry your
project slug as a prefix (`wp-$SLUG-nginx-1`, etc.):

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
  [ "$(docker inspect -f '{{.State.Health.Status}}' wp-$SLUG-nginx-1 2>/dev/null)" = healthy ] && break
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
  cd deploy && docker compose -p wp-$SLUG down -v
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

**`$!` gives you the wrapper's PID, not the server's.** This has now caught four
separate agents, on `npm run dev` (npm's PID, not vite's) and on `nohup dotnet …`
(nohup's, not the app's). Kill the wrapper and you orphan the child that actually
holds the port; your cleanup reports success and the server survives to confuse the
next agent. Assume `$!` is wrong for anything launched through a wrapper, and
**verify the port is actually free after you kill** rather than trusting the exit
code:

```bash
kill "$PID"; sleep 1
curl -sf "http://localhost:$PORT" >/dev/null && echo "STILL UP - wrong PID"
```

If something is still listening, find the real owner and confirm it is yours before
killing it — `/proc/<pid>/environ` or `/proc/<pid>/cwd` will tell you. Someone else's
server on a port you assumed was free is the same hazard from the other direction.

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

## Fixture monoculture: the defect this repo keeps shipping

Every serious defect that reached review in this repo got there the same way — **a
test suite that passed because every fixture shared an incidental property**, not
because the code was right. Four instances so far, in four unrelated subsystems:

| Suite | The property every fixture shared | What it hid |
| --- | --- | --- |
| sanitize delimiters | lowercase, address mid-sentence | a lab FQDN + IP walked the hard gate at end of sentence |
| sanitize IPv6 | the one fixture was a *compressed* address | matrix read 14/14 on "colon / port" while an expanded address plus port scanned clean |
| job engine auth halt | the queued job was always seeded oldest | the halt could be silently suppressed by a newer run |
| frontend mode guard | one route, role overwritten by a later fetch | the Viewer scenario proved nothing at all |

The counts looked healthy every time. `14/14`, `4 passed`, green CI. **A passing
suite is evidence about the fixtures, not about the code** — and the fixtures are
the part nobody re-reads.

Two habits catch it:

1. **Break the code and watch the test fail.** For every guard you add, revert it in
   isolation and record the failure count. A guard whose reversion breaks nothing is
   not tested, however many assertions surround it. Record the counts in the PR body
   so a reviewer re-measures rather than re-reads.
2. **Ask what every fixture has in common, then add one that doesn't.** If your
   inputs are all lowercase, add uppercase. All compressed, add expanded. All ordered
   one way, reverse them. If a shared property turns out to be load-bearing, you have
   found a real bound and should say so; if it isn't, you have just found the next
   defect before a reviewer did.

Where a suite enumerates cases against a fixed axis (delimiters, formats, roles,
states), **derive the axis from the thing under test** rather than hand-listing it —
a hand-written list is a monoculture with extra steps, and it silently stops covering
the code the moment the code grows a case the list never heard of.

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
   payload was eaten, or a pwsh `Import-Module backend/…/X.psm1` with a repo-relative
   path (pwsh rejects it — `Import-Module` needs an absolute/rooted path or a name on
   `$env:PSModulePath`, so root it, e.g. `Import-Module "$PWD/backend/…/X.psm1"`).
   A broken step makes a healthy PR look broken.

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

   **Angle brackets are not the only casualty, and a fenced code block does not
   protect you.** A reviewer diffed its posted comment and found a regex lookahead
   `(?![\w:])` stored as `(?[\w:])` — the `!` silently gone, inside a fence. The
   swallowed token is a lookahead-open immediately followed by `[`. So when a body
   or comment must carry a negative-lookahead regex, either space it so `(?!` is not
   adjacent to `[`, or describe it in words and point at the file — then read it back
   and diff. A regex that is quoted wrong is worse than one that is omitted, because
   the reader will paste it and believe the result.

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

## What CI covers — and does not

GitHub Actions runs four workflows — [`sanitize.yml`](../.github/workflows/sanitize.yml),
[`backend.yml`](../.github/workflows/backend.yml),
[`frontend.yml`](../.github/workflows/frontend.yml),
[`deploy.yml`](../.github/workflows/deploy.yml) — added in issue
[#79](https://github.com/blac9216/waypoint/issues/79):

| Workflow | Triggers on | What it runs |
| --- | --- | --- |
| `sanitize` | every PR + push, no path filter (hard gate) | the scanner's own test suite (`.github/sanitize/test_scan_repo_specific.py`), then a `gitleaks` full-history secret scan, then a repo-specific scanner (`.github/sanitize/scan_repo_specific.py`) for lab-style FQDNs, non-RFC-5737 IPv4 addresses, non-documentation/non-loopback/non-unspecified IPv6 addresses (issue #112), and Broadcom/VMware depot-token shapes |
| `backend` | `backend/**` | `dotnet build -warnaserror`, `dotnet test` with coverage, a coverage **floor** gate |
| `frontend` | `frontend/**` | `npm ci`, `npm run build`, the ADR-0007 air-gap asset guard **as its own explicit step**, `npm run test:coverage`, a coverage **floor** gate, `oxlint` |
| `deploy` | `deploy/**`, `scripts/**` | `docker compose config`, `nginx -t` against the shipped `conf.d` with a throwaway generated dev cert, `shellcheck` |

Every job is path-filtered except `sanitize`, which is a hard gate on everything —
a docs-only change still gets scanned, because a leaked hostname or token is just as
real in a markdown file as in code. Every job sets its own `concurrency` group with
`cancel-in-progress`, so a superseded push doesn't keep burning runner time. No
workflow references a repository secret; PR triggers are plain `pull_request`, never
`pull_request_target`; every third-party action is pinned by full commit SHA.

### Coverage gate: a committed floor, not a stored baseline (issue #102)

`backend.yml` and `frontend.yml` both run a coverage **floor** gate as their last
test-adjacent step, via [`scripts/check-coverage-floor.py`](../scripts/check-coverage-floor.py)
(stdlib-only Python, no network calls, no marketplace action). It parses the coverage
report each job already produces — backend's Cobertura XML from
`--collect:"XPlat Code Coverage"`, frontend's `coverage-summary.json` from vitest's
`json-summary` reporter (built into the already-vendored `@vitest/coverage-v8`, no new
dependency) — and fails the job if line coverage is below a hardcoded threshold.

Issue [#79](https://github.com/blac9216/waypoint/issues/79) asked for a true
**no-regression** check (fail only if a PR's coverage is lower than the base branch's)
in preference to a fixed floor, to avoid the first legitimately-hard-to-test path
forcing a waiver. That is the better gate, but it needs somewhere to keep the base
branch's number — either a persisted baseline artifact across CI runs or a file
committed to the repo and updated on every merge to `main`. This repository is fully
air-gapped (`CLAUDE.md`, ADR-0007): no Codecov, no Coveralls, no external coverage
service of any kind. Storing and comparing a baseline entirely inside self-hosted CI
is possible (a `coverage-baseline.json` committed to the repo, updated in the same PR
that raises coverage) but is deliberately **not** built here — issue #102 chose the
floor as the pragmatic air-gap-compatible interim, and a no-regression gate on a
committed baseline is left as a documented future option.

Current floors (backend re-baselined 2026-08-09 against current `main`; frontend
measured 2026-08-08 at commit `f2aadfe`):

| Project | Metric | Floor | Measured | Headroom |
| --- | --- | --- | --- | --- |
| backend | line | 88.0% | 88.84% (CI on current main, PR #391) | ~1 point |
| frontend | line | 88.0% | 90.12% (165/165 tests) | ~2 points |

The backend floor was set from the codebase's *current* coverage, not an older
high-water mark: 91.92% was measured at `f2aadfe`, but ~78 M2 commits landed
between then and the gate going live and moved the real number to 88.84%. The gate
is baselined at reality (adding it changed no product code), so this is not a
lowered floor accommodating a regression.

**Ratcheting the floor up**: re-measure locally (`dotnet test backend/Waypoint.sln
--collect:"XPlat Code Coverage"` / `npm run test:coverage` in `frontend/`), confirm the
new number holds, then raise the `--floor` value in the workflow file. Never lower a
floor to accommodate a regression — fix the regression instead. The floor is a ratchet
in one direction only.

**A green check is not proof of correctness.** CI is additive to the contextless
review this repo already relies on — it is never a substitute for it, and reviewers
have repeatedly caught real defects no CI run could have seen:

- **What CI cannot see at all**: the full `docker compose` bring-up (isolation
  discipline, the three-way collision described above, whether the stack is actually
  *your* stack and not someone else's recreated containers), any browser-driven check
  (mid-animation states, computed transition timing, which element keeps focus/accent
  after a collapse — see "A real browser is available" above), and anything that
  depends on the borrowed `dev/local/` depot material, which CI never has access to
  and never will (per the no-repository-secrets constraint).
- **What CI cannot judge even when it runs**: whether a passing test is passing for
  the right reason. This repo's own review history has caught a config that was
  correct by accident, an isolation test that reused an IP across two "independent"
  cases, and an unnecessary verification limit a reviewer closed by actually trying
  the thing instead of disclosing around it. A green `dotnet test` or `npm test` run
  proves the assertions held, not that the assertions were the right ones to write.
- **What `sanitize` cannot catch**: it is tuned against today's tree and today's
  known-legitimate patterns (RFC 5737 ranges, `*.example.<tld>`, loopback, Docker's
  `127.0.0.11`, four-part product versions immediately preceded by the word
  `version` AND a mandatory `:`/`=` separator (quote optional), so
  `version: A.B.C.D`, `--version=A.B.C.D` and `app.version=A.B.C.D` are suppressed as
  well as `version: 'A.B.C.D'`; a bare `version A.B.C.D` with only whitespace and no
  separator is NOT suppressed (issue #361 — that shape is a weak version cue and was
  waiving real IPv4 literals), and a `version` mentioned anywhere else on the line is
  not either). It is a
  mechanical backstop for the sanitization policy in `CLAUDE.md`, not a replacement
  for the diff-by-hand review that policy still requires — a sufficiently
  well-disguised secret or a lab identifier that doesn't match any of its patterns
  will not be flagged.

  Issue #111 narrowed the IPv4/FQDN adjacency guards just far enough to catch a
  dash-separated address range (illustratively, `192.0.2.1-192.0.2.7` — both
  endpoints, using an RFC 5737 pair here so the example itself stays
  allow-listed rather than tripping this very scanner) and an
  underscore-prefixed address (`host_` glued straight onto an address, or an
  identifier prefix glued straight onto a `*.example.<tld>`-shaped hostname)
  without reopening #89's build-suffix suppression — deliberately, not every
  adjacent-dash shape from that issue is closed. What still gets through, by
  design: a dash-adjacent address with nothing address-shaped on the *other*
  side of the dash (an address immediately followed by `-primary`, or preceded
  by a bare `-` with a word before it, or a hostname with a trailing
  `-01`-style suffix); a range whose separating dash falls exactly at a line
  break (only the endpoint that doesn't touch the break is caught — the check
  is per-line, same as the `version` suppression above); and a suspicious-TLD
  hostname immediately followed by an underscore-joined continuation of the
  same identifier — narrowing that trailing case too was tried and produced a
  real false positive against this repo's own `.editorconfig`
  (`EditorconfigRegressionTests` pins the specific line), so it was
  deliberately left as it was. Zero-padded octets are recognised at any
  padding width ([#119](https://github.com/blac9216/waypoint/issues/119)):
  the per-part digit count is not bounded in the regex at all, and whether a
  run of digits denotes a real address is decided in one place, by value.

  Issue #112 added an IPv6 detector (full/compressed forms, link-local,
  unique-local, the bracketed-with-port URL form, zone IDs); it has no
  analogous dash-adjacency guard at all, so an IPv6 range and a chained IPv4
  range of any length are both caught in full. That property used to be
  documentation only — nothing in the suite would have noticed if it stopped
  being true. Issue #132 found the gap the hard way: adding a trailing "not a
  dash" lookahead to the IPv6 pattern (described in words rather than written,
  for the reason given a few paragraphs down) took a dash-separated pair of
  IPv6 addresses from two findings to one — silently losing the second
  endpoint, the #111 defect for the other address family — with the full
  120-test suite green. It is pinned now: `GuardCharacterDelimiterTests`
  derives the set of punctuation characters that MATTER for this file's
  boundary guards straight off the compiled regexes (the same "read it off
  the detector instead of remembering it" treatment #115 gave the fixture
  count), and a dash is one of them because `FQDN_RE` names one explicitly —
  which is what forces IPv6 to carry a dash row too, even though `IPV6_RE`
  itself names none. Add a punctuation character to any detector's boundary
  lookaround and the derived set grows; leave the matching declared row out
  and the completeness test fails. Where the derivation stops — WHETHER a
  given character suppresses or flags for a given detector — is a measured,
  hand-authored verdict kept in a separate table from the derived character
  set, deliberately, so the two are never blended into one list that reads as
  derived when only the axis (not the verdict) is.

  **How much of a pattern the derivation can see, stated because the first
  cut of it could not see all of one.** It walks EVERY lookaround node in a
  compiled pattern's parse tree, at any nesting depth — the assertions at the
  top level and any placed inside a group, an alternation or a repeat. The
  first version walked the top level only, which was a true statement about
  the three detectors as written and a false mechanism: a guard nested one
  group deeper was invisible, and for a punctuation character that no
  top-level guard mentions *and* the hand-written delimiter lists do not
  contain, nothing in the suite covered it at all. Measured (PR #138 round 1,
  finding 2): a trailing guard nested inside the IPv6 pattern's bare
  alternative, rejecting a hash, a pipe and a plus, left the whole suite green
  while three real findings disappeared — one of them an address in a markdown
  table cell, which this repo's docs produce constantly. Recursing closed it;
  the same mutation now fails four tests.

  **The same defect had a second half, and finding one of them did not find
  the other.** The derivation is two functions: one decides WHERE assertions
  are (that is the recursion above), the other reads which characters a given
  assertion body NAMES. Round 1 fixed the first and gave it a structural
  guard rail; the second kept a hand-written list of body shapes and no rail
  at all, so a guard whose body was a repeat over a character class, or a
  branch whose alternatives are more than one node long, named punctuation
  that nothing derived (PR #138 round 2, finding 1). Measured, and it is the
  same measurement one level over: either spelling, spliced into the IPv6
  pattern's bare alternative over the same hash, pipe and plus, left the
  suite at 141/141 while the same three findings went from one to none. Both
  functions share ONE descent rule now, and the rail is symmetric: no node
  kind present in the real detectors may be missing from the shared list, and
  every kind on that list must be shown — by running a probe pattern, not by
  reading the code — to carry a guard's characters out to the derived set. A
  third spelling of the same defect was closed at the same time: a character
  class RANGE (`[!-/]` names a dot and a dash without either appearing as a
  literal) is now enumerated over printable ASCII, excluding alphanumerics so
  the real `[A-Za-z0-9]` guards still contribute nothing. All three closures
  are no-ops against today's detectors — asserted, not assumed.

  **What remains, disclosed here rather than found later**, and pinned by
  `test_the_disclosed_extraction_limit_is_what_the_doc_says` so this
  paragraph cannot drift from the code again:

  - What is derived is which characters a guard NAMES, never what the guard
    DOES with them — suppress or flag stays the hand-measured verdict table.
  - A guard that names characters only through an open-ended category
    (not-a-word, not-a-digit, not-whitespace) contributes none of them:
    enumerating those would demand a declared row for most of ASCII. This one
    cannot escape quietly, which is measured rather than argued — splicing a
    trailing "not a non-word character" guard into the bare alternative fails
    12 tests outright, because rejecting every non-word neighbour changes the
    measured verdict of characters the declared table already carries.
  - A guard that names its characters through a BACK-REFERENCE contributes
    nothing, and this is the one residual that is neither derived nor loud:
    the characters are not in the pattern at all, they are whatever the
    referenced group captured at match time, which no parse-tree read can
    know.

  **The first cut of that detector shipped with a boundary bug, and it is
  worth stating plainly rather than summarising away**: its trailing guard
  put a literal `.` inside the lookahead — the exact construction the IPv4
  detector's own comment warns against at length — so an IPv6 literal that
  ended a sentence was never matched, and a line ending in a lab ULA scanned
  `clean` with exit 0 on the real tree (PR #115 round 1, the same escape
  shape as the round-2 blocker on PR #83). The cause was not the regex, it
  was the test suite: `SurroundingContextTests` exists to enumerate exactly
  this, and the new detector simply never joined it. Both are fixed —
  every address detector is now driven through the same 14 trailing and 10
  leading delimiters from one table, and a check that appears in neither
  that table nor an adjacent, written-out exempt list fails a test.

  **A second boundary case survived that fix, for the same reason one level
  in.** A round later, a fully-expanded IPv6 literal followed by an
  unbracketed `:port` was still scanning `clean` on the real tree: the greedy
  hex/colon class swallowed the port, strict parsing failed on the ninth
  group, and "does not parse" was read as "not an address, so allowed"
  (PR #115 round 2). The matrix reported that delimiter as covered because
  each detector had exactly ONE fixture, and IPv6's was the *compressed*
  spelling, whose port digits absorb as a legal eighth group. The previous
  revision of this bullet ended by asserting that nothing left open was
  "a boundary case". That was wrong when it was written, and it was the second
  time in two rounds that a sentence in this file or in that scanner asserted
  an impossibility nobody had executed. So this is now a list of what is
  actually true, not a summary:

  - The single-group port case is closed: a trailing all-digit group is
    retried away before a candidate is declared a non-address, at any port
    width. Issue #131 found the retry ran only ONCE, so a candidate carrying
    TWO trailing all-digit groups (a fully-expanded literal followed by a
    bare port followed by a second numeric group — `...:0007:443:8443`, or
    the same shape with a zero-padded group ahead of the port,
    `...:0007:0:443`) still failed strict parsing and scanned clean. The
    retry is a loop now, not a single attempt: it strips one trailing
    all-digit group at a time until the parse succeeds, the tail is no longer
    all-digit, or **three** groups have been stripped. What is bounded is the
    NUMBER of groups retried, never any one group's width — a 30-digit
    trailing group still resolves, because an arbitrary width bound is
    exactly what #119 cost and this fix does not reintroduce one.
    **Why the loop is bounded at all: so that this bullet can be true.**
    Uncapped, the loop flags ANY colon-separated run whose first eight groups
    parse and whose remaining groups are all digits — so the disclosed #118
    false-positive class below becomes unbounded in RECORD LENGTH, not merely
    a group or two wider. That is a class no test can enumerate and no
    sentence here can state truthfully, and it is exactly how three separate
    places in this repo came to assert a bound of "exactly one group" that the
    loop no longer had. A pin named `..._are_still_only_these` cannot bound an
    infinite set. The false-positive count is a consequence of bounding, not
    the argument for it.
    **Why three, and what three costs.** Three is not free relative to two,
    and an earlier revision of this bullet implied it was — on a corpus that
    happened to contain no 11-group record, which made two and three look
    tied. Measured against a corpus with even coverage from 9 to 13 colon
    groups: cap 2 misses 4 of 10 leak shapes at 10 of 22 false positives; cap
    3 misses 3 at 13. So three buys one further leak shape — a literal with
    three trailing numeric groups, which no producer in the closed list this
    gate's threat model names (netstat, log lines, inventory exports,
    CKL/HDF, URLs) has ever been shown to emit — and costs three further
    false-positive families, all of them 11-group records: an EUI-64 string
    with three trailing numeric fields, a certificate fingerprint with the
    same, and an 11-field numeric counter record. It is a deliberate trade in
    favour of the leak direction, not a dominant choice: a missed lab address
    is an incident under `CLAUDE.md`, while a false positive is a visibly red
    gate. That the cost is real matters, because this gate runs with zero
    exemptions as a load-bearing property (below), so a false positive has no
    sanctioned waiver — it stops the gate until content is edited, and a gate
    that cries wolf gets switched off.
    **Two residuals, disclosed not closed** — they are the loop's two
    stopping conditions. A trailing group carrying a non-digit character
    glued on (`...:0007:443a`) fails the all-digit test on its very first
    retry attempt, so the loop never starts stripping it — a materially
    different shape (not a port at all, digit or otherwise). And four or
    more trailing all-digit groups (`...:0007:1:2:3:4`) exhaust the bound.
    Pinned by `MultiGroupPortRetryTests.test_a_glued_letter_on_the_final_
    group_remains_undetected` and `.test_four_trailing_all_digit_groups_
    are_a_disclosed_residual` rather than left to be rediscovered.
  - A match that begins *inside* a longer hex/colon run is re-anchored onto
    the widest span that still parses, so a word glued to the front of an
    address no longer reports a fragment of it. **Issue #133 narrowed WHICH
    parser decides "still parses" for this purpose.** Re-anchoring used to
    call the same port-retry-enabled parser the final candidate is checked
    with, so a span could win "widest" by discarding one of the real
    address's OWN trailing groups as if it were a port — `node99:<fully
    expanded address>` re-anchored onto `de99:<the address minus its own last
    group>`, naming a token that is not a string that appears on the line
    (`de99` is the tail of the hostname `node99`). Re-anchoring now judges
    candidate spans with the strict parser only; the port retry still applies
    to the final candidate scan_text reports, so an address that genuinely
    needs it to be recognised still is. This is precision, not correctness —
    the line was always a real finding, only the reported span was too wide.
    **Disclosed, not closed:** a colon-delimited numeric hostname suffix
    before a COMPRESSED address (an `esxi01:`-style label directly ahead of a
    `::`-compressed lab literal, reporting the label's own trailing digits as
    part of the address) is the same imprecision, one level over, with no
    port anywhere in the line — `"::"` gives a compressed address enough
    slack that prepending a short, independently-valid, colon-delimited group
    parses without any retry at all. Telling that shape apart from the
    legitimate glued-word-recovery case it is otherwise identical to
    (`X2001:db8::1`, MidRunMatchTests) needs information this gate's regex
    genuinely does not have — both are "one clean group-plus-colon directly
    before an already-valid address", and widest-wins correctly favours the
    case it exists for, which is why it cannot also refuse this one. Pinned
    by `MidRunMatchTests.test_a_numeric_hostname_suffix_before_a_compressed_
    address_is_a_disclosed_residual`.
  - The matrix enumerates several fixture VARIANTS per detector, and how many
    it owes is derived from the detector rather than remembered — from its
    regex's own alternations and optional groups, and from whether its
    variants cover both the canonical and a non-canonical spelling of the same
    value (compressed vs expanded, padded vs unpadded, upper vs lower case).
    Dropping the fully-expanded IPv6 fixture now fails a test.
  - **Left open on the delimiter side**, both mirroring the IPv4 and FQDN
    detectors and both pinned: a literal glued to a following `_` (an
    underscore reads as more of the same token on the trailing side — the
    `.editorconfig` reasoning above), and a literal followed by `.` plus an
    alphanumeric (a dotted continuation such as a hostname, where the
    address-shaped prefix is not standing alone).
  - **Narrowed, not eliminated, on the false-POSITIVE side** — a two-part
    identifier joined by `::`, both halves spelled entirely in hex LETTERS
    with no digit anywhere in the candidate (a `cafe`/`babe`-style
    placeholder, or a single hex-lettered word after `::`), no longer fires
    ([#118](https://github.com/blac9216/waypoint/issues/118)): every
    sanctioned spelling this gate allows carries at least one digit, so
    requiring one costs no real address, and every real lab literal this
    gate's threat model produces (inventory exports, netstat, logs, CKL/HDF,
    URLs) is hex-and-digits, not hex-letters-only prose. This is a narrow,
    digit-based exception on top of `ipaddress.IPv6Address` as the arbiter,
    not a loosening of it — a real, digit-bearing literal (including one
    whose other group is hex-letter-heavy) is unaffected, pinned by
    `HexLetteredIdentifierTests.test_real_ipv6_address_is_still_flagged`.
    **What is deliberately still open**, pinned by
    `test_the_known_residual_false_positives_are_still_only_these`: a run of
    colon-separated hex groups that happens to be valid IPv6 syntax AND
    carries a digit somewhere in it (an EUI-64-style run is the running
    example) is a different shape from the one just closed — the digit guard
    cannot touch it, by design, since the whole point of that guard is to
    leave any digit-bearing candidate exactly as reportable as before. The
    port retry widens that class by the number of groups it is allowed to
    strip, so a run of **nine, ten or eleven** groups whose trailing groups
    are all digits now resolves to the eight in front of them, and a
    twelve-group run is where the class stops. That the range is finite at
    all is the whole reason the retry loop carries a bound: uncapped, the
    class would be unbounded in record length — any colon-separated numeric
    record long enough to have eight parseable groups in front of an
    all-digit tail — which is a class no test can enumerate and no sentence
    here can state truthfully. The eleven-group rows specifically are what
    raising the bound from two to three cost (see the #131 bullet above);
    they are an EUI-64 string, a certificate fingerprint and a numeric
    counter record, each with three trailing numeric fields.
    `test_the_known_residual_false_positives_are_still_only_these` pins both
    ends, the nine- to eleven-group runs that fire and the twelve-group runs
    that do not. And an unbracketed
    `<sanctioned address>:<port>` pair, which as written is also a valid,
    different, non-sanctioned address — the gate reports rather than guesses,
    and the bracketed URL form is unambiguous and stays silent.
  - **Closed, not disclosed**: a zero-padded IPv4-mapped literal used to lose
    its IPv6 finding while the IPv4 detector still caught the embedded quad
    on its own — under-reported, not silent
    ([#123](https://github.com/blac9216/waypoint/issues/123)). The mapped
    form's embedded dotted quad is now normalized through the same
    `_parse_ipv4_octets` padding-width-independence #119 established for the
    plain IPv4 detector, at any padding width, so the IPv6 finding names the
    full literal rather than a truncated fragment or being dropped outright.
  - **Disclosed residual, not closed** — a four-part product version
    immediately followed by a file extension with NO preceding version key
    (a bare `<four-part-version>.ovf` / `.tar.gz` / `.zip`, no `version:` key
    in front) is flagged as an IPv4 literal
    ([#113](https://github.com/blac9216/waypoint/issues/113)). This is a
    deliberate false POSITIVE, accepted as the price of closing a false
    NEGATIVE. An earlier revision (PR #360 round 1) added a syntactic lookahead
    to `IPV4_RE` that rejected a `.`+short-alpha run so these version quads
    would not match. It was reverted: a version quad and an IPv4 literal are
    byte-for-byte identical, so the same lookahead ALSO suppressed a real,
    non-doc address in the same position — a routable four-octet quad glued to
    `.bak` / `.log` / `.csv` stopped matching, and a line like `exfil
    <routable-quad>.bak` passed the gate. That is a false negative on the
    load-bearing public-repo secret gate, the one outcome `CLAUDE.md` forbids;
    a spurious CI fail on a bundle filename is not. There is no structural
    signal that separates the two — the only reliable "this is a version" cue
    is a PRECEDING version key, which `is_version_string()` already honours (so
    a `version:`-keyed quad stays quiet even with an extension after it), and a
    bare `<version>.ovf` carries no such cue. This is #113's own documented
    **Option B**: accept the extensioned-version FP, do NOT encode an extension
    list (the shape that has failed open before). When a real bundle filename
    trips this, the fix is to add a version key or rename the file, never to
    reintroduce a syntactic extension guard. Both directions are pinned in
    `VersionExtensionTests` — the real IP + extension MUST flag, the keyless
    version quad + extension DOES flag, and the version-keyed quad stays quiet.
    (No dotted-quad literal is written into this bullet on purpose; the
    concrete cases are assembled via `quad()` in the tests — the #327 lesson.)
  - **Closed, not disclosed**: a backslash inserted immediately before each
    separator (`.` for IPv4/FQDN, `:` for IPv6) used to defeat all three
    detectors identically — not a suppression, an absence of any match at
    all, because the fragments on either side of the escaped separator were
    too short to independently satisfy a detector's structural floor
    ([#137](https://github.com/blac9216/waypoint/issues/137)). Unlike the
    encoding-evasion bucket below, this shape reads as a plausible everyday
    escaping convention (`.properties` files, some `rsync`/shell-quoted
    output, certain log formats), not obviously deliberate obfuscation,
    which is why it was named explicitly rather than folded into that
    bucket. Fixed at the model level: every line is normalized (a backslash
    directly before `.` or `:` is dropped) before any of the three
    detectors runs, rather than adding a fourth alternative to each regex.
    A backslash with no separator immediately after it (a Windows path, a
    regex source file, `\n`) is untouched. See
    `BackslashSeparatorEvasionTests`.
  - Everything else is a deliberate-evasion encoding (integer- or hex-encoded
    addresses, zero-width-space splitting), which is outside this gate's
    accidental-disclosure threat model rather than a boundary case it missed.
- **Value-narrow detector exceptions in force**: one. The three canonical RFC 1918
  whole-space CIDR literals (base address + exact canonical prefix, e.g. the
  ten-slash-eight) are not IP findings — they name every private network in
  existence and can identify no host (added for the #61 forwarded-headers
  defaults). The bare base quad, any other host, and any wrong, narrower, or
  digit-extended prefix all remain findings; `PrivateSpaceCidrTests` pins each
  direction. This is a value-level exception inside the detector, not a file
  exemption — every file is still scanned by every check.
- **Checks waived on a specific file** (`ALLOWLIST_FINDINGS` in `scan_repo_specific.py`):
  one entry, exactly one detector on it:
  - `runners/compliance-runner/powershell/module.transport.vmware.ps1` — `fqdn` check
    only. Waives the single `.local` FQDN hit on VMware's factory-default SSO domain
    (the `administrator@` account on the `vsphere` `.local` domain — spelled split
    here because `docs/testing.md` is a scanned file and the literal would itself trip
    the FQDN detector). That domain is baked into every vCenter and present verbatim in
    the unmodified project-owned source imported from `vmware-stig-docker`
    ([#438](https://github.com/blac9216/waypoint/issues/438)); it is a product
    constant, not lab data, and identifies nothing about the author's environment. The
    `ip`, `ipv6`, and `depot-token` detectors stay live on this file — only the one
    named FQDN check is waived, not the path.

  Every other git-tracked file that isn't a known-safe binary extension
  (`KNOWN_SAFE_BINARY_EXTENSIONS` — app icons, fonts, wasm; six `.png` icons today) is
  scanned by all four detectors. Keeping the allowlist this narrow matters because the
  alternative degrades silently: an exempt
  path reports clean while never having been looked at, and nothing in a green check
  distinguishes the two. That silent-degradation failure mode used to include a
  whole *category* of file, not just individually-listed paths: before issue #101,
  `SKIPPED_EXTENSIONS` covered certs/keys (`.pem`, `.key`, `.pfx`, `.p12`) and
  archives (`.zip`, `.gz`, `.tgz`, `.pdf`) too, and any of those passed clean without
  ever being read — a `.pem`'s Subject/SAN carries a hostname in plain text, which is
  exactly the shape this scanner exists to catch, and gitleaks only covers key
  *material*, not that. The fix split that list in two: `KNOWN_SAFE_BINARY_
  EXTENSIONS` stays a skip (icons/fonts/wasm cannot carry the kind of text payload
  these detectors look for), and everything else un-inspectable — the named
  `UNINSPECTABLE_EXTENSIONS` (certs/keys/archives/PDF) plus anything that simply
  fails to decode as UTF-8 — now **fails the run** the moment a tracked file matches,
  before any detector runs, with the finding naming the exact path and telling a
  human to either remove the file or add its extension to `KNOWN_SAFE_BINARY_
  EXTENSIONS` with a reason. `UninspectableFileTests` and
  `MainRefusesUninspectableFilesTests` in `test_scan_repo_specific.py` pin this. An earlier revision of this PR exempted one 204 KB UI mockup
  from *all three* detectors in order to waive a single hostname-naming nit, which
  left the IP and depot-token detectors dark on the most likely file in the repo for
  someone to paste real lab inventory into while demoing. That file was re-sanitized
  instead ([#86](https://github.com/blac9216/waypoint/issues/86)) and the exemption
  deleted. The replacement mechanism makes a broad exemption **explicit and
  individually justified — not impossible.** Be precise about that, because an
  earlier revision of this bullet claimed the stronger thing and was wrong: an entry
  must name a path *and* each check it waives *and* a reason for each, but there are
  only four checks (issue #112 added `ipv6` as the fourth), so naming all four is a
  whole-file exemption and the validator accepts it. What changed is that reopening
  the hole now takes four enumerated, individually-argued lines a reviewer will see,
  instead of one bare path that reads like a naming nit while switching off every
  detector. The enforcement is the
  reviewer, not the data structure —
  `test_naming_every_check_is_a_whole_file_exemption` pins this limitation in
  executable form so the claim and the code cannot drift apart again. If you are
  about to add an entry here, the first question is whether the *content* should be
  fixed instead; if you add one anyway, this bullet is where it gets disclosed,
  because a file the gate does not look at belongs in the section about what the
  gate does not cover.
- **What the scanner would miss if it broke**: nothing detects a detector that has
  stopped detecting. Running the scanner against a clean tree proves the absence of
  findings, never the presence of detection — the same asymmetry that let the
  frontend air-gap guard fail open three times. That is why `sanitize` runs
  `test_scan_repo_specific.py` (145 tests covering all four detectors, their
  case handling, the IPv4/FQDN dash- and underscore-adjacency narrowing from
  issue #111, the padding-width independence from #119, the bound on the IPv6
  swallowed-port retry from #131, both allowlist paths,
  the exit codes, a standing false-positive corpus, and every claim of
  impossibility left in the scanner's own comments) before it trusts the scan.
  Delimiter handling is the one part not left to per-detector test-writing:
  every address detector goes through a single shared matrix of 14 trailing and
  10 leading delimiters, over several fixture variants each, and a detector can
  only sit outside it by being named in an exempt list with a written reason —
  a test compares both against the scanner's own `CHECK_NAMES`, and a second
  test derives from each detector's regex and validator how many variants it
  owes. Those two guards exist because the IPv6 detector joined the suite with
  no delimiter case at all, and then joined it with one fixture that hid the
  next bug — each cost a full review round (above). It is also why the
  frontend guard now runs as its own explicit workflow step rather than relying on
  `package.json`'s `build` script continuing to chain it.
- **A passing detector suite is not a working detector, either** — one level further
  down, and this repo has now been bitten by it. The suite's first 34 assertions all
  passed while both address detectors were defeated by a trailing full stop and the
  FQDN detector ignored every uppercase lab TLD; a reviewer got a lab FQDN *and* a
  lab IP past the hard gate in one appended sentence, exit 0 (PR #83 round 2). The
  assertions were not wrong, they were **uniform**: every fixture was lowercase and
  put a word after the address, so the suite proved the detectors fired on that one
  shape. When you add a fixture here, the question is not "does this case pass" but
  "what property do all my fixtures share that I am therefore not testing" — casing,
  surrounding delimiters, position on the line, and how many of the enumerated
  patterns (all eight lab TLDs, not four) are actually exercised.

  It then happened a second time, in a variant worth naming separately because
  the fixture-monoculture question above would not have caught it: the IPv6
  detector (#112) arrived with the delimiter bug already fixed for the other two
  detectors, in the same file, directly under the comment explaining it — because
  the matrix that enumerates delimiters was per-detector *by omission*, so a new
  detector opted out by simply not appearing (PR #115 round 1). So there is a
  second question for a new detector: not only "what do my fixtures share", but
  "which existing matrices should this detector be in, and what makes it
  impossible to leave it out". Shared coverage that a new case joins by being
  remembered is coverage that eventually is not.

  **And a third time, one level further in, which is the version worth
  remembering.** The matrix was fixed so no detector could opt out — and every
  detector in it still had exactly one fixture. A green "14/14 on colon/port"
  therefore proved that those delimiters are safe *for one shape of one
  address*, while reading as if it proved more. It did not: IPv6 passed that
  row only because its single fixture was compressed, so the port absorbed as
  a legal eighth group; the fully-expanded spelling lived in the same file,
  never met a port or the matrix, and scanned clean (PR #115 round 2). The
  third question, then, is **"what does my one fixture per case have that the
  real inputs will not"** — and the durable answer is not to write more
  fixtures but to make the count derivable: the suite now reads each
  detector's own regex for the shapes it admits, and its own validator for
  the spellings it accepts, and fails when a variant for one of them is
  missing. A number a person has to remember to raise is a number that lags
  the code, and reads as covered while it does.

Treat every green run the way this document already asks you to treat a passing
local test: as one input a contextless reviewer weighs, never as the verdict itself.

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

## A green local build is not evidence that CI is green

The sandbox and CI run **different .NET SDK patch versions** — 8.0.129 locally at the
time of writing, 8.0.423 in CI. `Directory.Build.props` sets `AnalysisLevel=latest`,
and "latest" resolves to whatever the *running* SDK ships, so a newer CI SDK enables
analyzer rules the local one has never heard of.

This is not hypothetical: a PR passed `dotnet build -warnaserror` locally and went red
in CI on `CA1873` at a `[LoggerMessage]` call site. Nothing was wrong with the local
run; it simply could not see the rule.

So:

- **Read CI's per-step conclusions from the job log**, not just the overall check
  colour, and not just the first failure. When a new rule fires, it usually fires in
  more than one place — audit every call site of the pattern CI flagged rather than
  fixing only the line it happened to reach first.
- **Post the PR body after CI has run**, not before. Otherwise the red check is
  public and the body describes a state that never existed.
- Do not "fix" this by pinning `AnalysisLevel` down or adding a `global.json` to make
  the sandbox match — chasing the local SDK would silence rules the shipped build is
  actually held to. The asymmetry is fine; assuming it away is not.

The general form of this trap is worth remembering beyond .NET: **any tool whose
behaviour depends on its own version can disagree between here and CI.** A local pass
is evidence about the local toolchain.

## Per-component test suites

The stack-level contract is above. Each component owns its own suite and documents it
in its own README as it lands:

- **`backend/`** — `dotnet build` / `dotnet test`, plus the image's self-answering
  health probe. See `backend/README.md`.
- **`frontend/`** — `npm run build` (which **must** fail on any external asset, per
  [ADR-0007](adr/0007-frontend.md)) and `npm test`. See `frontend/README.md`.
- **`deploy/`** — bring-up and the SSE `proxy_buffering off` requirement from
  [ADR-0003](adr/0003-reverse-proxy-nginx.md). See `deploy/README.md`.

## Fresh-stack M1/M2 parity matrix (issue #444, epic #433)

Epic #433 replaced the M1/M2 backend's in-process dispatch with the runner topology
(ADRs 0013/0014/0015): `Waypoint.Api` enqueues and answers queries only,
`compliance-runner`/`download-runner` claim, execute, and own leases/events. Issue
#444 is the closing proof that every M1/M2-delivered workflow still works end to end
on that topology, and that a missing runner or a missing operator-gated tool fails
**honestly** (a clear error, not a hang or a silent no-op) rather than passing
undetected. This section maps each acceptance-matrix item to where it is proven —
an automated test, a deploy smoke-test step, or a disclosed gap — so a reviewer (or a
future change) can tell what still covers what without re-deriving it.

| Matrix item | Covered by |
| --- | --- |
| Cross-process queue behavior (API enqueues, runner-scoped registry claims) | `backend/Waypoint.Tests/Infrastructure/Postgres/CrossProcessRunnerParityMatrixTests.cs` (`ApiEnqueues_IndependentRunnerRepositoryClaims_AdvancesToTerminal`) |
| API restart before claim (run-secret survival) | `CrossProcessRunnerParityMatrixTests.ApiRestartsBeforeClaim_RunSecretStillDecryptsForTheRunnerSideClaim` (extends the store/decrypt round trip `RunSecretStoreTests.cs`/#450 already proved, across an actual ASP.NET host teardown/rebuild) |
| Runner restart mid-run recovery | `CrossProcessRunnerParityMatrixTests.RunnerRestartsMidRun_ExpiredLeaseRecovered_AndClaimedByTheReplacementWorker` (composes `JobLeaseRecoveryTests`' expiry primitive across two distinct worker identities) |
| Duplicate-replica claim safety | `CrossProcessRunnerParityMatrixTests.TwoReplicasOfTheSameRunner_RaceAFannedOutRun_NoDoubleClaimNoOrphan` (also `JobQueueRepositoryAllowlistClaimTests.IdenticalReplicas_...` at higher concurrency) — plus live: `deploy/scripts/fresh-stack-smoke-test.sh` step 12 (`--scale compliance-runner=2`) |
| One-domain-unavailable isolation | `CrossProcessRunnerParityMatrixTests.OneExecutionDomainUnavailable_TheOtherDomainsQueueIsUnaffected` — plus live: smoke-test step 11 (`docker compose stop compliance-runner`, confirm `download-runner` still available in `/system`) |
| All services healthy on a fresh `--build` | smoke-test step 1 |
| Login | smoke-test step 2 |
| `GET /system` reports both runner domains ready with capabilities | smoke-test step 3; `SystemApiTests.cs`/`WorkerRegistryRepositoryTests.cs` at the unit/integration level; `WorkerRegistryRunnerRoleGrantTests.cs` proves the actual least-privilege runner roles (not the test fixture's owner role) can execute the real upsert — see "A real bug this matrix caught" below. **Operator-visible (Runners indicator, issue #465):** `frontend/e2e/01-login-and-system.spec.ts` (issue #468) |
| Catalog-index works without the gated tool | smoke-test step 4; `CatalogIndexJobHandlerEndToEndTests.cs` (never wrapped by `ToolGatedDownloadJobHandler` — indexing is a pure filesystem walk). **Operator-visible:** `frontend/e2e/04-catalog-download.spec.ts` loads the Download Catalog screen live (issue #468) |
| Download fails with the clear tool-absent message | smoke-test step 5; `ToolGatedDownloadJobHandler`'s own unit coverage in `backend/Waypoint.Tests/`. **Operator-visible:** `frontend/e2e/04-catalog-download.spec.ts` queues a download and asserts the tool-absent message renders in the UI when an artifact is indexed (issue #468) |
| Locally-installed test tool success path | **Gap, disclosed, not closed** — see below |
| Site/target/credential setup via API | smoke-test step 6; `SitesTargetsApiTests.cs`, `CredentialsApiTests.cs`. **Operator-visible (Configuration screen CRUD):** `frontend/e2e/02-configuration-crud.spec.ts` (issue #468) |
| Service-credential scan: enqueue → compliance-runner claim → honest failure against an invented unreachable target | smoke-test step 7; `ScanJobHandlerEndToEndTests.cs`. **Operator-visible (Start-a-Scan wizard, service-credential path):** `frontend/e2e/03-scan-liverun-results.spec.ts` (issue #468) |
| Personal-credential scan: same, ad hoc credential | smoke-test step 8; `RunSecretScanRunTests.cs` |
| Cancellation mid-run | smoke-test step 9; `AuthFailureHaltTests.cs`/job-cancel unit coverage. **Operator-visible (Abort run control):** `frontend/e2e/03-scan-liverun-results.spec.ts` proves the Operator+-gated control submits (issue #468) — the terminal-state assertion is deferred to the Live Run gap below |
| SSE stream reconnect/replay | smoke-test step 10 (route-level: answers `text/event-stream` live); `proxy_buffering off` streaming proof is `deploy/README.md` "Verifying SSE streaming"; `JobEventStreamServiceTests.cs`/`EventStreamEndpointTests.cs` at the integration level. Reconnect/replay **from a browser `EventSource`** is still not exercised — `LiveRunScreen`'s SSE consumption is unreachable through the UI today, see the "Live Run screen" gap below |
| Secret canary scan (no plaintext in DB/artifacts/logs) | smoke-test step 13; `RunSecretScanRunTests.CreateScanRun_WithRunSecret_NeverPersistedInsecurely_CanaryProof` |
| Discovery inventory caching | `AutoDiscoverOnScanInitiationTests.cs`, `DiscoverJobHandlerEndToEndTests.cs` |
| Stage resume / retry | `JobStageDispatcherTests.cs`, `ScanJobHandlerEndToEndTests.cs` (`StageComplete` outcome) |
| Artifact/result serving | `RunArtifactsApiTests.cs` |

### A real bug this matrix caught (issue #444, fixed by migration 0028)

Running the smoke script against a genuinely fresh Compose stack (not a cached
build, not a reused `pgdata` volume) found that `compliance-runner`/`download-runner`
never actually appeared in `GET /system`'s `runners` list, and every `scan` job
claimed by `compliance-runner` sat at `queued` forever with `attempt_count: 0`.
Migration `0027_worker_registry_runner_grants.sql` (issue #443) granted the two
runner roles `INSERT, UPDATE` on `worker_registry` and reasoned "SELECT is not
needed... neither runner role ever SELECTs this table" — true of the application's
own SQL text, false about what Postgres requires to execute it:
`WorkerRegistryRepository.HeartbeatAsync`'s `INSERT ... ON CONFLICT (worker_id) DO
UPDATE ...` needs SELECT on the target table to evaluate the conflict and the
`EXCLUDED`-row reference, even with no explicit `SELECT` anywhere in the C# or
embedded SQL. Both prior test suites (`WorkerRegistryRepositoryTests.cs`,
`SchemaMigrationTests.cs`) ran against the full-privilege test-fixture role, never
the actual least-privilege `waypoint_compliance_runner`/`waypoint_download_runner`
roles the shipped migrations grant to — so this shipped, twice, undetected, until a
live fresh-stack run hit `permission denied for table worker_registry` against a
fully-migrated database.

Fixed by `0028_worker_registry_upsert_needs_select.sql` (grants the missing
`SELECT`) and closed for regression by
`backend/Waypoint.Tests/Infrastructure/Postgres/WorkerRegistryRunnerRoleGrantTests.cs`,
which runs the real upsert through a connection authenticated as each runner role
(not the fixture's owner role) — verified to fail red on `main` before 0028 and pass
green after it.

### A real operator-visibility gap this matrix found (fixed by issue #467)

`ResourceAdmissionController` (issue #437) silently released every claimed `scan`
job back to `queued` in this sandbox: `CgroupResourceDiscovery` found no usable
cgroup limits (a nested-Docker/devcontainer property, not a product defect) and fell
back to a conservative 1 CPU core, below `scan`'s own 2.0-core
`JobResourceProfiles` weight — so `scan` could never be admitted, and the denial was
logged only at Debug, invisible at the default `Information` level. An operator on
an under-resourced host would see scans queue forever with no visible diagnostic
pointing at resource admission as the cause. The smoke script's
`RunnerResources__FallbackCpuCores`/`FallbackMemoryBytes` environment-override
workaround (documented inline in the script) is unchanged, but the underlying gap
is now closed: `ResourceAdmissionController` logs a rate-limited Warning per job
type on first denial (once per `DenialWarningInterval` thereafter, not per poll),
distinguishing "will never fit" (the job type's profile alone exceeds the total
effective budget — a permanent misconfiguration) from "doesn't fit right now"
(transient contention with currently-admitted jobs). Starved job types also flow
into `RunnerCapacityReport` (both runner hosts' file-based readiness report),
`worker_registry.starved_job_types` (migration 0029), and `GET /system`'s
`runners[].starved_job_types`, so an operator can see *which* job types are stuck
and *why* without reading a runner container's logs.

### Disclosed gaps

- **Locally-installed test tool success path.** `deploy/README.md` "Compliance
  content and managed-tool state" documents the `managed-tool` named volume
  `download-runner` mounts read-write at `/var/lib/waypoint/managed-tool`
  (`ManagedToolOptions.ToolStatePath`) as the interface an operator's install flow
  populates. Exercising the *success* path with an invented stub executable placed
  in that volume was judged not worth the risk of the stub being mistaken for
  real tool-shape guidance in a public repo, and was not automated here — the
  absence path (`ToolGatedDownloadJobHandler`'s clear failure) is proven live and
  in unit tests, but no test asserts a populated volume flips a `download` job to
  success. A follow-up issue can add a throwaway stub (clearly marked invented,
  never resembling `vcf-download-tool`'s real invocation contract) to
  `deploy/scripts/fresh-stack-smoke-test.sh` if this gap needs closing.
- **Frontend/Playwright live-stack checks — closed by issue #468.** A Node 22
  toolchain (`nvm install 22`, matching `frontend/package.json`'s
  `"node": ">=22.19.0"` floor) plus `frontend/e2e/` (Playwright, a
  devDependency — browser binaries are never committed, installed locally via
  `npx playwright install chromium`) now exercise the operator-visible surface
  this gap used to describe: login, the Runners indicator, Sites/Targets/
  Credentials CRUD, the Start-a-Scan wizard's service-credential path through
  submission, the Abort run control, the Results screen rendering a completed
  run, and the Download Catalog screen's empty/tool-absent states.
  `deploy/scripts/e2e-playwright.sh` brings up an isolated fresh stack (same
  recipe as `fresh-stack-smoke-test.sh`: unique `-p`/port, self-provisioned
  admin password + master key, devcontainer bind-mount translation,
  compliance-profiles pre-seeding, cgroup-fallback override), seeds a site/
  target/credential via the API, runs the suite, and tears down fully — see
  "Running the Playwright suite" below.

  **Two real, previously-undetected bugs were found live and fixed as part of
  closing this gap** (both invisible to `npm test`'s mocked-fetch unit tests,
  which only ever exercised the shape each file's author assumed, never the
  shape the real backend actually sends):

  - `frontend/src/lib/router.tsx`'s hand-rolled router stored the FULL
    navigated path — including any query string — as its route-matching
    state, but `routes.ts`'s `ROUTES` table (and `routeForPath`) key on the
    bare pathname only. Any `navigate("/path?query=...")` call therefore
    matched no route, and `App.tsx`'s `route ?? DEFAULT_ROUTE` silently fell
    back to Dashboard. This broke `useScanWizard.ts`'s own
    `navigate('/live-run?run=' + id)` post-submit call — the entire
    Start-a-Scan → Live Run handoff redirected to Dashboard instead of
    landing on the run. Fixed: the router now strips the query string before
    storing route-matching state (still pushes the full URL to history).
  - `frontend/src/screens/catalog/catalog.ts`'s `fetchCatalogArtifacts`
    assumed `GET /catalog/artifacts` returns `{artifacts: [...],
    index_synced_at}`; the real `CatalogController.ListArtifacts` returns a
    bare `CatalogArtifactResponse[]` (`return Ok(items...)`, no envelope, no
    `index_synced_at`, no `search` query binding). `res.artifacts` was
    therefore always `undefined` against the real backend, crashing
    `DownloadCatalogScreen`'s `useMemo(() => artifacts.map(...))` on mount.
    Fixed: `fetchCatalogArtifacts` now adapts the real bare-array shape,
    filters `search` client-side (the backend has no such query parameter),
    and `index_synced_at` is typed but always `null` until the backend grows
    that concept.

  **One gap found live is disclosed, not fixed, and intentionally NOT closed
  by this issue** — see `frontend/e2e/03-scan-liverun-results.spec.ts`'s file
  header for the full detail: `LiveRunScreen`'s REST-seed data layer
  (`liverun.ts`'s `fetchRun`/`fetchRunJobs`, feeding `RunHeader`/`RunJob[]`)
  assumes a contract (`queues`, `pass`/`fail`/`na`, `site`, `credential_name`,
  per-job `queue`/`benchmark`/`note`/`job_id`) that the real
  `RunsController`'s `GET /runs/{id}` and `GET /runs/{id}/jobs` responses
  never carry — confirmed directly against a live stack; the real payloads
  are `{id, run_type, state, paused, blocked, scope, credential_id,
  initiated_by, ..., job_count*}` and `[{id, run_id, job_type, target_id,
  target_name, state, priority, attempt_count, ...}]`. The result is an
  unhandled `"e.queues is not iterable"` render crash on every real run —
  the Live Run screen cannot currently be exercised end to end through the
  UI at all. This is a `RunsController`/`liverun.ts` contract-alignment
  problem, not a missing test, and `backend/`/`Waypoint.Runner` were
  explicitly off-limits to the session that closed #468 (concurrent
  coordination with agents working `RunsController`/admission). The
  Playwright suite asserts the crash explicitly (fails fast with the real
  error, not a 60s timeout guessing at UI text) so this has a clear,
  reproducible, CI-visible signal — see issue tracker for the follow-up.
  SSE reconnect/replay from a browser `EventSource` specifically stays
  unproven until this is fixed, since it's the same screen.

  A second, smaller disclosed gap: `lib/system.tsx`'s `SystemProvider`
  fetches `GET /system` exactly once, in a `useEffect` keyed only on auth
  status — there is no polling interval. The Runners indicator (and mode/
  STIG Manager status) can therefore freeze at a stale reading (e.g.
  "Runners 1/1" when both runners are in fact up) for the rest of the
  session if that one fetch races ahead of a runner's first heartbeat;
  reloading the page is the only way to refresh it today. Confirmed directly:
  `worker_registry` gained both runner rows within seconds server-side while
  the already-rendered page's indicator stayed frozen for 150s straight.
  `frontend/e2e/01-login-and-system.spec.ts`'s Runners-indicator test works
  around this with an explicit grace period + page reload (what an operator
  would have to do today) rather than polling the live DOM, and documents the
  gap inline. Making `SystemProvider` poll live is a real product change
  (shared chrome state, broad blast radius) — a follow-up issue, not part of
  #468.

  CI wiring (running this suite in `.github/workflows/`) is a deliberate
  follow-up, not part of #468 — `.github/workflows/` is owner-gated.

### Running the Playwright suite

```bash
cd deploy
./scripts/e2e-playwright.sh <your-slug> <your-port>
```

Same isolation recipe as the smoke script (unique `-p`/port, full `down -v`
teardown on exit including on failure), plus: seeds one site/target/
(shared) credential via the API so the Start-a-Scan wizard has something to
select, joins its own process to the stack's `edge` network when the
published host port is unreachable from this namespace (identical
devcontainer/remote-daemon trap the smoke script's helper-container pattern
works around — see that script's header comment; this one has no
Playwright-side equivalent to "run every request in a container", so it
joins the network directly instead and disconnects in its own cleanup trap),
and finally runs `npm run test:e2e` from `frontend/` with `E2E_BASE_URL`/
`E2E_ADMIN_PASSWORD`/`E2E_SITE_NAME`/`E2E_CREDENTIAL_NAME` set. Requires
`frontend/node_modules/.bin/playwright` and a locally installed Chromium
(`cd frontend && npm ci && npx playwright install chromium` — browser
binaries are a devDependency-managed local install, never committed and
never fetched at appliance runtime; `npm run build`'s external-asset guard
never scans `frontend/e2e/`).

If the shipped `edge` network's pinned subnet collides with a concurrent
stack on a shared host, `WAYPOINT_E2E_SUBNET` (e.g.
`WAYPOINT_E2E_SUBNET="203.0.113.0/24"`) overrides both the `edge` subnet
and the backend's `ForwardedHeaders__KnownNetworks__0` together, same
requirement as the smoke script's `WAYPOINT_SMOKE_OVERRIDE_FILE`.
`WAYPOINT_E2E_OVERRIDE_FILE` carries the same devcontainer bind-mount
translation override the smoke script generates automatically, and can hold
more than one path (space-separated) if both a devcontainer override and a
subnet override are needed at once.

### Running the smoke script

```bash
cd deploy
./scripts/fresh-stack-smoke-test.sh <your-slug> <your-port>
```

Follows the isolation recipe above automatically (unique `-p` derived from the slug,
unique `WAYPOINT_HTTPS_PORT`) and tears its own project down (`down -v`) on exit,
including on failure. It also self-provisions what `deploy/README.md`'s manual
"Bring-up" steps 3/4 otherwise require by hand (a dev admin password + hash, a
generated secrets master key, mounted into all three trusted services) so a fresh
run needs no pre-existing `../config/` state, and auto-detects the devcontainer
bind-mount host-path trap described earlier in this document (translating
`frontend/dist`, `nginx/conf.d`, `nginx/certs`, `postgres/initdb`, and the
generated master-key/admin-hash sources to their host-absolute paths when it
detects it is itself running inside a mounted container).

If the shipped `edge` network's pinned `192.168.240.0/24`
collides with a concurrent stack on a shared host, pass an uncommitted override file
via `WAYPOINT_SMOKE_OVERRIDE_FILE` (a scratch compose file overriding both the `edge`
subnet and the backend's `ForwardedHeaders__KnownNetworks__0` to match — the two must
move together, since the backend only trusts forwarded headers from that exact
subnet). Never commit an override file; the shipped subnet is correct on a real
appliance host with no concurrent stacks. The same variable can carry the
devcontainer path-translation override too (the script generates one automatically
when unset and needed); passing your own file skips the auto-detection.
