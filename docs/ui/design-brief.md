# Waypoint — UI Design Brief

Status: active — this is the working input to the design phase. Update it as the design
iterates; when screens stabilize, extract the **data ledger** (every element → its
source: Postgres table / SSE stream / catalog / computed) into the API contract.

## Prototype (2026-08-02) and reconciliation

A high-fidelity interactive prototype covering all nine screens lives in
[`prototype/`](prototype/) — open `vcf-ops-console.dc.html` in a browser; its
`README.md` is the design handoff (tokens, layout rules, per-screen specs).
**Mockups are illustrative; `../domain-model.md` and the ADRs are normative** — on
conflict, the domain model wins and the discrepancy gets logged. `../domain-model.md`
and `../api-contract.md` are this document's own source of truth for entities and
actions; where this brief and the prototype's visuals disagree, this brief wins.

The reconciliation pass adopted the prototype's inventions into the domain model
(three-layer Global→Site→Target config resolution, the Benchmarks content model,
compliance-content management, auth-failure queue halt, run controls, global job log
drawer) and recorded these decisions:

- **Remediation is Admin-only in v1** (the prototype's role summary saying Operator
  remediates is outdated; its Admin-only copy elsewhere is correct).
- **Mode badge is read-only in production**; mode changes are a redeploy, not a toggle.
- **Expired attestations are not applied** — control reports Open, run logs a WARN,
  Results lists them.
- The remediation typed-confirmation modal is deliberately undesigned until M4.

Fixes required in the next design iteration:

1. **Personal credentials must not appear stored** (ADR-0011): the start-a-scan
   "personal" option becomes *enter credentials now* with inline fields; remove
   `owner: personal` rows from the Credentials tab; fix the Operator role copy
   ("their own stored credentials" → "credentials entered at run time").
2. **Remediation copy**: make every mention Admin-only, matching the decision above.
3. **Priority queues**: P5 "GUEST / SSH TARGETS" may stay as a *visual* group, but the
   backend has six priorities (VM=5, SRG=6) and dispatch follows six, not five —
   annotate the queue header rather than flattening the model.

> Public-repo reminder: every mockup uses fictional data — `*.example.internal`
> hostnames, RFC 5737 IPs, invented finding counts. Never paste real inventory,
> scan output, or depot account data into designs.

## 🚧 Architecture realignment (epic #726) — read before designing any compliance screen

