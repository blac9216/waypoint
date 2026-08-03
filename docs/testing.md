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

## What CI covers — and does not

GitHub Actions runs four workflows — [`sanitize.yml`](../.github/workflows/sanitize.yml),
[`backend.yml`](../.github/workflows/backend.yml),
[`frontend.yml`](../.github/workflows/frontend.yml),
[`deploy.yml`](../.github/workflows/deploy.yml) — added in issue
[#79](https://github.com/blac9216/waypoint/issues/79):

| Workflow | Triggers on | What it runs |
| --- | --- | --- |
| `sanitize` | every PR + push, no path filter (hard gate) | the scanner's own test suite (`.github/sanitize/test_scan_repo_specific.py`), then a `gitleaks` full-history secret scan, then a repo-specific scanner (`.github/sanitize/scan_repo_specific.py`) for lab-style FQDNs, non-RFC-5737 IP addresses, and Broadcom/VMware depot-token shapes |
| `backend` | `backend/**` | `dotnet build -warnaserror`, `dotnet test` with coverage |
| `frontend` | `frontend/**` | `npm ci`, `npm run build`, the ADR-0007 air-gap asset guard **as its own explicit step**, `npm test`, `oxlint` |
| `deploy` | `deploy/**`, `scripts/**` | `docker compose config`, `nginx -t` against the shipped `conf.d` with a throwaway generated dev cert, `shellcheck` |

Every job is path-filtered except `sanitize`, which is a hard gate on everything —
a docs-only change still gets scanned, because a leaked hostname or token is just as
real in a markdown file as in code. Every job sets its own `concurrency` group with
`cancel-in-progress`, so a superseded push doesn't keep burning runner time. No
workflow references a repository secret; PR triggers are plain `pull_request`, never
`pull_request_target`; every third-party action is pinned by full commit SHA.

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
  `version` — with or without a `:`/`=` separator, so `version 1.2.3.4`,
  `--version 1.2.3.4` and `app.version=1.2.3.4` are suppressed as well as
  `version: '1.2.3.4'`; a `version` mentioned anywhere else on the line is not). It is a
  mechanical backstop for the sanitization policy in `CLAUDE.md`, not a replacement
  for the diff-by-hand review that policy still requires — a sufficiently
  well-disguised secret or a lab identifier that doesn't match any of its patterns
  will not be flagged.
- **Files the scanner does not read**: **none, currently — and that is a property
  worth keeping.** `ALLOWLIST_FINDINGS` in `scan_repo_specific.py` is empty, so every
  git-tracked file that isn't a known-binary extension is scanned by all three
  detectors. This matters because the alternative degrades silently: an exempt path
  reports clean while never having been looked at, and nothing in a green check
  distinguishes the two. An earlier revision of this PR exempted one 204 KB UI mockup
  from *all three* detectors in order to waive a single hostname-naming nit, which
  left the IP and depot-token detectors dark on the most likely file in the repo for
  someone to paste real lab inventory into while demoing. That file was re-sanitized
  instead ([#86](https://github.com/blac9216/waypoint/issues/86)) and the exemption
  deleted. The replacement mechanism makes a broad exemption **explicit and
  individually justified — not impossible.** Be precise about that, because an
  earlier revision of this bullet claimed the stronger thing and was wrong: an entry
  must name a path *and* each check it waives *and* a reason for each, but there are
  only three checks, so naming all three is a whole-file exemption and the validator
  accepts it. What changed is that reopening the hole now takes three enumerated,
  individually-argued lines a reviewer will see, instead of one bare path that reads
  like a naming nit while switching off three detectors. The enforcement is the
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
  `test_scan_repo_specific.py` (52 assertions covering all three detectors, their
  delimiter and case handling, both allowlist paths, the exit codes, and the
  documented false-positive exemptions) before it trusts the scan, and why the
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

## Per-component test suites

The stack-level contract is above. Each component owns its own suite and documents it
in its own README as it lands:

- **`backend/`** — `dotnet build` / `dotnet test`, plus the image's self-answering
  health probe. See `backend/README.md`.
- **`frontend/`** — `npm run build` (which **must** fail on any external asset, per
  [ADR-0007](adr/0007-frontend.md)) and `npm test`. See `frontend/README.md`.
- **`deploy/`** — bring-up and the SSE `proxy_buffering off` requirement from
  [ADR-0003](adr/0003-reverse-proxy-nginx.md). See `deploy/README.md`.
