# ADR-0025: Compliance trust, temporary access cleanup, and evidence lifecycle

Status: Accepted (planned; implementation tracked by epic
[#726](https://github.com/blac9216/waypoint/issues/726))

Supersedes the permanent top-level target/profile scan model and the implication that
compliance operational history and results may have independent retention lifecycles.
It preserves ADRs 0012–0015 and 0019: runners still own execution and durable cleanup,
Postgres remains the queue/event authority, and compliance evidence remains domain-owned.

## Context

The planned component model freezes exact baselines, access, and ordered attempts, but
does not yet decide certificate trust, the narrow scan-time SSH mutation required by
some catalog capabilities, or the unit that constitutes a defensible scan record.
The shipped scan payload also still carries a caller-selected target/profile shortcut.
Leaving these gaps to individual handlers would permit process-global TLS bypass,
best-effort cleanup, incomplete artifacts, and two incompatible permanent scan models.

## Decision

### Connection-scoped trust

TLS verification is enabled by default for every HTTPS target and service connection.
Admins manage uploaded CA certificates/chains as public trust material, separately
from encrypted credentials. Ingestion validates format, size, chain, duplicates,
expiry, and safe storage paths and fails closed. A connection selects a versioned
managed trust bundle; transfer of that configuration uses ADR-0015's signed envelope.

An Admin may instead authorize certificate-verification bypass only for one named
target or service connection. The decision is explicit, reasoned, versioned, and
audited with actor/time, and produces a prominent warning in readiness and resulting
evidence. It is never inferred or inherited as an appliance-wide default. Planning
freezes the applicable trust-policy identity/version. A runner materializes that
policy for the individual client/session; it must never mutate process-global trust or
verification callbacks shared by concurrent jobs. Certificate failure is isolated to
the affected component/connection.

### Catalog-gated temporary SSH and durable restoration

Scans remain read-only with one explicit exception: an Admin may opt one target into
temporary SSH enablement when the exact catalog product/version declares a reviewed
capability provider that can inspect, enable, and restore SSH through an already
authorized management path. There is no global switch or generic shell fallback.
Without both policy and capability, disabled SSH is a named coverage failure for only
the dependent components.

Each immutable planned item freezes planning-time SSH availability and its observation
provenance, the target-specific temporary-enablement authorization/policy version, the
exact catalog capability and provider identity/version, and the resolved management-
credential purpose. Retries use those decisions from the same plan; later policy or
catalog edits cannot authorize or redirect mutation. Immediately before any mutation,
the runner re-observes the service and durably records that original runtime state and
provenance together with the cleanup obligation before changing it. It enables SSH
only for the bounded execution window. An originally enabled service is never
disabled. An originally disabled service must be restored after success, failure,
cancellation, timeout, runner restart, or lease recovery.

Restoration is mandatory, durable, idempotent, and retryable. The attempt cannot be
considered cleanly terminal until restoration succeeds or a persistent cleanup-
failed state records the unresolved obligation. That state raises a prominent
security/operational alert and remains eligible for reconciliation; retrying a scan
cannot erase or supersede it. Before another attempt may temporarily mutate the same
service, any unresolved restore must be reconciled or the service must be safely re-
observed and the original state re-established under the existing obligation. The
gate is keyed to that service and does not block independent sibling services or
components. Restore ownership never forks into independent sibling obligations. This
narrowly reconciles the read-only principle: scan checks and outputs do not remediate
configuration, while the authorized access-state mutation is separately audited and
must return the target to its observed state.

### One compliance evidence graph

One run-owned evidence graph contains requested/resolved scope, coverage and omissions,
immutable planned items, component jobs, every ordered attempt and redacted log/event,
control findings, applied attestation snapshots, HDF/CKL artifacts, cleanup records,
and STIG Manager upload attempts/receipts. References are immutable and preserve exact
catalog, product-version, baseline, profile closure, and XCCDF/mapping provenance.

A finding distinguishes a compliance outcome from execution status. A valid check may
be compliant or noncompliant; failure to execute is an execution error, not a failed
control. Every planned exact-baseline component materializes a complete current
control-ledger/artifact projection, including readiness that fails before job creation
and jobs with zero attempts. Every applicable baseline control appears in it exactly
once. A control that did not execute or lacks a valid required attestation is
`Not_Reviewed` with a safe
reason—never `Not_Applicable`, duplicated, or omitted. This projection does not invent
a job or attempt when readiness rules prohibit one. Coverage omissions continue to
record component/boundary work that never became executable and remain separate from
the control projection, so aggregate success cannot conceal missing coverage.

Every STIG-backed component produces a complete HDF and CKL projection from the same
exact active profile/XCCDF baseline. SRG components produce HDF only and are never
converted to CKL or uploaded. Earlier attempt findings/artifacts remain immutable
historical evidence. The latest completed attempt supplies the current projection
when one exists; otherwise the planned component's synthesized `Not_Reviewed`
projection is current, without rewriting or conflating attempt history.

### Direct STIG Manager upload

The job that produces an eligible CKL owns direct upload through the configured STIG
Manager API. Watched-directory upload is not supported. Destination/collection,
benchmark revision, run/component/attempt/artifact identity, request attempt,
safe status/error classification, actor, and sanitized receipt evidence are persisted.
Upload evidence is bounded and allowlisted: only redacted permitted response metadata
or body fields and receipt identifiers may be retained. Authorization/session headers,
tokens, reflected secrets or identifiers, and unbounded raw responses are never
stored. The sanitized record still preserves enough idempotency, duplicate/conflict,
retry, and diagnostic evidence to audit the exchange; exact wire fields remain #785.
Upload failure does not alter the scan finding outcome or destroy the immutable
artifact. Authorized retries reuse the retained CKL and frozen destination policy
without rescanning. Changing destination is a distinct future workflow, never an
implicit retry mutation.

### Graph-wide retention and legacy migration

One appliance-wide Admin-configurable compliance retention period defaults to six
months. Policy changes are prospective, versioned, audited, and visible before purge.
Eligibility and purge cover the complete evidence graph atomically: deletion may be
retryable internally, but readers see retained or tombstoned—not a partially missing
graph. A durable tombstone records run identity, policy version, actor/system trigger,
time, and outcome. Optional per-run holds remain issue #784 and, if implemented,
protect this same graph.

The shipped top-level target plus caller-selected profile payload is transitional.
There is one migration. Existing runs remain readable as historical legacy evidence.
Before legacy creation and fallback resolution are removed, each configured legacy
schedule/saved intent is deterministically translated only when its exact requested
scope can be preserved under catalog-resolved component planning. If translation
would widen, narrow, or ambiguously reinterpret scope, the schedule/intent is disabled
or blocked in an explicit action-required state with an audit record; it is never
silently changed. All new scan creation then uses requested scope → catalog-resolved
exact baseline → immutable component plan, and in-flight legacy work drains before
the fallback is removed. No adapter may create both representations, and no permanent
legacy endpoint/payload, profile picker, or fixed-path fallback remains. Endpoint,
authorization, and transition wire shapes remain #785.

## Consequences

- #753 implements managed trust; #744/#745 implement artifacts, uploads, findings,
  receipts, and retention. All behavior here remains planned until those issues land.
- #514's severity aggregate can consume the normalized control/finding graph, but its
  dashboard query remains open. #607 real-wrapper coverage and #608 noninteractive
  enforcement remain required executable work. #652's path-containment fix remains
  required during migration and defense in depth; removal of legacy profile selection
  does not make unsafe path handling acceptable. #784 remains optional and open.
- #649 remains the undecided separate-run concurrency policy. Remediation execution
  remains #15. API/security/RBAC wire contracts are #785; UI/roadmap contracts are
  #786. This ADR defines neither endpoints nor screen behavior.

## Alternatives rejected

- Process-wide certificate bypass or trust mutation: concurrent jobs could inherit a
  different connection's security decision.
- Best-effort SSH restoration: cancellation and lease loss would leave durable target
  mutation without a durable owner.
- Independent expiry for logs, findings, and artifacts: surviving fragments cannot
  prove scope, execution, baseline, or upload history.
- Watched-directory upload or rescanning after upload failure: both introduce a second
  lifecycle and weaken artifact identity.
- Permanent dual payload models: equivalent scans could resolve different baselines
  and evidence semantics.