Status: **planned**. The prototype's compliance screens (Start a Scan, Live Run/Live
Jobs scan detail, Compliance Results, Benchmarks) were designed against the M2/M3
scan slice: a caller-selected profile, one job per top-level target, and
whole-profile config documents. [Epic #726](https://github.com/blac9216/waypoint/issues/726)
replaces that model with a closed catalog of exact-version baselines, stable
component identity, immutable plans, component jobs with ordered attempts, and
per-control settings (ADRs [0022](../adr/0022-compliance-catalog-and-content-lifecycle.md)–
[0025](../adr/0025-compliance-trust-cleanup-and-evidence.md), reconciled into
[`../api-contract.md`](../api-contract.md) and [`../security.md`](../security.md) by
issue #785). The subsections below are the reconciled information architecture —
**domain behavior and entity/action vocabulary, not a pixel spec.** The prototype
remains useful reference for visual density, tokens, and layout mechanics (see
[`prototype/README.md`](prototype/README.md)); its per-screen behavioral specifics
that assume the old model (a profile picker, one job per target, a single
mutable config document) are superseded by this section and by the 🚧-marked notes
inline in the Screens list below. Every 🚧 marker in this document follows the
convention already used in `../roadmap.md` and `../api-contract.md`: planned,
not yet shipped, and never to be conflated with the M2/M3 behavior it replaces.

### Benchmarks — source-of-truth entity/action map

**Entities:** `catalog product` (exact version, read-only, from `/catalog/products`)
→ `baseline` (`active`\|`superseded`\|`staged`, one exact `(product_version,
profile_version, xccdf_version?, mapping_version)` tuple, from `/baselines`) →
`control` (per-baseline, stable identity) → per-control **Input**, **Attestation**,
and future **Remediation** settings (three independently versioned kinds, each
resolving `Global → Site → Target`, from `/baselines/{id}/controls/{controlId}/settings`)
→ `content source` (STIG Manager or manual upload, from `/content-sources`) →
`candidate content` (staged, awaiting diff/review, from `/candidate-content`) →
`conflict` (same identity/version claimed by two artifacts, resolved by
`/candidate-content/{id}/conflicts/{id}/resolve`).

**Actions:**
- View exact baselines and their status (Viewer+).
- Review a candidate's per-control diff — `added`\|`removed`\|`changed`\|`remapped`\|
  `severity_impacting`\|`input_impacting`\|`attestation_impacting`\|`metadata_only`\|
  `unchanged`; an `unknown` equivalence result displays as `changed` (Cyber+).
- Approve a changed/unknown control, normally requiring a successful isolated
  candidate-test run reference; Admin may waive the test with a reason (Cyber+
  approve, Admin waive).
- Resolve a same-identity/version conflict by selecting one artifact with a reason
  (Admin-only).
- Run an unscheduled candidate test against any compatible configured
  component/target, including production, with explicit confirmation — never
  posture evidence, never CKL/upload-eligible (Admin-only).
- Activate a baseline atomically once every control/mapping/dependency/approval/
  test-or-waiver and whole-baseline validation gate passes; roll back to any
  retained, still-compatible previously approved baseline (Admin-only).
- Read/write per-control Input/Attestation/(future Remediation) settings at
  Global/Site/Target layers (Cyber+ read, Admin write per the RBAC table below).

**Superseded from the prototype:** "InSpec profile" as the unit users pick is
replaced by exact-version baselines the catalog resolves deterministically —
operators never choose a profile. The whole-profile Input/Attestation/Remediation
document editor (one YAML blob per profile per layer) is replaced by per-control
settings keyed by stable control identity; the prototype's three-tab
Input/Attestation/Remediation layout at the *control* level (not the profile level)
is the surviving visual pattern — reuse the tab structure, not the document-editor
behavior. Source conflicts (two artifacts claiming the same identity/version) are a
new screen element the prototype does not have; benchmark sync/import review
(staged candidates awaiting diff/approval) is new for the same reason.

### Start a Scan — source-of-truth entity/action map

**Entities:** `site` → `target` (top-level connection/policy boundary) →
`component` (the executable subject: stable identity = parent + catalog component
key + authoritative vendor identity; never hostname/IP/display-name/tree-position)
→ `target_scope` (`{ mode: "all", target_ids }` or `{ mode: "explicit",
component_ids }`) → `plan preview` (`/runs/plan-preview`: resolved component set,
per-component readiness, `fact_conflict` requiring resolution, required-purpose
credential coverage) → `CompliancePlan` (the frozen result of `POST /runs`).

**Actions:**
- Select target/component scope — `all` (expands against refreshed inventory,
  includes newly discovered compatible components) or an explicit component set
  (never silently widens) — **never a profile** (Cyber+ interactive; scheduled
  scans configure the same scope shape at `/schedules`).
- Trigger the mandatory pre-scan refresh (always runs before planning; latency is
  not a design concern — never skip or hide it behind an assumed-fresh cache).
- View the plan preview before confirming: resolved components, readiness per
  component (`ready`\|`coverage_omission` with reason), any `fact_conflict`
  (configured vs. discovered exact version disagree), required credential
  purposes.
- Resolve a `fact_conflict` interactively (Cyber+ chooses configured or
  discovered for this run only; the choice mutates neither source). A scheduled
  run cannot choose — it skips the component and raises the conflict instead.
- Supply interactive credential overrides (Cyber+ saved override) or an ad hoc
  personal credential (Operator+), keyed to `(component, purpose)`.
- Confirm and create the run — 202, or an honest zero-execution plan when no
  component is runnable in an explicit scope (never rejected outright unless
  refresh validates zero runnable components across the whole request).

**Superseded from the prototype:** the "product filters + InSpec profiles that will
apply" panel and the profile-implied scope in step 2 are removed entirely —
scope selection is asset-only. The plan-preview step (new) sits where the
prototype's step-2 checkbox tree lived, but the checkbox tree itself (cached
inventory → component selection) is still the right interaction shape; what
changes is that confirming now shows a plan preview with per-component readiness
and conflict resolution before the run is created, not an estimate line alone.
Credential/schedule steps (3–4) keep their prototype shape (service vs. personal,
run now vs. schedule) with ADR-0011's already-recorded fix (personal credentials
entered at run time, never stored).

