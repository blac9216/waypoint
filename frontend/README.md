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

## No real backend yet

Issue #3 (the ASP.NET Core backend) hasn't landed, and the compose stack's
`deploy/backend-stub` only answers `/api/v1/health` (everything else 501s).
`npm run dev` runs a dev-server-only mock (`vite-plugins/mock-backend.ts`,
`apply: 'serve'` — it contributes nothing to `vite build`) implementing:

- `POST /api/v1/auth/login` — dev credentials `admin` / `waypoint-dev`
  (fictional, not a real secret)
- `GET /api/v1/system`, `GET /api/v1/stigman` — static mode/version/STIG
  Manager info
- `GET /api/v1/events` — a synthetic SSE stream (job.state/job.log/
  run.progress/system.notice) for exercising the job log drawer live

`src/lib/auth.tsx`'s header comment flags the one real assumption this app
makes ahead of the contract: the exact shape of `POST /api/v1/auth/login`,
which `docs/api-contract.md` doesn't yet specify. Update that one file if the
real shape differs once issue #3 lands.

## The external-asset guard

`scripts/check-no-external-assets.mjs` scans `dist/` for any `http(s)://`
URL and fails the build if it finds one that isn't in its narrow, justified
allowlist (three inert strings baked into React/workbox-window — read the
script's header comment). `npm run build` runs it automatically as the last
step.

**File selection is a denylist, deliberately.** Every file under `dist/` is
scanned except a short list of known-binary extensions (images, fonts,
`.wasm`/`.zip`/`.gz`). It is not an allowlist of "text" extensions: that
shape silently exempts every file type nobody thought of — `.mjs`, `.cjs`,
`.xml`, and anything with no extension at all — and `public/` is copied
verbatim into `dist/` with whatever names it contains, so an allowlist means
a newly emitted file type ships unchecked until someone notices. A guard that
fails open is worse than no guard (ADR-0007 makes this a hard requirement, not
a style preference). If a binary format ever trips a false positive, add its
extension to `SKIPPED_BINARY_EXTENSIONS` with a reason.

`scripts/check-no-external-assets.test.mjs` proves the guard actually fails on
a deliberate violation — including one inside a `.mjs` chunk and one inside an
extensionless file, the two shapes the old allowlist let through.
