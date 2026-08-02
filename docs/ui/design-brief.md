# Waypoint — UI Design Brief

Status: active — this is the working input to the design phase. Update it as the design
iterates; when screens stabilize, extract the **data ledger** (every element → its
source: Postgres table / SSE stream / catalog / computed) into the API contract.

## Prototype (2026-08-02) and reconciliation

A high-fidelity interactive prototype covering all nine screens lives in
[`prototype/`](prototype/) — open `vcf-ops-console.dc.html` in a browser; its
`README.md` is the design handoff (tokens, layout rules, per-screen specs).
**Mockups are illustrative; `../domain-model.md` and the ADRs are normative** — on
conflict, the domain model wins and the discrepancy gets logged.

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

1. **Live run view — the hero screen.** A scan fanning out across ~40 targets in
   priority queues (NSX → VCSA components → vCenter → ESXi → VMs → SRG). Per-target
   rows with states (`queued / running / attesting / converting / uploaded / failed`),
   streaming monospace log pane, live pass/fail/N-A counts, overall progress. Iterate
   here first — this screen constrains the backend most (state machine, SSE schema,
   log storage).
2. **Dashboard** — fleet compliance posture by site, recent runs, repo disk usage,
   update status, mode badge.
3. **Start-a-scan flow** — pick site → scope (product filters + checkbox tree of cached
   hosts/VMs) → credential choice (mine vs service) → run now or schedule → confirm.
4. **Results & history** — runs list; run detail with per-target artifacts (CKL/HDF),
   CAT I/II/III severity counts, STIG Manager upload status, attestation version
   history.
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