### Live Run — source-of-truth entity/action map

**Entities:** one run-centric compliance workspace, a domain-deep projection over
`run` → `component job` (1:1 with a `PlannedComponentItem`; the queue/ownership/
lease/priority/cancellation/capacity-admission unit) → ordered `attempt`
(append-only, monotonically numbered; latest completed attempt supplies the
component's current result) → `attempt event/log` (redacted, per-attempt). Global
**Live Jobs** (ADR-0019) remains the separate cross-domain operational surface for
every job family, including this run's jobs — it is not superseded or replaced by
this workspace; the two are complementary (cross-domain operational vs.
domain-deep compliance).

**Actions:**
- View grouped priority counts and a searchable, virtualized component-job list —
  server-side grouped counts and cursor-paged/searchable rows at 10,000+ jobs; no
  requirement to hold every job or event in memory at once.
- Select one component job for log-first detail: current attempt's redacted
  events/logs plus the full ordered attempt history (prior attempts remain
  immutable and addressable, never deleted or reset).
- Stop (cooperative cancel of the active attempt; terminal only once the runner
  records cleanup, including any SSH-restore obligation) and Start/retry (creates
  a new attempt against the same immutable plan — never resumes a process or
  re-resolves current inventory/baseline/settings/trust) per component job.
- Bulk controls across a filtered/selected set at scale, alongside per-item
  controls — both must remain usable at 10,000+ component jobs.
- Repair a readiness-failed or auth-failed component job's credential for its
  next attempt only (`POST /jobs/{id}/repair-credential`) — access only, never
  scope/baseline/settings/trust.
- Distinguish `readiness_failed` (zero attempts, a gate failed before execution
  started) from `failed` (an attempt ran and ended badly) and from `blocked`
  (a queue-wide credential halt) — these are different states with different
  recovery actions, never collapsed into one "error" treatment.

**Superseded from the prototype:** the five fixed priority-queue headers (NSX →
VCSA → vCenter → ESXi → VMs) become dynamic grouped counts over component jobs —
still organized by priority, but the six-priority backend model (already noted as
a design-brief fix above) and virtualization at scale replace the fixed
five-header table. "Target" as the row unit becomes "component" (a VCSA service,
an individual VM, etc. are each their own component job, not sub-rows under one
target-level job). The per-target retry-in-place behavior becomes attempt
creation — the state-board and log-first layout switchers, and the blocked-banner
pattern for a queue-wide credential halt, remain valid visual patterns to reuse.

### Compliance Results — source-of-truth entity/action map

**Entities:** `run` → `component` → `coverage` (`executed`\|`coverage_omission`
with reason — the honest incomplete-coverage signal) → `findings` (every
applicable control in the exact-baseline closure appears exactly once;
disposition ∈ `Compliant`\|`NonCompliant`\|`Not_Reviewed`) → `attempt history`
(per component, ordered) → `artifacts` (HDF for every component; CKL for
STIG-backed components only, from the same exact active baseline) → `attestation
snapshot` (applied at plan/attempt time, immutable per run) → `upload receipt`
(direct job-owned STIG Manager upload attempts/status, sanitized).

**Actions:**
- View honest coverage: a coverage omission is never silently dropped or
  presented as successful coverage; a corrupt/absent HDF is `counts_available:
  false`, never a compliant-looking `0/0/0`.
- View current findings per component and drill into per-attempt history (the
  latest completed attempt supplies the current result; prior attempts remain
  immutable evidence).
- `Not_Reviewed` covers both "did not execute" (readiness failure, zero
  attempts, execution error) and "non-automatable control with no valid/
  unexpired attestation" — it is never reported as `Not_Applicable`, duplicated,
  or omitted. There is no post-scan human-assessment workflow to resolve it in
  this screen.
- Download exact artifacts (CKL/HDF) and export bundles.
- View STIG Manager upload attempts/receipts and retry a failed upload from the
  retained artifact without rescanning (Admin-only retry; destination change is
  a distinct future workflow).
- Remediate findings entry point (Admin-only, typed confirmation) — unchanged
  from the existing decision, still deliberately undesigned until M4.
- Purge a terminal run's evidence graph (Admin-only, typed confirmation,
  idempotent/retryable) — distinct from generic operational-history deletion,
  which defers to this domain purge for compliance-owned runs.

