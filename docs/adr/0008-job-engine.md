# ADR-0008: Central job engine on a Postgres-backed queue

Status: Accepted

## Context

Both products — and every future feature — are long-running operations against
infrastructure: scans, remediations, downloads, discovery, bundle export/import,
catalog indexing. The STIG runner currently owns its own runspace-pool orchestrator
(`module.parallelism.ps1`); the download tool runs sequentially. A UI demands shared
job history, live progress, and scheduling.

## Decision

One **job engine** in the backend serves all job types:

- **Queue**: Postgres table, claimed with `SELECT … FOR UPDATE SKIP LOCKED`;
  priority-ordered. No Redis/broker at this scale.
- **Run → Jobs fan-out**: a user-initiated Run expands to one Job per target/component.
  Priorities carry over from the STIG catalog's `reportGroup`/`priority` model
  (NSX=1, VCSA=2, vCenter=3, ESXi=4, VM=5, SRG=6); other job types declare their own.
- **Workers**: PowerShell runspace pools hosted in-process (ADR-0006) execute the
  existing modules. The predecessor's parallelism module ceases to be the orchestrator.
- **Streaming**: job log/state events stream to the UI over SSE and persist to
  Postgres. Two scopes: a per-run stream (live run view) and a **global event stream**
  feeding the ever-present job log drawer in the UI.
- **Run controls**: pause queue (stop dispatching, in-flight jobs finish) and abort run
  are first-class engine operations.
- **Failure policy**: Continue — an individual job failure never halts its run
  (inherited from the predecessors). Exception: **N consecutive auth failures (default
  3) against the same credential halt that queue** (`blocked`) rather than continuing,
  to avoid locking the service account out of AD; an Admin can swap the credential and
  resume, re-queueing the blocked jobs.
- **Scheduling**: read-only job types only (scans, discovery, catalog index) may be
  scheduled, under service credentials. Remediation, bundle apply, and updates are
  never schedulable — by design, not configuration.

## Rationale

- This is the AWX/Rundeck/Semaphore shape, proven for "credentials + jobs against
  infrastructure." Building it once means the download manager, transfer, and updater
  get progress/history/streaming for free.
- Postgres-as-queue removes an entire infrastructure component; our concurrency is
  dozens of targets, not thousands of messages.

## Consequences

- The job/target state machine and the events schema are the API contract for the
  UI's hero screen (live run view) — design them first.
- Dead-job recovery (worker crash mid-job) needs a heartbeat/lease column from day one.
