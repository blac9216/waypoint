# frontend

React + TypeScript PWA, static Vite build, zero external assets at build or
runtime. See [ADR-0007](../docs/adr/0007-frontend.md), the
[design brief](../docs/ui/design-brief.md), and the
[prototype handoff](../docs/ui/prototype/README.md) (design tokens, global
chrome spec, layout rules) — this app implements that spec against React
primitives, it does not port the prototype's markup.

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

**File selection is a denylist, deliberately.** Every file under `dist/` is
scanned except a short list of known-binary extensions (images, fonts,
`.wasm`). It is not an allowlist of "text" extensions: that shape silently
exempts every file type nobody thought of — `.mjs`, `.cjs`, `.xml`, and
anything with no extension at all — and `public/` is copied verbatim into
`dist/` with whatever names it contains, so an allowlist means a newly
emitted file type ships unchecked until someone notices. A guard that fails
open is worse than no guard (ADR-0007 makes this a hard requirement, not a
style preference). If a binary format ever trips a false positive, add its
extension to `SKIPPED_BINARY_EXTENSIONS` with a reason.

**Compressed artifacts fail the build outright — they are never treated as
inert binary.** Compressed formats encode compressed *text*; scanning the
compressed bytes as UTF-8 finds nothing (the guard would report a false "OK"
while inspecting gibberish), which is worse than not scanning at all because
it looks thorough. The current build emits none of these, so this costs
nothing today; if a precompression step is ever added, the guard fails
loudly instead of silently passing content it cannot inspect, forcing
whoever adds it to either teach the script to decompress-and-scan
(`node:zlib` has Brotli, gzip, and zstd built in) or accept the compressed
output failing the build.

**Detection fails closed by union: magic bytes OR extension, either one
alone.** A file is treated as compressed if `detectMagicByteFormat()` matches
its header **or** its extension is on `COMPRESSED_EXTENSIONS`. Magic-byte
detection may only ever *add* coverage; it can never take a file out of the
fail-closed bucket that the extension list would have caught.

That rule is the lesson of four failures, not caution for its own sake. The
first three were the extension-list model failing open: an allowlist that
skipped `.mjs`/extensionless files (#65), compressed formats treated as inert
binary (#77), and — even after #77 added a `.br`/`.gz`/`.zip` denylist — any
format nobody had added to *that* list yet, such as `.zst`/`.xz`/`.7z`/`.lz4`
(#81). The fourth was the fix for #81 *replacing* the list with content
sniffing instead of adding to it, which took `.gz` and `.zip` off the
fail-closed list and let three real shapes back through (PR #88 review).

Both halves are load-bearing. Magic bytes catch what no list can enumerate:
`detectMagicByteFormat()` sniffs gzip, zstd, xz, zip, 7z, lz4 and bzip2
headers, so a compressed payload is caught no matter what it is named. The
extension list catches what has no header to sniff — and **Brotli is not the
only headerless format**: raw DEFLATE (RFC 1951) has no header at all, and
zlib's (RFC 1950) two leading bytes are a checksum constraint rather than a
fixed magic. All three are declared `vite-plugin-compression2` algorithms
(`'gzip' | 'brotliCompress' | 'deflate' | 'deflateRaw' | 'zstandard'`), and
that plugin's default output filename is `[path][base].gz` for everything
except brotli and zstandard — so `algorithms: ['deflate']` emits a `.gz`
holding a zlib stream with no gzip magic in it, which only the extension
list can catch. Signature matching is also anchored at offset 0, so a stream
behind a stray leading byte, or a real zip that opens `PK\x05\x06` instead of
`PK\x03\x04`, likewise needs the name. Read the script's header comment
before changing any of this.

The vendor URL allowlist's three entries are all `$`-anchored to the exact
literal they justify, not prefix-matched — including the `bit.ly` shortlink,
where the path is the resource identity and a prefix match would allowlist
an arbitrary destination reachable through a different shortlink sharing the
same prefix.

`scripts/check-no-external-assets.test.mjs` proves the guard actually fails on
a deliberate violation — including one inside a `.mjs` chunk, one inside an
extensionless file, one inside a `.map` source map, one inside a dotfile, one
behind an uppercase extension, one behind a compressed artifact detected by
magic bytes under an extension nobody put on any list, one behind a `.br`
file caught only by its extension, and one behind a bit.ly shortlink that
only shares the allowlisted prefix — the shapes the old allowlist and the
issue #77/#81 holes let through. A dedicated block covers the union
property: a zlib stream and a raw-DEFLATE stream named `bundle.js.gz`, an
empty `PK\x05\x06` zip, and a gzip stream behind one leading byte all fail
the build, and neither detection signal can veto the other.

Every compressed fixture is a realistic (~40 KB) bundle-shaped file, not a
one-line one, and is asserted not to contain the URL in cleartext before it
is used: a one-line fixture is caught by accident because these compressors
store short literals raw, so the URL survives verbatim in the "compressed"
output and a toy test would pass even against a broken guard.
