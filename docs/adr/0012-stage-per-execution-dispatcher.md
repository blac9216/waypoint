# ADR-0012: Stage-per-execution dispatcher and resume-from-stage

Status: Accepted

## Context

ADR-0008 defines the job engine as one Postgres-backed queue, one lease per claimed
job, one worker executing a job to completion. `docs/api-contract.md`'s "State
machines" section already describes a `Standard`-shape scan job's pipeline as
`queued → running → attesting → converting → uploaded`, and migration 0015
(#124/#280) made `attesting`/`converting` require a live lease exactly like `running`
— they are actively-worked states, not resting states.

Issue #274 (the InSpec scan stage) hit a real gap while trying to implement the first
handler that reaches `attesting`: `JobDispatcherHostedService.RunJobAsync` forces
every `IJobHandler.ExecuteAsync` return through exactly one final transition, computed
from the handler's shape (`Succeeded` → the shape's terminal state, `Failed`/
`AuthFailed` → their terminal states). There was no way for a handler to report
"I finished this stage; leave the job where `AdvanceAsync` already put it, mid-pipeline,
and stop" — every attempt collapsed into either a false terminal success or a false
failure. That halted #274 (see its "Blocked" comment), which surfaced two designs:

- **Option A** (recommended by the initial analysis): one continuous handler
  execution walks every stage inside a single `ExecuteAsync`, with a new outcome kind
  telling the dispatcher "let the handler's last `AdvanceAsync`'d state stand instead
  of forcing terminal." Smallest change; one lease, one worker, one continuous log
  stream for the whole pipeline.
- **Option B**: stage-per-execution. Each stage becomes a separately claimable
  execution; the dispatcher requeues the job between stages instead of holding the
  lease across all of them.

The owner ruled directly on #274 (2026-08-08): **Option B**, with an explicit
requirement — *"I want the jobs to be able to resume from any stage after a
failure."* That rules out Option A on its own: one continuous execution cannot resume
mid-pipeline after a crash or a failure without re-running already-completed stages,
because nothing durable records which stage was reached independent of the
lease-bound `state` column. The ruling additionally identified the modeling
constraint migration 0015 imposes: `attesting`/`converting` now require a lease, so
the *between-stages resting representation* cannot be an unleased row in either of
those states — it has to be a state the claim query already treats as claimable.

This ADR also resolves #282, filed as 0015's fraternal-twin gap: `JobQueueRepository`'s
lease-recovery sweep (`RecoverSql`) and its supporting partial index
(`idx_jobs_lease_recovery`) matched only `state = 'running'`, so a worker that crashed
mid-`attesting`/`converting` left a row with an expired lease that the sweep could
never see — stranded, not recovered. #282 was deliberately left open pending this
issue's dispatcher design, because the correct requeue target for a crashed mid-stage
job is a stage-per-execution decision, not a guess made in isolation.

## Decision

1. **`queued` stays the only claimable, unleased state.** No new job-engine state is
   introduced. A job resting between stages is `queued`, exactly like a fresh job —
   the claim query (`ClaimSql`, unchanged) and 0015's CHECK both continue to hold
   without modification.

2. **A durable stage marker, `jobs.stage`, records pipeline position.** This column
   already existed in `0001_initial_schema.sql` (`stage TEXT NULL`) with no writer;
   this ADR is what gives it a contract. `NULL` means "start the pipeline from its
   first stage." Any other value is handler-defined (this repo does not enumerate
   stage names centrally — `job_type` and `payload` are already handler-defined in
   exactly this sense) and is opaque to the dispatcher and to `JobStateMachine`.

3. **A new handler outcome, `JobOutcomeKind.StageComplete`, carries the next stage
   marker.** A handler that finishes one stage of a multi-stage pipeline first calls
   the existing `JobExecutionContext.AdvanceAsync` to move the row to the
   intermediate state it just reached (e.g. `running → attesting`), exactly as before
   — then returns `JobExecutionOutcome.StageComplete(nextStage, note)` instead of
   `Succeeded`. The dispatcher, on seeing this outcome, does **not** run its forced
   terminal-state switch; instead it calls the new
   `IJobQueueRepository.RequeueAtStageAsync`, which moves the row from the handler's
   last `AdvanceAsync`'d state back to `queued`, clears the lease (same discipline as
   `AdvanceStateAsync`'s `clearLease: true` path), and writes `stage = nextStage`.
   Reaching the pipeline's actual last stage is still reported as `Succeeded`, which
   still forces the shape's terminal state exactly as it always has — this is why
   every existing single-stage handler (`download`, `catalog-index`, `discover`, the
   scan-stub) needs zero changes and shows zero behavior change: they never return
   `StageComplete`, so the dispatcher's pre-existing code path is unmodified for them.

