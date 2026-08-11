# ADR-0014: Runners own job leases, execution events, and resource admission

Status: Accepted

Supersedes the backend-worker ownership portion of
[ADR-0008](0008-job-engine.md). Preserves the stage-per-execution and resume semantics
of [ADR-0012](0012-stage-per-execution-dispatcher.md).

## Context

Once execution moves to dedicated services, either the backend can retain a job lease
while remotely instructing another process, or the process doing the work can own the
lease itself. Split ownership would require an internal dispatch/streaming protocol,
make a dropped connection ambiguous, and complicate cancellation, orphan recovery,
and terminal-state authority.

The existing Postgres queue, durable stage marker, append-only event log, and SSE
replay already form a process-independent coordination boundary. Runners also need
bounded parallelism that respects the CPU and memory actually allocated to their
container rather than one hard-coded global worker count.

## Decision

1. **Runners claim work directly from PostgreSQL.** The atomic
   `FOR UPDATE SKIP LOCKED` claim includes the runner's explicit job-type allowlist.
   Filtering after a claim is prohibited: a runner must never claim and release work
   belonging to another execution domain.

2. **The executing runner is the sole job-lease owner.** It renews the lease, observes
   `cancel_requested` and run aborts, performs stage transitions, reports terminal
   state, and participates in expired-lease recovery. The backend only creates jobs
   and performs authorized control mutations.

3. **Runners write structured events directly to PostgreSQL.** Job logs, progress,
   state changes, and audit-relevant execution events use the existing durable event
   contract. Backend SSE endpoints read and replay those rows; Docker stdout is not an
   event transport and no runner-to-backend streaming API is introduced.

4. **A shared C# runner library owns worker mechanics:** unique worker identity,
   filtered claims, handler registration, lease heartbeat, cancellation, graceful
   shutdown, stage-complete requeue, retry/recovery integration, event publication,
   secret redaction, health, and concurrency admission. Domain handlers own payload
   validation, credential choice, tool invocation, domain progress, artifacts, and
   success/failure interpretation.

5. **Concurrency is resource-aware and operator-bounded.** At startup a runner reads
   the CPU and memory limits assigned to its container through cgroups and combines
   them with handler resource profiles and operator caps. Exact resource weights and
   default concurrency values require measurement and are intentionally not fixed by
   this ADR. Multiple runner replicas may compete on the same queue safely, but one
   resource-aware replica per runner type is the initial topology.

6. **Execution runners decrypt claimed-job credentials locally.** The API continues
   to encrypt credential writes. Each runner receives database access and the
   envelope-encryption master key, authorizes decryption against the claimed job and
   its registered type, records the decrypt audit before use, registers plaintext
   with its local sink redactor, and keeps it only for the execution window. Plaintext
   credentials are never sent through an internal dispatch API.

7. **Storage follows least privilege:**
   - compliance runner: compliance-content read access and scan-artifact write access;
   - download runner: managed tool/depot/content write access;
   - backend: read-only artifact access where required to serve results/downloads;
   - runner source/tool mounts: only the domain that needs them.

## Rationale

- Lease ownership and process ownership remain identical, making crash and network
  failure semantics unambiguous.
- PostgreSQL already provides durable coordination at the product's scale; an internal
  runner RPC protocol or broker would duplicate it.
- SSE remains independent of which process executes a job.
- Resource-aware admission prevents a count suitable for discovery from being applied
  blindly to memory-heavy scans.
- Local decryption avoids transporting plaintext between services and preserves the
  existing audit/redaction model at the point of use.

## Consequences

- Database and master-key access expand from the backend alone to the two trusted
  runner services. Network, mount, authorization, audit, and canary tests must reflect
  that larger but explicit trust boundary.
- Queue claim SQL and its concurrency proofs must cover job-type allowlists.
- Event sequence allocation must remain database-authoritative across concurrent
  runner processes and replicas.
- Runner readiness must include registered capabilities and allocated-resource
  discovery; it must fail closed when required dependencies or mounts are unavailable.
- Scaling replicas improves process/failure isolation, but does not create more host
  resources. It remains an operator option rather than the default concurrency tool.
