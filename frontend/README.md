# frontend

React + TypeScript PWA, static Vite build, zero external assets at build or
runtime. See [ADR-0007](../docs/adr/0007-frontend.md), the
[design brief](../docs/ui/design-brief.md), and the
[prototype handoff](../docs/ui/prototype/README.md) (design tokens, global
chrome spec, layout rules) — this app implements that spec against React
primitives, it does not port the prototype's markup.

## Prerequisites

Node.js **>=22.19.0** (pinned in `.nvmrc`; matches CI's `frontend.yml`, which
installs Node 22). This is a hard floor, not a suggestion: `jsdom`, `undici`,
and `whatwg-url` — transitive deps of the vitest/jsdom test stack — declare
their own `engines.node` requirements in that range, and `.npmrc` in this
directory sets `engine-strict=true`, so `npm install`/`npm ci` **fails**
immediately on an older Node instead of installing and letting the failure
surface later as a confusing `vitest` worker crash
(`webidl.util.markAsUncloneable is not a function`, from `undici`).

If your shell's default `node` is older than 22 (a common sandbox/devcontainer
default is 20.x), and you manage Node versions with
[nvm](https://github.com/nvm-sh/nvm):

```bash
nvm install    # reads .nvmrc, installs/uses Node 22 if not already present
nvm use
```

Or, if a Node 22 install already exists under nvm but isn't your shell
default:

```bash
export PATH="$HOME/.nvm/versions/node/v22.<x>.<y>/bin:$PATH"
```

## Commands

```bash
npm install
npm run dev              # http://localhost:5173 — dev server + local auth/events mock
npm run build             # typecheck -> vite build -> external-asset check (fails the build on either)
npm run check:external-assets  # re-run just the guard against an existing dist/
npm run preview           # serve the production build locally
npm run test               # vitest (unit + component)
npm run test:coverage      # vitest with v8 coverage
npm run lint                # oxlint
```

## Structure

```
src/
├── lib/            # framework-free-ish core: api client, SSE client, auth,
│                    theme, role model, router — no JSX except auth/theme/
│                    system/router's Provider components
├── components/
│   ├── chrome/      # top bar, left rail, job log drawer, icons — the global
│   │                 chrome this issue implements
│   └── auth/        # local-auth login screen
├── screens/         # one placeholder per nav destination (see below)
└── styles/          # tokens.css (design tokens, lifted verbatim from the
                       prototype handoff) + global.css (reset, scrollbar, blip)
scripts/
├── check-no-external-assets.mjs        # the air-gap guard (see below)
├── compare-guard-matrix.mjs            # guard negative-control matrix vs. another revision
├── verify-mode-guard-browser.mjs       # manual Chromium harness for the route guard
└── gen-icons.py                          # regenerates public/icons/*.png
vite-plugins/
└── mock-backend.ts   # dev-server-only local-auth/system/events mock (see below)
```

## Screens are placeholders

This issue scopes the **global chrome** (top bar, left rail, job log drawer)
and the login flow — not the nine screens themselves. Each screen in
`src/screens/` is a `PlaceholderScreen` naming the `docs/api-contract.md`
endpoints it will eventually read, so routing, the role/mode guard, and the
top-bar screen-title binding all have somewhere real to land today. Building
out an individual screen (Dashboard, Live Run, the Download Catalog, …) is
future work per the roadmap.

## No real backend in dev

Issue #3 (the ASP.NET Core backend) has landed in `backend/`, and the compose
stack's `backend` service (`deploy/docker-compose.yml`) builds it directly —
the old `deploy/backend-stub` nginx placeholder it used to point at is no
longer wired up. `npm run dev` runs the Vite dev server standalone, with no
proxy to that or any backend container, so it still uses a dev-server-only
mock (`vite-plugins/mock-backend.ts`, `apply: 'serve'` — it contributes
nothing to `vite build`) implementing:

- `POST /api/v1/auth/login`, `GET /api/v1/auth/me` — dev credentials
  `admin` / `waypoint-dev` (fictional, not a real secret); response shapes
  match the real backend's `Contracts/AuthContracts.cs` (see
  `docs/api-contract.md`'s Auth section)
- `GET /api/v1/system`, `GET /api/v1/stigman` — static mode/version/STIG
  Manager info
- `GET /api/v1/events` — a synthetic SSE stream (job.state/job.log/
  run.progress/system.notice) for exercising the job log drawer live

`src/lib/auth.tsx` consumes the confirmed contract: `POST /api/v1/auth/login`
returns `{token, role, expires_at}` (no `user` object), and identity comes
from `GET /api/v1/auth/me`. See `docs/api-contract.md`'s Auth section for the
full shape, including the closed set of PascalCase `role` values.

## The external-asset guard

`scripts/check-no-external-assets.mjs` scans `dist/` for any `http(s)://`
URL and fails the build if it finds one that isn't in its narrow, justified
allowlist (three inert strings baked into React/workbox-window — read the
script's header comment). `npm run build` runs it automatically as the last
step.

**File selection is an ALLOWLIST of what is scannable, not a denylist of what
is opaque.** This is the important sentence about this script. A file passes
only if it is one of two things:

- **scannable** — it decodes as valid UTF-8 and contains no NUL bytes. Those
  are scanned for external references.
- **a recognised binary asset** — its **magic bytes** match
  `KNOWN_BINARY_SIGNATURES`, a short, explicitly justified allowlist of the
  types a Vite `dist/` legitimately contains (PNG, JPEG, GIF, WebP, AVIF, ICO,
  WOFF, WOFF2, OTF, TTF, TTC, WASM). Verified by content, never by filename.
  Those are skipped from the opacity check — but their bytes are still scanned
  for a *cleartext* URL, because a skip is a licence not to fail for being
  unreadable, not a licence to ignore what is there.

**Everything else fails the build.** Unrecognised, undecodable, compressed,
archived, UTF-16, truncated or ambiguous — all of it. There is no fallthrough
bucket, so there is nothing for a novel format to fall through into.

That inversion is the lesson of **five** fail-opens, and every earlier fix
corrected the *list* or the *table*: an extension allowlist that skipped
`.mjs`/extensionless files (#65); compressed artifacts parked on the
known-binary skip list (#77); formats nobody had added to the `.br`/`.gz`/
`.zip` denylist such as `.zst`/`.xz`/`.7z`/`.lz4` (#81); a fix for #81 that
*replaced* the list with magic-byte sniffing instead of adding to it, taking
`.gz`/`.zip` off the fail-closed list (PR #88 round 1); and then, with a
magic-bytes-OR-extension union in place, `.Z`, lzop, lzip, RAR, zstd skippable
frames and a `.tar` of compressed members walking past both halves (PR #88
round 2).

Five is not five unlucky omissions. **The set of opaque formats is unbounded;
enumerating it can never converge.** So the question changed from "did we
remember this compression format?" (infinite, and we kept losing) to "is this a
text format we can actually scan, or a binary asset we explicitly recognise?"
(finite, and closed). Under the new model `.Z`, lzop, RAR, `.tar`, a file named
exactly `.gz`, and every format invented after this paragraph fail by default,
without anyone having heard of them.

**The escape hatch is a justified allowlist entry.** If a build genuinely
starts emitting a new binary asset type, add its magic bytes and a written
reason to `KNOWN_BINARY_SIGNATURES` — a deliberate, reviewable line in a diff,
which is what an air-gap exemption should be. Two formats are deliberately
absent and fail today: **BMP** (its whole signature is the two ASCII bytes
`BM`, too weak to license a skip) and **EOT** (no signature at all).

**Compressed-format detection still exists, as diagnostics only.**
`detectCompressedFormat()` recognises gzip, zstd, xz, zip, 7z, lz4, bzip2,
`.Z`, lzop, lzip, RAR, zstd skippable frames and tar so a failure message can
say *"this looks like gzip"* instead of a bare "unscannable". It has no vote in
the verdict: an unrecognised opaque file fails exactly the same way. The
`COMPRESSED_EXTENSIONS` name list survives as a narrow **one-way veto** on the
scan branch — a file *named* `.gz`/`.br`/… is never scanned even if its bytes
decode cleanly — which can only ever add a failure, and covers the one residual
the text test cannot: a compressed stream whose bytes coincidentally form valid
UTF-8. It matches on the basename suffix rather than `extname()`, because
`extname(".gz")` is the empty string and a file named exactly `.gz` would
otherwise be invisible to it.

**Why "valid UTF-8, no NUL bytes" is the right criterion.** Every format this
guard has lost to fails it for reasons intrinsic to the format rather than to
anyone's memory: gzip, zstd, xz, 7z, `.Z` and lzop all put an illegal UTF-8
byte in their first two bytes; zip, RAR, tar and every length-prefixed
container NUL-pad their headers; and brotli, raw DEFLATE and zlib — the three
headerless `vite-plugin-compression2` algorithms that forced the old model to
keep an extension list at all — produce high-entropy output that is not valid
UTF-8 either. It also closes a hole no list ever addressed: UTF-16LE text
carrying a CDN import, which the old model "scanned", found nothing in, and
reported OK.

**The residual, stated honestly.** Two things this model cannot see, and they
are now the *only* two rather than an open-ended list:

1. A file that decodes as clean UTF-8 and smuggles a reference past
   `URL_PATTERN`, which matches only a contiguous literal scheme — string
   concatenation, JS escapes, HTML entities, base64, a JSON-escaped
   `https:\/\/`, or an uppercase `HTTPS://`. That is a property of the
   *pattern*, not of file selection; tracked in **#110** (and **#105** for the
   case-sensitivity subset), with the current behaviour pinned by a
   "known residual" block in the test suite so it stays visible.
2. A URL inside an allowlisted binary that is not present in cleartext (e.g. a
   compressed PNG `zTXt` chunk). The cleartext byte-scan covers the
   uncompressed case; the compressed-metadata case is accepted, because an
   image cannot execute a fetch.

The vendor URL allowlist's three entries are all `$`-anchored to the exact
literal they justify, not prefix-matched — including the `bit.ly` shortlink,
where the path is the resource identity and a prefix match would allowlist an
arbitrary destination reachable through a different shortlink sharing the same
prefix.

`scripts/check-no-external-assets.test.mjs` proves the guard actually fails on
a deliberate violation — inside a `.mjs` chunk, an extensionless file, a `.map`
source map, a dotfile, an uppercase-extension file, and a bit.ly shortlink that
only shares the allowlisted prefix. Dedicated blocks cover the inverted model:
the round-2 escapes (`.Z`, lzop, lzip, RAR, a zstd skippable frame, a `.tar` of
compressed members, files named exactly `.gz` and `.br`), UTF-16LE text, random
high-entropy bytes, and the property that the diagnostic table **cannot change
a verdict**. Another block enforces that `scanDist` — the code path the CLI
runs — routes every file through the single `classifyDistFile` predicate and
re-derives nothing itself, structurally and by corpus-wide parity: round 1
shipped a regression precisely because the property test asserted about a helper
the CLI never called.

Every compressed fixture is a realistic (~40 KB) bundle-shaped file, not a
one-line one, and is asserted not to contain the URL in cleartext before it is
used: a one-line fixture is caught by accident because these compressors store
short literals raw, so the URL survives verbatim in the "compressed" output and
a toy test would pass even against a broken guard.

`scripts/compare-guard-matrix.mjs` runs a 46-case negative-control matrix
against **two** revisions of the guard at once (this one and, by default,
`origin/main`'s), and exits non-zero if the current guard fails to be a strict
superset of the baseline — every shape the old guard stopped must still be
stopped. It prints whether each fixture leaks the marker URL in cleartext, so a
"caught" verdict that is really the scan branch stumbling over plaintext is
visible rather than assumed. Two-revision comparison is the only way to see a
regression like PR #88 round 1's, since each revision looks self-consistent on
its own.

`scripts/verify-mode-guard-browser.mjs` is the manual counterpart for the
route guard (issues #78/#82): it drives a real Chromium against `vite dev`
with `/api/v1/system` delayed, hung, or answering each mode, and records
every screen mount with a latching `MutationObserver` installed before any
app script. It needs a browser and a transient `playwright-core`, so it is
not part of `npm test`; see its header comment for the invocation.
