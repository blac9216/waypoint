# ADR-0010: One appliance, connected/disconnected modes, bundle-based transfer

Status: Accepted
Amended-by: 0015
Date: 2026-08-02

## Context

The download manager inherently needs Broadcom depot access (connected side of the air
gap); the STIG runner works inside enclaves (disconnected side). The predecessor encodes
this split as a `Transfer/` staging directory. The planned estate: **one
internet-connected enclave using all features, plus disconnected enclaves consuming
its exports.**

## Decision Drivers

_Backfilled under ADR-0027 from this ADR's own Context and Rationale (present since
the bootstrap commit `2fef4614`, 2026-08-02T09:43:45Z); corroborated same-day by epic
#17, which restates the connected/disconnected split as this ADR's decision._

- The predecessor tools already split along the air gap: the download manager needs
  Broadcom depot access (connected side); the STIG runner has to operate inside
  enclaves with no such access (disconnected side).
- The real user base spans both sides — one internet-connected enclave plus
  disconnected enclaves consuming its exports — not a connected-only audience.
- A connected-only product would exclude most of that user base; two separate products
  would double maintenance for what is otherwise a feature-flag's worth of difference
  between them.

## Considered Options

_Backfilled under ADR-0027 from this ADR's own Rationale (present since the bootstrap
commit `2fef4614`, 2026-08-02T09:43:45Z); the sources record no issue or PR that
separately evaluated these alternatives — the comparison lives only in this ADR's own
text. Corroborated same-day by epic #17._

- **Connected-only product** — rejected. Shrinks the audience to a fraction of the
  real user base, most of whom operate disconnected enclaves.
- **Two separate products** (one per side of the air gap) — rejected. Doubles
  maintenance for a feature-flag's worth of difference between the two.
- **One appliance image with an instance-level connected/disconnected mode** (this
  decision) — chosen. Connected mode exposes STIG, downloads/catalog browser,
  content-library and Photon repo management, export bundle composition, and optional
  online update check; disconnected mode exposes STIG features plus import bundle
  handling, with download/depot features hidden or disabled. One codebase, one image,
  never a fork; tracked for implementation in epic #17.

## Decision

One appliance image, deployed per enclave, with an instance-level **mode**:

- **Connected**: all features — STIG, downloads/catalog browser, content-library and
  Photon repo management, **export bundle** composition, optional online update check.
- **Disconnected**: STIG features + **import bundle** (verify signature/checksums, show
  contents diff against local state, apply). Download/depot features hidden or disabled.

Transfer becomes a first-class feature: signed export bundles carry selected artifacts,
repo/content-library deltas, and catalog indexes across the gap. Update bundles
(ADR-0009) share the same signing/manifest format. The mode is configuration surfaced
as a persistent UI badge — one codebase, one image, never a fork.

## Rationale

- Alternative (connected-only product) shrinks the audience to a fraction of the real
  user base; alternative (two products) doubles maintenance for a feature-flag's worth
  of difference.

## Consequences

- Every feature must declare its mode availability; UI and API both enforce it.
- Bundle contents diffing requires the disconnected side to index its local state the
  same way the connected side indexes the depot.
- Cross-enclave versioning: a bundle built by appliance vN should import on vN-1/vN+1
  within a documented compatibility window.
