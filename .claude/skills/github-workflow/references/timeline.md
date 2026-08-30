# Timeline re-evaluation (session start only)

Milestone due dates are projections, not promises, and they are recomputed once per
session so the Roadmap stays honest without being churned mid-run.

Inputs: every open milestone's issues with their **Estimate** section (size class → cycle
days from the calibration the planning skill produced, or its defaults when there is no
history); issue-level `blocked by` links, including across milestones; which milestones
have started (any issue assigned / in progress / merged); today's date.

Rules:
- Milestones never carry `blocked by` links between each other — that link rots the
  moment one issue unblocks. Order comes from **issue** dependencies and, absent those,
  from the existing `due_on` order.
- A milestone that has started is **pinned** at its actual start (its first started
  issue); its projected end = start + remaining critical path at the observed parallelism.
- A milestone the owner just started in parallel is placed from **now**, overlapping;
  it does not push the running one.
- Milestones not started are laid out serially after the running ones unless issue
  dependencies force otherwise.
- Write `due_on` only where it changed by more than a day; note the change in the session
  log. Never edit a closed milestone.
- If placement is ambiguous (two unstarted milestones with no dependency between them and
  no prior order), ask the owner rather than guess.

The planning skill (`plan-work`) does the first placement when it files a milestone and
can be called again to re-sequence when priorities change; this pass only keeps the
projection current between those calls.