**Superseded from the prototype:** per-target artifact rows become per-component;
the "attested N/A" KPI becomes `Not_Reviewed` counts (attested is one of several
reasons a control can be `Not_Reviewed`, not a separate disposition). Attempt
history (new) sits alongside the existing per-target artifacts table as a
drill-down. The ATTESTATIONS APPLIED sidebar's shape (control id, scope pill,
justification, author/version) is still correct — it now keys to
`(component_id, control_id)` instead of `target` once per-control settings ship.

### Alerts — active content vs. sync-health/review, Admin-only acknowledgement

**Entities:** `alert` (`kind` ∈ `discovery_failure`\|`content_review_ready`\|
`content_sync_failure`\|`ssh_cleanup_failed`\|`trust_bypass_active`\|
`credential_readiness`\|`retention_purge_failed`, `severity`, `subject`,
`raised_at`, `acknowledged_at?`/`acknowledged_by?`).

**Actions:**
- View all alerts (Viewer+) filterable by kind/acknowledged/time.
- Acknowledge (Admin-only) — records awareness for audit only; **never resolves,
  clears, or hides the underlying condition.** A re-evaluation that still finds
  the condition true keeps the alert visible rather than requiring a new alert
  row. Acknowledgement must never read, in copy or interaction, as a "dismiss" —
  a distinct un-acknowledged/acknowledged badge stays next to the still-open
  condition, not a removal from the list.

**Two distinct alert families, never collapsed into one generic signal:**
1. **Review alerts** (`content_review_ready`) — additive success: new staged
   content is waiting for Cyber+ review/diff/approval. This is expected,
   routine traffic, not a failure.
2. **Sync-health/diagnostic alerts** (`discovery_failure`, `content_sync_failure`,
   `ssh_cleanup_failed`, `trust_bypass_active`, `credential_readiness`,
   `retention_purge_failed`) — something did not complete as expected and needs
   attention. Each keeps its own distinct kind and drill-down (e.g.
   `discovery_failure` links to `/discovery-refreshes` for the "why").

The prototype's single ATTENTION sidebar list is still the right visual container;
what's new is the two-family distinction above and the Admin-only acknowledge
action with its never-hides-the-condition guarantee — the prototype's alerts had
no acknowledge action designed at all.

## RBAC — UI role gates (matches `../api-contract.md`'s RBAC summary and
`../security.md`'s RBAC reconciliation; narrows/clarifies `../domain-model.md`'s
Roles table, never widens it)

| Action family | Viewer | Cyber | Operator | Admin |
|---|---|---|---|---|
| Read dashboards/runs/results/plans/attempts | ✅ | ✅ | ✅ | ✅ |
| Initiate an interactive scan (arbitrary subset) | — | ✅ | ✅ | ✅ |
| Control (pause/resume/abort/cancel/retry/repair-credential) a scan **the caller initiated** | — | ✅ | ✅ | ✅ |
| Control any scan regardless of initiator | — | — | — | ✅ |
| Interactive saved-credential override | — | ✅ | ✅ | ✅ |
| Interactive ad hoc (personal) credential | — | — | ✅ | ✅ |
| Manage recurring scan schedules | — | — | — | ✅ |
| Content: review/diff/approve changed or unknown controls | — | ✅ | ✅ | ✅ |
| Content: activate/roll back a baseline; waive a candidate test | — | — | — | ✅ |
| Trust bundles / scoped TLS bypass | — | — | — | ✅ |
| Temporary SSH enablement / reconcile | — | — | — | ✅ |
| Target/component persistent configuration (bindings, `configured_fact`, purge) | — | — | — | ✅ |
| Retention policy / graph purge | — | — | — | ✅ |
| Alert acknowledgement | — | — | — | ✅ |

Every gated action follows the existing **visible-with-disabled-reason** treatment
(`opacity: 0.42`, explanatory `title`) already established under Roles &
Permissions in `prototype/README.md` — a role that cannot act still sees the
control and the reason, it is never silently hidden. This is distinct from
mode-gating (air-gapped genuinely removes the Download Catalog nav item because
the feature does not exist): every RBAC gate above is a permission gap, not a
feature-does-not-exist gap, so it always renders visible-with-reason. The
Cyber+/Operator+ "own scan" control rule above is scan-specific and does not
widen any other job family's control authority (download/bundle-import/update
control checks are unaffected).

