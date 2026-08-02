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
- **If you collided anyway**, say so plainly in your report and re-run the affected
  verification under isolation. A contaminated result handed onward is worse than no
  result, because the next person treats it as evidence.

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
| `sanitize` | every PR + push, no path filter (hard gate) | `gitleaks` full-history secret scan, plus a repo-specific scanner (`.github/sanitize/scan_repo_specific.py`) for lab-style FQDNs, non-RFC-5737 IP addresses, and Broadcom/VMware depot-token shapes |
| `backend` | `backend/**` | `dotnet build -warnaserror`, `dotnet test` with coverage |
| `frontend` | `frontend/**` | `npm ci`, `npm run build` (runs the ADR-0007 air-gap asset guard), `npm test`, `oxlint` |
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
  `127.0.0.11`). It is a mechanical backstop for the sanitization policy in
  `CLAUDE.md`, not a replacement for the diff-by-hand review that policy still
  requires — a sufficiently well-disguised secret or a lab identifier that doesn't
  match any of its patterns will not be flagged.

Treat every green run the way this document already asks you to treat a passing
local test: as one input a contextless reviewer weighs, never as the verdict itself.

## Per-component test suites

The stack-level contract is above. Each component owns its own suite and documents it
in its own README as it lands:

- **`backend/`** — `dotnet build` / `dotnet test`, plus the image's self-answering
  health probe. See `backend/README.md`.
- **`frontend/`** — `npm run build` (which **must** fail on any external asset, per
  [ADR-0007](adr/0007-frontend.md)) and `npm test`. See `frontend/README.md`.
- **`deploy/`** — bring-up and the SSE `proxy_buffering off` requirement from
  [ADR-0003](adr/0003-reverse-proxy-nginx.md). See `deploy/README.md`.
