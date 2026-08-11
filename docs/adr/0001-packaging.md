# ADR-0001: Docker Compose first; optional OVA wrapper later

Status: Accepted; prebuilt-image delivery superseded by [ADR-0015](0015-source-build-and-operator-export.md)

## Context

Waypoint must deploy in air-gapped DoD-style environments to an audience of VMware
admins. Candidate packagings: a Kubernetes-based appliance OVA (Aria Automation style),
a plain Docker Compose stack, or a Compose stack wrapped inside a minimal-OS OVA.

## Decision

1. **v1 ships as a Docker Compose stack.** Air-gapped delivery is a tarball:
   `docker save` of all images + the compose file + an install script.
2. **v2 may wrap the identical compose stack in an OVA** built with Packer on a minimal
   OS (Photon OS preferred) for deploy-OVF/set-IP appliance UX. The OVA is a packaging
   wrapper, not a different architecture.

## Rationale

- Kubernetes buys rolling upgrades, horizontal scaling, and multi-node self-healing —
  none needed by a single-team appliance — at the cost of cluster lifecycle management,
  in-cluster cert rotation, two layers of networking, and ~4x resource footprint.
- Target users already run Docker (both predecessor tools are Docker images).
- Most non-K8s vendor appliances are exactly "minimal OS + container stack baked in,"
  so the OVA path stays open with zero rework.

## Consequences

- No zero-downtime rolling updates (accepted; see ADR-0009).
- Single-node only. If multi-node ever becomes real, that is a new ADR.
- Update/transfer bundle format must work for both compose-native and OVA deployments.