## Product in one paragraph

An on-prem web appliance unifying VMware STIG compliance (scan + remediate) and VCF
software download/repository management for DoD-style networks. Deploys in **connected**
mode (all features, builds signed export bundles) or **disconnected** mode (air-gapped,
imports bundles; download features hidden). A top-bar badge always shows the mode.

## Roles (screens must respect these)

| Role | Sees / does |
|---|---|
| Viewer | Read-only dashboards and results |
| Cyber | Viewer + initiate scans (service credentials) + export/audit results |
| Operator | Cyber + ad hoc scans with own credentials + downloads/content libraries |
| Admin | Everything: sites, credentials, users, remediation, updates, transfer |

## Domain model highlights that shape the UI

- **Sites** contain targets; multiple targets of a kind per site (e.g. two vCenters).
- ESXi hosts/VMs are **discovered and cached**: show "last refreshed", refresh action.
- Credentials have owners (personal vs shared/service); ad hoc runs may use "mine" —
  choosing "my credentials" **prompts for the password at run initiation** (personal
  credentials are never stored; no personal-credential CRUD screens in v1 — ADR-0011).
- STIG config (attestation/input YAML) is per-site per-component with per-target
  overrides, edited in a code-editor pane, **versioned with author + timestamp**.
- Scans: read-only, schedulable. Remediation: Admin-only, typed confirmation
  (`REMEDIATE`), never schedulable.
- STIG Manager: global default connection, optional per-site override.

## Screens, in priority order

1. **Live Jobs — the operational workspace.** A global selector shows every active
   run and job concurrently, grouped by run. Selecting a scan opens its priority-queue
   detail (NSX → VCSA → vCenter → ESXi → VMs → SRG); discovery, downloads, content,
   transfers, credential tests, and updates use type-specific details with a safe
   generic fallback. Selection never serializes runner execution.
2. **Dashboard** — fleet compliance posture by site, recent runs, repo disk usage,
   update status, mode badge.
3. **Start-a-scan flow** — pick site → scope (product filters + checkbox tree of cached
   hosts/VMs) → credential choice (mine vs service) → run now or schedule → confirm.
4. **Compliance Results** — scan/remediation runs only; run detail with per-target artifacts (CKL/HDF),
   CAT I/II/III severity counts, STIG Manager upload status, attestation version
   history. Generic lifecycle/log history remains in Live Jobs; other outputs remain
   in their owning domain screens.
5. **Download catalog browser** (connected) — indexed depot catalog: search/filter by
   product/version; rows with size + status (not downloaded / queued / downloading 43% /
   verified / failed); multi-select → queue; downloads queue view; content library +
   Photon repo management with disk usage.
6. **Transfer** — connected: compose + build signed export bundle; disconnected:
   import + validate, contents diff against local state.
7. **Config** — sites & targets (discovery status), credentials, users/roles,
   STIG Manager, STIG config documents (code editor + version history), updater
   (version, upload bundle, health-gated apply; online check only in connected mode).

## Style

Enterprise operations console: dense but calm data tables; dark theme primary + light
theme; restrained status colors (pass/fail/open); monospace only for logs and IDs;
left-rail navigation; top bar with mode badge + user/role. Ships as a self-hosted PWA
with **zero external assets** (no CDN fonts/icons — everything vendored).

## Realism requirement for mockups

Populate with plausible fictional data, never lorem ipsum: vCenter FQDNs
(`vcsa-01.example.internal`), ESXi hostnames (`esxi-01.example.internal`), VCSA
component names (`envoy`, `vmdird`, `eam`, `sts`, `ui`), STIG benchmark IDs/versions,
realistic finding counts, artifact names/sizes (`VMware-VCSA-all-8.0.3` ISO ~9 GB,
ESXi 8.0U3 patch bundles).

## Design-phase working agreements

- Design workflows, not features; the seven screens above carry the product.
- Timebox: reach "I can see the product," extract the data ledger, move on — pixel
  polish comes after data flows.
- Keep the ledger current; it becomes the API contract in M0/M1 (see
  [`../roadmap.md`](../roadmap.md)).
