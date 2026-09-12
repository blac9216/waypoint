# ADR-0007: React + TypeScript PWA with zero external assets

Status: Accepted
Date: 2026-08-02

## Context

The UI is the product's entire value-add over the existing CLIs. It must run in
air-gapped networks and serve an "operations console" workload: dense tables, live
streaming run views, code-editor panes for attestation/input YAML.

## Decision Drivers

_Backfilled under ADR-0027 from #11._ The sources record no issue or PR that captures
the original evaluation session — this ADR was authored in the repo's initial skeleton
commit ("Add architecture docs, ADRs, Claude workflow skills, and repo skeleton"),
which predates any tracked issue or PR. #11 is the earliest tracked issue that names
ADR-0007, and confirms the drivers below by scaffolding the frontend directly against
them. The bullets themselves are drawn from this ADR's own original Context and
Rationale text, not invented:

- The UI must run fully air-gapped: no CDN assets, no external font/icon/package
  fetches at runtime (Context, above).
- The workload is a dense "operations console": tables, live streaming run views, and
  code-editor panes for attestation/input YAML (Context, above).
- TypeScript is needed on the frontend regardless of what backend language is chosen.
- Live updates arrive via SSE subscriptions to the job engine (ADR-0008).
- The bundle must stay static so nginx (ADR-0003) remains the only thing serving the
  UI — no SSR runtime to harden.

## Considered Options

_Backfilled under ADR-0027 from #11._ As with Decision Drivers above, no issue or PR
beyond this ADR's own original Rationale text records the comparison, and the sources
do not name the frontend-framework alternatives considered against React — only that
React was chosen for having "the deepest ecosystem for data-dense consoles and
code-editor components." The bullets below are that original Rationale text, moved
verbatim, plus the SSR alternative implicit in the "static bundle" Decision:

- **React + TypeScript, static build (chosen).** TypeScript is unavoidable for the
  frontend regardless of backend choice; React has the deepest ecosystem for
  data-dense consoles and code-editor components. A static bundle keeps nginx the
  only thing serving the UI — no SSR runtime to harden.
- **A server-rendered (SSR) frontend** — rejected implicitly by the "fully static
  bundle" Decision below; the sources do not record a named SSR framework being
  evaluated, only that SSR would add a runtime to harden that a static bundle avoids.

## Decision

- **React + TypeScript**, built with Vite to a fully static bundle served by nginx.
- **PWA** (installable, offline shell) — a natural fit for air-gapped operators.
- **Zero external assets at build or runtime**: fonts, icons, and all packages vendored;
  no CDN references anywhere in the bundle. CI should fail on any external URL in the
  build output.
- Live updates via **SSE** subscriptions to the job engine (ADR-0008).
- Visual language: enterprise ops console, dark theme primary + light theme, restrained
  status colors, monospace only for logs/IDs. The detailed screen inventory lives in
  [`../explanation/ui-design-brief.md`](../explanation/ui-design-brief.md).

## Rationale

- TypeScript is unavoidable for the frontend regardless of backend choice; React has
  the deepest ecosystem for data-dense consoles and code-editor components.
- Static bundle keeps nginx the only thing serving the UI — no SSR runtime to harden.

## Consequences

- Component/design decisions (component library, editor component, state management)
  are deferred to the design phase output — recorded later as a follow-up ADR.
- Air-gap asset policy needs an automated check, not a convention.
