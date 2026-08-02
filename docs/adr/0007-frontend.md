# ADR-0007: React + TypeScript PWA with zero external assets

Status: Accepted

## Context

The UI is the product's entire value-add over the existing CLIs. It must run in
air-gapped networks and serve an "operations console" workload: dense tables, live
streaming run views, code-editor panes for attestation/input YAML.

## Decision

- **React + TypeScript**, built with Vite to a fully static bundle served by nginx.
- **PWA** (installable, offline shell) — a natural fit for air-gapped operators.
- **Zero external assets at build or runtime**: fonts, icons, and all packages vendored;
  no CDN references anywhere in the bundle. CI should fail on any external URL in the
  build output.
- Live updates via **SSE** subscriptions to the job engine (ADR-0008).
- Visual language: enterprise ops console, dark theme primary + light theme, restrained
  status colors, monospace only for logs/IDs. The detailed screen inventory lives in
  [`../ui/design-brief.md`](../ui/design-brief.md).

## Rationale

- TypeScript is unavoidable for the frontend regardless of backend choice; React has
  the deepest ecosystem for data-dense consoles and code-editor components.
- Static bundle keeps nginx the only thing serving the UI — no SSR runtime to harden.

## Consequences

- Component/design decisions (component library, editor component, state management)
  are deferred to the design phase output — recorded later as a follow-up ADR.
- Air-gap asset policy needs an automated check, not a convention.
