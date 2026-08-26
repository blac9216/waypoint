# ADR-0024: Compliance execution items, attempts, credentials, and control settings

Status: Accepted (planned; implementation tracked by epic
[#726](https://github.com/blac9216/waypoint/issues/726))

Supersedes ADR-0021 §§4–7 where that decision resolves only target bindings, rejects a
whole run for one missing binding, or permits schedule-carried overrides. ADR-0021's
named purposes, compatibility matrix, independent purpose resolution, audit rules,
and closed-contract requirement remain accepted.

## Context

ADR-0023 freezes one planned item per concrete compliance component, but leaves its
execution, access, and control configuration to this decision. The shipped scan model
has one mutable job view per target, profile-wide configuration documents, target-only
credential bindings, and a run-wide credential halt. Those shortcuts cannot represent
independently retryable VCSA services, tens of thousands of VM components, or the
approved per-control configuration model without either rewriting history or building
a second scheduler beside the Postgres queue.

## Decision

### Component job and ordered-attempt hierarchy

A compliance run is a domain projection over its immutable plan. Each concrete
`PlannedComponentItem` has exactly one Postgres component job, which is the queue,
ownership, lease, priority, cancellation, and capacity-admission unit. Coverage
omissions outside the concrete resolved component set have plan/result rows but no
fake jobs. A readiness-failed component job remains visible and may have zero attempts;
it is never silently omitted. No family job, nested task engine, or run-level scheduler
sits above the queue.

Each component job owns an append-only ordered sequence of attempts, numbered
monotonically. An attempt records timing, runner/lease and stage transitions,
credential-binding attribution, redacted events/logs, cancellation and cleanup state,
outcome, and references to attempt-produced results/artifacts. ADR-0012 stage markers
resume work within that same attempt after a runner/lease failure; an operator Stop
cancels the current attempt, and Start/retry/restart creates a new attempt against the
same immutable planned item. It never resumes an operating-system process or resolves
current inventory, baseline, settings, or trust.

Cancellation is cooperative and terminal only after the runner has stopped execution
and recorded any required cleanup outcome. A cancelled or failed attempt is never
deleted or reset when another begins. One active attempt per component job is the
execution invariant; this does not decide whether separate runs may overlap, which
remains issue #649.

The latest completed attempt supplies the component's current result and therefore its
current contribution to run aggregates. A successful retry may replace a prior failed
current result, but prior attempts, failures, logs, results, and artifacts remain
immutable and addressable. Coverage omissions remain independent and keep the run
incomplete even when every executable component's latest attempt succeeds.

### Complementary operational and compliance projections

ADR-0019's global Live Jobs remains the cross-domain operational projection. The
compliance run projection is a domain-deep view over the same runs, component jobs,
attempts, and events, not another execution authority. At 10,000 or more component
jobs it obtains server-side grouped counts and bounded cursor-paged/searchable rows,
loads attempt events only for selected detail, and uses bounded SSE catch-up plus
paged history. It never requires every job or event in API/browser memory.

ADRs 0013/0014 still assign claims, leases, cancellation, events, and terminal state
to the executing runner. ADRs 0018/0020 admit every component job through the same
local and shared capacity controls as other jobs; compliance grouping and catalog
priority do not reserve or manufacture capacity.

### Credential hierarchy, schedules, and repair

The closed catalog declares the purpose(s) required by each planned component,
including a distinct compatible purpose for catalog-declared `vcf-api` work before
that work can execute. Reusable service bindings resolve from most to least specific:

1. component/purpose binding;
2. top-level target/purpose binding.

For an interactive run, a compatible saved-credential override is a more-specific,
run-scoped layer available to Cyber-or-higher; Operator-or-higher may instead supply
an ADR-0016 personal run secret. Scheduled runs use only configured service bindings
at component/purpose then target/purpose. A schedule never stores or applies an
interactive, saved, or ad hoc override. Every selected credential must satisfy the
purpose compatibility map, and the resolved provenance is frozen per planned item.

A missing, incompatible, or ambiguous credential affects only components requiring
that purpose. Their jobs remain explicit readiness failures while independent jobs
continue; safe diagnostics name the component and purpose but never secret data.
The run is incomplete, not rejected wholesale. The same isolation applies to a
missing required control input.

An authorized credential repair may replace a failed component/purpose binding for a
later attempt through the audited swap/resume mechanism. Repair must be compatible,
records old/new non-secret attribution and actor/time/reason, and changes access only:
it cannot alter the planned component, scope, baseline, closure, selector, transport,
control settings, trust policy, or output semantics. Credential health/halt accounting
and queries operate on all component-purpose bindings, not a legacy single
`jobs.credential_id`. Exact halt thresholds and grouping beyond this binding-aware
requirement remain implementation policy.

### Control-granular settings and snapshots

Input, Attestation, and future Remediation are three independently versioned setting
kinds keyed by stable baseline control identity. Each kind resolves
`Global → Site → Target`, most specific value winning; absence at a lower layer
inherits rather than erases the higher value. Values, author/time, applicability,
and provenance remain distinct. Remediation execution is not authorized by these
settings and remains issue #15.

Planning snapshots every effective setting needed by each control, including the
source layer/version, value or secret reference/digest, attestation actor/provenance
and expiry, and an explicit missing/inapplicable state. The immutable snapshot is part
of the planned item's compliance definition and is reused by every attempt. Later
edits require a new run.

A missing required Input leaves the affected component job visibly skipped without an
execution attempt and with a safe readiness reason. An applicable non-automatable
control without a valid attestation remains in the complete baseline result as
`Not_Reviewed`; there is no post-scan human-assessment workflow. An expired attestation
is not applied and its expiry is reported. Any applicable control that cannot produce
a compliance result because of an execution error likewise remains `Not_Reviewed`,
never `Not_Applicable` or omitted.

## Consequences

- Issues #735–#737 implement snapshots, credential requirements, and queue fan-out;
  #745 persists findings/attempt artifacts; #757 owns scaled API/UI delivery. All
  behavior in this ADR remains planned until those changes land.
- #607 remains required real-wrapper qualification; #608 remains runner-wide prompt
  prevention; and #612 remains outcome-semantics hardening. This architecture does not
  close their executable work.
- #664 is retained and broadened from a second-purpose trigger follow-up to all
  component-purpose halt/swap/query behavior. #665 and #678 remain transitional
  legacy-wire/test/UI cleanup until #785/#786 replace or adapt those paths.
- #721 and #757 remain blocking scale delivery: bounded queries must disclose and
  continue pagination rather than silently truncate. Architectural definition here
  does not claim their implementation complete.
- API/RBAC endpoints are #785, UI/roadmap contracts are #786, and trust, temporary SSH
  cleanup, evidence retention, and legacy retirement are #808.

## Alternatives rejected

- One job per run with hidden component tasks: it duplicates leases, retry, controls,
  and capacity admission outside Postgres.
- A new job for every retry: it weakens stable component ownership and makes current
  result aggregation ambiguous; ordered attempts express history directly.
- Resolve current configuration on retry: the same run would cease to be reproducible.
- Reject a whole run for one access/input gap: it violates component failure isolation
  and hides otherwise valid coverage.
