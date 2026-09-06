# ADR-0033: Disk space joins the shared capacity admission model

Status: Accepted
Amends: 0018
Date: 2026-09-06

## Context

ADR-0018's capacity lease pool admits jobs against CPU and memory, discovered from
cgroup limits or the host and shared across runners via a Postgres-coordinated pool.
Download-lane acquisition (ESX/patch, Photon, VMware Tools, VKS, content-library sync)
is the first job family in this codebase where the dominant resource constraint is
neither CPU nor memory — it is disk space on the volume a job writes into. The
download-parity design (Epic #16, owner grill decision 19) requires disk to join
admission before subscription-driven or tool-concurrency-unbounded acquisition
(decision R2-8: concurrency unbounded under capacity admission) can be trusted not to
fill a volume mid-run and leave a job in an unrecoverable partial-write state.

## Decision Drivers

- ADR-0018's admission model already generalizes CPU/memory across a shared pool; disk
  should reuse that shape rather than invent a parallel, download-lane-specific gate.
- Projected download size is knowable in advance from catalog/repo metadata (ADR-0028:
  metadata is indexed before any bytes move), so admission can be a preflight check,
  not a reactive one.
- A reserve must exist below "technically free" — filling a volume to the last byte
  risks the host's own operation (logs, Postgres WAL, other stores sharing physical
  disk) even when the job's own bytes would nominally fit.
- Per-store usage must be visible to operators (Dashboard, `/system`) the same way
  CPU/memory budgets already are, or disk admission failures will be opaque compared to
  the existing CPU/memory diagnostics ADR-0018 §2 established.

## Considered Options

1. **No disk admission; rely on the download tool's own failure handling.** Zero new
   machinery, but a job that runs out of disk mid-write fails in a lane-specific way
   (partial files, tool-specific error text) rather than being refused up front with a
   clear diagnostic, and provides no defense against a burst of concurrent
   subscription-driven jobs collectively exceeding free space even though each looked
   fine in isolation.
2. **Disk as a separate, download-lane-only admission gate outside the shared pool.**
   Faster to build in isolation, but creates a second admission code path with its own
   fairness/starvation/diagnostic shape, exactly the duplication ADR-0018 itself was
   written to avoid for CPU/memory.
3. **Disk joins the shared capacity lease pool as a third admitted resource, alongside
   CPU and memory** (this decision). One admission code path, one diagnostic shape,
   one place operators check; the projected-bytes-vs-free-space-minus-reserve
   calculation is a preflight lease claim exactly like a CPU/memory claim.

## Decision

Disk space becomes a third resource dimension in ADR-0018's shared capacity lease
pool. Before a download-lane job is admitted, its projected byte size (from indexed
catalog/repo metadata — ADR-0028) is checked against the target store's free space
minus a configurable reserve (`RunnerResources__MinFreeDiskReserveBytes` or
equivalent, following ADR-0018 §1's `RunnerResources__*` naming convention); a job
that would exceed the reserve is refused admission with a diagnostic naming the
projected size, the free space, and the reserve, the same shape ADR-0018 §3 already
requires for CPU/memory startup checks. Per-store disk usage surfaces to `/system` and
the Dashboard alongside the existing CPU/memory capacity report (ADR-0018 §2). Disk
admission composes with, and does not replace, tool-concurrency being otherwise
unbounded under capacity admission (decision R2-8) — a burst of subscription-evaluated
jobs is throttled by the same lease pool CPU/memory concurrency already throttles,
with disk as an additional claim that must also clear before a lease is granted.

## Consequences

- The capacity lease pool schema and protocol (ADR-0020) need a disk-bytes column/claim
  type alongside its existing CPU/memory shape; this is additive to, not a rewrite of,
  that protocol.
- A job whose target store's projected size cannot be known in advance (an
  unparseable, undated catalog entry — ADR-0028's quarantine case) cannot be admitted
  by projected size; it falls back to ad-hoc, non-subscription handling with a
  conservative default reserve check only.
- Multiple runner replicas sharing one depot volume (ADR-0029) share one disk-capacity
  claim for that volume, not one per replica — the lease pool's existing
  shared-across-runners design (ADR-0018 §4–5) already generalizes to this.
- Required observability (decision R2-8's throttle-detection requirement) now includes
  disk-admission refusals as a first-class, alertable event, not merely a log line.
