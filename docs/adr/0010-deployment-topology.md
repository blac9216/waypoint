# ADR-0010: One appliance, connected/disconnected modes, bundle-based transfer

Status: Accepted; operator-built transfer contents clarified by [ADR-0015](0015-source-build-and-operator-export.md)

## Context

The download manager inherently needs Broadcom depot access (connected side of the air
gap); the STIG runner works inside enclaves (disconnected side). The predecessor encodes
this split as a `Transfer/` staging directory. The planned estate: **one
internet-connected enclave using all features, plus disconnected enclaves consuming
its exports.**

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
