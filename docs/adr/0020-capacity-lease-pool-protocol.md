# ADR-0020: Capacity lease pool protocol, recovery, and fairness policy

Status: Accepted

Delivers the shared capacity lease pool whose direction ADR-0018 §4 recorded (Option B,
owner ruling on issue #555, 2026-08-23) and deliberately deferred to issue #569.
Complements ADR-0014 (job leases) and ADR-0018 (per-runner budget discovery and the
startup admission invariant), which are unchanged.

## Context

ADR-0018 accepted a shared, database-coordinated capacity pool so runner replicas share
real appliance capacity dynamically instead of each assuming the whole host, but left
the pool's schema, claim protocol, recovery, and fairness policy to #569. The design
must never let coordination failure become silent overcommit, must keep a runner's own
discovered/capped budget authoritative (ADR-0018 §5), and must prevent large-profile
jobs from being starved indefinitely by streams of small ones.

## Decision

1. **Schema (migration 0036).** A singleton `capacity_pool` row holds the appliance's
   shareable CPU/memory capacity: operator-set (`CapacityPool:PoolCpuCores`/
   `PoolMemoryBytes`, `source='operator'`, authoritative, never overwritten by derived
   reports) or derived (each runner contributes its host-derived numbers from ADR-0018
   discovery at startup, converged with `GREATEST` per axis — a container-capped
   replica never shrinks what an uncapped sibling measured). `capacity_leases` holds
   one row per job with a slice of the pool — runner id, job id, job-type weights from
   `JobResourceProfiles`, heartbeat/expiry timestamps — keyed on **job id**, because
   the jobs-queue lease (ADR-0014) already guarantees single job ownership; the
   capacity row follows the job across worker loss and re-claim.
2. **Atomic claim, serialized on the pool row.** Before executing a claimed job (and
   strictly *after* local `ResourceAdmissionController` admission, so the per-runner
   discovered/capped budget stays the hard upper bound), the runner executes one SQL
   statement that takes `FOR UPDATE` on the pool row, sums unexpired leases, and
   inserts the new lease only if it fits. Two runners racing for the last slot
   serialize on that lock; overcommit by race is structurally impossible.
3. **Heartbeat and reap ride the existing job-lease clock.** The dispatcher's job
   heartbeat loop renews the capacity lease with the same cadence; a worker that stops
   heartbeating loses both leases together. Expired capacity rows stop counting
   *immediately* (every claim filters `expires_at > now()`) and are physically deleted
   by `LeaseRecoveryHostedService`'s existing sweep — recovery by predicate first,
   cleanup second, exactly like jobs-queue lease recovery. Release happens on terminal
   state, stage requeue, and released claims; a failed release falls back to expiry.
4. **Failure posture: deny, never overcommit.** Any database failure in the claim path
   is answered as a denial: the job claim is released back to `queued` (the same path
   as a local admission denial) and retried later. A missing pool row denies everything
   — an unregistered pool admits nothing rather than guessing. A capacity lease lost
   *while its job runs* is logged and re-claimed on the next heartbeat; the running job
   is never cancelled over lost bookkeeping, because the job-queue lease is the
   ownership authority (ADR-0014).
5. **Fairness: reservation escalation.** A job continuously denied pool capacity for
   longer than `CapacityPool:StarvationReservationAfter` (default 30s) parks a
   *reservation* row (`reserved=true`). Reservations count against the pool for **other
   jobs'** claims — so capacity freed by completing jobs accumulates for the starved
   job instead of being consumed by an endless stream of smaller claims — but a job
   converting its *own* reservation checks only active leases, so two mutually
   oversized reservations cannot deadlock the pool: first-fit conversion wins and the
   other keeps waiting with its reservation (and its accumulated priority) intact.
   Reservations heartbeat and expire like leases, so a dead waiter cannot pin capacity.
6. **Visibility.** `GET /system` gains a `capacity_pool` object: total capacity and its
   source, leased CPU/memory, active lease count, and every waiting reservation (job,
   type, runner, weights, waiting-since) — the starvation-reason surface. Denials,
   reservations, reaps, and pool registration are logged; pool-unavailable denials log
   at Error.

## Rationale

- Serializing claims on the singleton pool row is the smallest mechanism that makes
  concurrent claims safe; at this product's job rates (ADR-0014's own scale argument)
  lock contention on one row is negligible against seconds-to-hours job durations.
- Keying leases on job id makes worker loss a non-event for accounting: the re-claimed
  job takes over its own row instead of leaking a stale one alongside a new one.
- Reservation escalation was chosen over strict FIFO queueing or priority aging because
  it needs no new scheduler: the existing deny/retry loop plus one flagged row yields
  "freed capacity accumulates for the starved job", which is the property the
  acceptance criterion actually asks for, with bounded complexity and a clean
  operator-visible surface.
- Deny-on-DB-failure puts the database in the admission path exactly as the Option B
  ruling accepted, with the failure mode chosen to be the safe one: a stalled queue is
  recoverable and visible; an overcommitted appliance mid-scan is neither.

## Consequences

- Admission now takes one extra database round-trip per claim; a denied claim is
  released back to the queue, so contention shows up as claim/release churn (rate-
  limited by the dispatcher's existing backoff), not as blocked workers.
- If every runner runs `CapacityPool:Enabled=false`, behavior reverts to ADR-0014 §5
  per-runner admission (each replica assumes its own budget) — the pre-#569 overcommit
  exposure returns. The default is enabled.
- The pool row's `source='operator'` is sticky: clearing the operator configuration
  later does not automatically fall back to derived capacity until the row is removed
  by an operator/migration connection (runners deliberately hold no DELETE grant).
- Runner role grants expand to the two new tables (migration 0036), with
  `RunnerRoleGrantDriftTests` extended to hold the boundary.
