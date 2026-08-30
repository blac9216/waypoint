# CONTEXT.md — glossary

The canonical vocabulary of this project. One entry per term: what it means, in one
line, and the words that are *not* used for it. No implementation details — types,
tables, endpoints and file paths belong in the design docs, which must use these words.
Update this file in the same PR as any doc that introduces or sharpens a term.

## Terms

**Site** — the top-level grouping, roughly an enclave's VMware estate; contains targets
of several kinds, with multiples allowed per kind.

**Target** — an Admin-configured connection and policy boundary within a site
(`vsphere`, `nsx-api`, or `ssh`/SRG); also today's scannable endpoint.

**Credential** — a stored access secret in one of two tiers: **Service/shared**, held in
the encrypted store and decryptable autonomously for scheduled or system runs; or
**Personal**, never a row in the reusable credential store, entered ad hoc at run
initiation and kept only as a terminal/expiry-bounded, run-scoped secret.

**Component** — a durable inventory entity beneath a target, identified by an
authoritative vendor identity rather than by hostname, IP, display name, or tree
position; tracks configured and discovered facts and a lifecycle of active, absent, or
retired.

**Component Observation** — immutable provenance from one discovery pass: the source
target, the observed identity and facts, the observed time, and the outcome.

**Discovery Refresh** — one boundary-scoped discovery pass (scheduled, pre-scan, or
manual) whose complete success is what allows components to be reconciled as absent or
retired.

**Requested Scope** — the immutable intent behind a run, either `all` (every compatible
component beneath named targets) or `explicit` (a named set of component identities).

**Resolved Scope** — the concrete component-identity set a requested scope actually
produces after a discovery refresh, with its own provenance and resolution time.

**Coverage Omission** — a component or boundary recorded as excluded from a run's
resolved scope, with its identity or boundary, stage, and reason.

**Compliance Plan** — the frozen record of a run's requested scope, resolved scope,
refresh coverage, and its planned component items; append-only once created.

**Planned Component Item** — one frozen, per-component entry inside a compliance plan:
the exact catalog revision, baseline, credential-purpose bindings, and any coverage
omissions for that component.

**Run** — what a user or schedule initiates (a scan, a download, and so on); the
domain-level unit of one initiated activity.

**Job** — the sole queue, priority, lease, cancellation, and capacity-admission unit; a
compliance plan maps each planned component item to exactly one job.

**Attempt** — one monotonically numbered, append-only execution record beneath a job;
only one attempt is active per job at a time.

**Compliance Evidence Graph** — the retention and integrity root for one run's
compliance findings, artifacts, and upload attempts.

**Control Finding** — one control's compliance disposition (compliant, noncompliant, or
`Not_Reviewed`) within the evidence graph, kept distinct from job/attempt execution
status.

**Managed Trust Bundle** — versioned, Admin-managed public CA material a connection
trust policy selects for verifying a target or service connection.

**Temporary SSH Obligation** — the audited, cleanup-owned record of a scan-driven,
opted-in temporary SSH-enablement mutation and its required restoration.

**Retention Hold** — an active exclusion that blocks a run's domain purge outright while
in effect, covering the whole evidence graph rather than individual records.

**Role** — one of four capability levels: **Viewer** (read-only), **Cyber** (Viewer plus
initiate scans and export results), **Operator** (Cyber plus ad hoc personal-credential
scans and download/catalog/content-library management), **Admin** (everything, including
shared credentials, remediation, and updates).
