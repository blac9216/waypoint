# ADR-0001: Docker Compose first; optional OVA wrapper later

Status: Accepted
Amended-by: 0015
Date: 2026-08-02

## Context

Waypoint must deploy in air-gapped DoD-style environments to an audience of VMware
admins. Candidate packagings: a Kubernetes-based appliance OVA (Aria Automation style),
a plain Docker Compose stack, or a Compose stack wrapped inside a minimal-OS OVA.

## Decision Drivers

_Backfilled under ADR-0027 from #2, #47; the sources record no separate issue debating
the Kubernetes alternative — the comparison lives only in this ADR's own text, restated
here from the section originally titled "Rationale."_

- Target users already run Docker (both predecessor tools are Docker images).
- Kubernetes buys rolling upgrades, horizontal scaling, and multi-node self-healing —
  none needed by a single-team appliance — at the cost of cluster lifecycle management,
  in-cluster cert rotation, two layers of networking, and ~4x resource footprint.
- Most non-K8s vendor appliances are exactly "minimal OS + container stack baked in,"
  so keeping the OVA path open should cost zero rework later.

## Considered Options

_Backfilled under ADR-0027 from #2, #47; the sources record no separate issue debating
the Kubernetes alternative — the comparison lives only in this ADR's own Context and
Decision Drivers text._

- **Kubernetes-based appliance OVA** (Aria Automation style) — rejected. Buys rolling
  upgrades, horizontal scaling, and multi-node self-healing that a single-team appliance
  does not need, at the cost of cluster lifecycle management, in-cluster cert rotation,
  two layers of networking, and ~4x resource footprint.
- **Plain Docker Compose stack** — chosen for v1 (tracked in #2). Target users already
  run Docker; air-gapped delivery is a `docker save` tarball + compose file + install
  script.
- **Compose stack wrapped in a minimal-OS OVA** — chosen for optional v2 (tracked in #47,
  Packer + Photon OS). A packaging wrapper around the same Compose stack, not a different
  architecture, so it stays available with zero rework.

## Decision

1. **v1 ships as a Docker Compose stack.** Air-gapped delivery is a tarball:
   `docker save` of all images + the compose file + an install script.
2. **v2 may wrap the identical compose stack in an OVA** built with Packer on a minimal
   OS (Photon OS preferred) for deploy-OVF/set-IP appliance UX. The OVA is a packaging
   wrapper, not a different architecture.

## Consequences

- No zero-downtime rolling updates (accepted; see ADR-0009).
- Single-node only. If multi-node ever becomes real, that is a new ADR.
- Update/transfer bundle format must work for both compose-native and OVA deployments.
