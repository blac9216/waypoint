# ADR-0019: Global job observability with domain-owned results

Status: Accepted

## Context

ADR-0008 gives scans, discovery, credential tests, downloads, content operations,
transfers, remediation, and updates a common Run/Job execution model with global and
per-run event streams. ADRs 0013, 0014, 0017, and 0018 assign execution ownership and
capacity without changing that common model.

The original UI design used **Live Run** and **Results** for a scan fan-out. Reusing
their generic `/runs` query for every job family made downloads and discovery look
like compliance scans. It also made a single selected run appear to be an execution
limit even though runners may execute several runs and jobs concurrently.

Job lifecycle data is common, but outputs are not. Inventory, downloaded content,
compliance findings, profiles, and transfer bundles have different ownership,
authorization, retention, and destructive actions.

## Decision

1. **Live Jobs is global operational observability.** A top-level workspace lists
   every active Run and Job, groups jobs by run, and lets the operator select among
   concurrent work. Selection observes execution; it does not schedule or serialize
   it. The runner and shared-capacity ADRs remain authoritative for concurrency.
2. **Details are type-specific.** A renderer selected from the authoritative run/job
   type presents relevant progress and controls. Scan pipeline stages from ADR-0012
   remain scan-specific. Unknown types use a safe generic lifecycle/log renderer.
3. **Operational history has a narrow meaning.** It contains identity, type, actor,
   target/context attribution, state, timing, redacted events/logs, and diagnostics.
   Active state uses the global/per-run SSE feeds; historical logs use a bounded,
   cursor-paged query over the same persisted events.
4. **Durable results remain domain-owned.** Compliance Results owns scan/remediation
   findings, attestations, waivers, and artifacts; Targets owns current inventory and
   credential-test/discovery outcomes; Catalog/Library owns downloaded content;
   Compliance Content owns profiles; Transfer owns bundles; system administration
   owns updates. Operational details link to those resources rather than reproducing
   their management actions.
5. **Lifecycle operations are explicit.** Deleting operational history never
   implicitly deletes domain state. A domain purge enumerates its owned projections
   and artifacts, is separately authorized and confirmed, is retryable, and leaves a
   non-secret audit tombstone. Credential deletion is likewise separate from history
   deletion.
6. **Visibility and action authorization are distinct.** A role may observe permitted
   job metadata/logs without receiving the ability to perform the job type's domain
   actions. Every stream and historical query applies server-side authorization and
   sink-level secret redaction.

## Rationale

The common operational projection preserves the value of one job engine while
keeping domain semantics honest. Type-specific renderers allow new handlers to reuse
status, logs, and concurrency UX without pretending all jobs produce scan artifacts.
Separating history from domain state also prevents a generic cleanup action from
silently destroying appliance content or compliance evidence.

## Consequences

- The Compliance navigation keeps Start a Scan, Compliance Results, and Benchmarks;
  Live Jobs is top-level and cross-domain.
- APIs need filterable active/history queries and bounded persisted-event reads in
  addition to the existing SSE streams.
- Every supported job type declares a detail renderer or deliberately uses the
  generic fallback, plus the domain route that owns any durable output.
- Retention policy must classify operational records separately from each domain's
  state and artifacts.
- Existing runner ownership, lease, stage, and shared-capacity decisions are not
  superseded.
- Owner review of the epic #588 outcome (2026-08-24, epic #706) found that a pure
  global surface with no domain console lost operationally load-bearing detail the
  deleted scan-only Live Run screen had (priority queues, stage board, per-target
  rows, finding counters, blocked banner — issues #704/#705) and that terminal work
  vanishing from the global list (only active runs were queryable) left no in-app way
  to browse completed work. Neither gap contradicts decision 1 or 4 above — decision 1
  never claimed the global workspace was the ONLY place a run's detail could be
  presented, and decision 4 already says durable domain output "link[s] to those
  resources rather than reproducing their management actions," which is exactly what
  a restored, dedicated compliance console (linked from the global surface, not
  duplicating it) does. The learned shape: the global workspace (renamed **Jobs**,
  issue #708 — its title no longer said "Live" once it also showed terminal history;
  `key`/route stay `live-jobs`/`/live-jobs`) gained a History mode (filtered,
  cursor-paged `GET /runs/history`, issue #689/#708) reusing decision 2's renderer
  registry and decision 3's historical-events query, bidirectionally linked to a
  restored dedicated compliance Live Run console (issue #707) that is not itself
  superseded or replaced by the global surface — the two are complementary
  projections, one cross-domain and operational, one domain-deep for compliance scan
  monitoring and controls.
- Roll-off (issue #708): an operator-configurable, disabled-by-default periodic sweep
  applies decision 5's existing lifecycle-deletion operation (`RunHistoryDeletionService`,
  issue #592) unattended to terminal runs whose generic-deletion gate is already
  `None` (docs/domain-model.md's classification table). It does not create a new
  deletion mechanism or loosen decision 5's compliance-purge gate — `scan`/`remediate`
  runs are excluded from the sweep's candidate query outright, never auto-deleted; the
  History mode's default view instead windows them out by time/type filter, which is
  a presentation default, not a deletion.

## Delivery

Tracked by [Epic #588](https://github.com/blac9216/waypoint/issues/588). Historical
log access is #581, the concurrent Live Jobs workspace is #590, type-specific details
and domain routing are #591, and lifecycle separation is #592. Credential deletion
and compliance-result purge are decomposed under #577. History mode + gate-respecting
roll-off is #708/#689; the restored compliance Live Run console is #707 — both
decomposed under [epic #706](https://github.com/blac9216/waypoint/issues/706).