4. **Claim routing hands the stage marker to the handler.** `ClaimedJob` (and the
   `ClaimSql`/`RETURNING` list) now carries `Stage`, read straight off the row. A
   handler reads `context.Job.Stage` and switches on it to resume at the right
   internal stage body — the same pattern `job_type` already uses to select a
   handler, one level down. A fresh job (`Stage == null`) starts at the first stage;
   a job that already reported `StageComplete` once resumes at the marker that call
   left; a job recovered by the lease sweep resumes at whatever marker was durable at
   crash time (see point 6).

5. **Resume/retry of a `failed` job re-enters at its stage marker.** `AdvanceStateAsync`
   (the terminal-failure path) never touches `stage`, so a job that fails at, say,
   `converting` keeps `stage = 'converting'` on its `failed` row. Whatever future
   operator-facing retry mechanism moves that row back to `queued` (out of this
   issue's scope — no such endpoint exists yet) needs to do nothing special: the next
   claim's `RETURNING stage` hands the marker straight back, and the handler resumes
   there rather than re-executing already-completed stages. This is the engine-level
   primitive the ruling asked for; the HTTP surface for it is a future issue.

6. **The lease-recovery sweep widens to `attesting`/`converting` and requeues at the
   marker, closing #282.** `RecoverSql`'s predicate becomes
   `state IN ('running', 'attesting', 'converting')`, and `idx_jobs_lease_recovery`
   (migration 0016) follows suit. The recovery `UPDATE` already never touched `stage`
   for the `running` case; extending the predicate costs nothing extra here, because
   whatever marker a prior `StageComplete` requeue left in place simply survives a
   recovery exactly like every other untouched column. `JobStateMachine.CanEngineTransition`
   is widened the same way `running → queued` was already special-cased, so
   `attesting → queued` and `converting → queued` are legal for an engine actor
   (recovery or a stage-complete requeue) while remaining illegal for a handler
   (`CanTransition` is unchanged) — a handler still cannot requeue itself and bypass
   retry accounting.

## Rationale

- **Matches the ruling directly.** "Resume from any stage after a failure" requires a
  durable, state-machine-independent record of pipeline position; Option A's
  single-continuous-execution model has nowhere to put that record that survives a
  crash mid-execution. Option B's stage marker is exactly that record.
- **Zero new job-engine states, zero change to the claimable/lease contract.**
  `queued` unclaimed, `running`/`attesting`/`converting` leased — 0015's CHECK, the
  claim query, and every existing transition table entry are untouched. The stage
  marker rides alongside `state`, not instead of it.
- **Zero behavior change for every handler that exists today.** None of them return
  `StageComplete`; the dispatcher's pre-existing `Succeeded`/`Failed`/`AuthFailed`
  switch is reachable exactly as before, unconditionally, for a handler that never
  reports it. This is proven by the existing 615-test suite passing unmodified.
- **#282 falls out for free.** Once `attesting`/`converting` are recognized as
  requeue-to-stage-marker states rather than unrecoverable dead ends, widening the
  sweep's predicate is the only change needed — the marker's persistence already does
  the "resume at the right place" work.
- Option A remains smaller in isolation, but smaller-and-wrong is not on the table
  once the ruling fixed the resume requirement; this ADR records why B, not A, is the
  standing decision, per this repo's ADR discipline (accepted decisions are not
  silently revisited).

## Consequences

- A multi-stage handler must be written stage-aware: it switches on
  `context.Job.Stage` rather than assuming it always starts at the beginning. This is
  a small but real authoring discipline every future `Standard`/`Srg`-shape handler
  (starting with #274's InSpec stage) must follow.
- A job resting between stages is, from the outside, indistinguishable from a fresh
  `queued` job except for `stage` — dashboards/UI reading `jobs.state` alone will show
  `queued` for both; anything wanting to say "resuming attest" rather than "waiting to
  start" must read `stage` too (`GetJobsForRunAsync` already projects it).
- Each stage boundary is a full claim/execute round trip: a multi-stage pipeline
  costs one claim-query round trip and one lease acquisition per stage, not one for
  the whole pipeline. Acceptable at this engine's scale (ADR-0008: dozens of targets,
  not thousands of messages); revisit if stage counts or claim contention grow.
- The per-run/global SSE log stream is no longer necessarily one continuous handler
  execution's output for a multi-stage job — it is stitched from separately-claimed
  executions of the same job id. Existing consumers already key events by `job_id`,
  so this does not break replay, but it is a new shape worth remembering when
  designing the Live Run UI's stage display (#274/#275).
- No operator-facing "retry a failed job" endpoint exists yet; this ADR provides the
  engine-level mechanism (marker survives failure, claim routing honors it) that such
  an endpoint will call, but does not itself add the HTTP surface.
