# Waypoint — Build Sequencing

Status: living document. Work is organised as **delivery-story milestones** — each
one a multi-epic story with a rolled-up description on GitHub — per
[`process/work-tracking.md`](process/work-tracking.md). This page is the narrative
timeline: closed stories keep their delivered summary and dates; open stories link to
the milestone, which holds current state. The ordering principle is unchanged:
**every technology here is a well-trodden path individually; the project risk is
trying to build auth + job engine + secrets + both product integrations
simultaneously.** Each story produces something demonstrable and forces exactly one
new subsystem into existence. (Earlier revisions numbered these M0–M7; the mapping
is in [`adr/README.md`](adr/README.md#former-milestone-numbering).)

> **Architecture realignment (approved 2026-08-11, delivered via #433).** The first two stories closed
> against the original combined-backend design, but a fresh appliance exposed that
> their execution dependencies were not packaged into a functional deployment. ADRs
> [0013](adr/0013-control-plane-and-runners.md)–[0015](adr/0015-source-build-and-operator-export.md)
> replaced backend-hosted execution with dedicated runners and clarified operator-built
> packaging. Epic #433 landed that realignment (split `compliance-runner`/
> `download-runner` services, control-plane-only backend, issue #443) ahead of the identity story —
> see "Architecture realignment" below for what shipped.

## Design & contracts ✅ (closed 2026-08-02 — planning phase, no milestone)

- ✅ UI design pass — high-fidelity prototype in [`ui/prototype/`](ui/prototype/);
  reconciliation recorded in [`ui/design-brief.md`](ui/design-brief.md).
- ✅ Data ledger → API contract + DB schema sketch: [`api-contract.md`](api-contract.md).
- ✅ Job/target state machines and SSE event schema: [`api-contract.md`](api-contract.md).

Next: decompose the first story into epics/issues per the `github-workflow` skill.

## Foundation & download slice ✅ (closed 2026-08-08 — [milestone](https://github.com/blac9216/waypoint/milestone/3), epic [#1](https://github.com/blac9216/waypoint/issues/1))

Reordered 2026-08-02: the download workflow went first — it was the easiest
end-to-end slice (no vCenter discovery, no InSpec/SAF pipeline, one credential)
while still forcing every foundation into existence.

Delivered: Compose stack (nginx + backend + Postgres + frontend shell, local auth
only); job engine (ADR-0008: queue, dispatcher, runspace hosting per ADR-0006, SSE
streaming global + per-run); minimal secrets store (ADR-0005 subset: envelope
encryption + write-only API, holding the Broadcom depot token); **depot catalog
indexing (`catalog-index`) + catalog browser + download jobs (`download`) with live
progress, checksum verification, and disk usage**, wired end to end against the
vcf-docker-download modules as the execution layer. The download-tool binary is
still hand-provisioned in dev — the in-UI install flow (local repo / depot fetch /
manual upload) remains scoped to *Download & depot parity*, not duplicated here. Test depot tokens/config
still come from the private sibling repo at runtime — gitignored mounts, never
committed.

## Sites, credentials & STIG scan slice ✅ (closed 2026-08-09 — [milestone](https://github.com/blac9216/waypoint/milestone/4), epic [#13](https://github.com/blac9216/waypoint/issues/13))

Delivered: full credential store (ownership model) + sites/targets CRUD, all
configured fresh in the UI. **No importer from the sibling repos'
`secrets.vault`/`site.json`** — Waypoint replicates their functionality and borrows
code where sensible, but is not tied to their data formats (decision 2026-08-08).
Discovery job type + cached inventory; **STIG scan of a vSphere site with live logs
in the browser** (the hero screen); NSX + SRG transports; attestation/input
document store with versioning; STIG Manager integration. Live-stack + Playwright
validation passed end-to-end; residual items are owner-gated (e.g. #100, live-vCenter
HDF/CKL parity, live STIG Manager upload) or deferred to later milestones — see the
epic's closing comment for the full list.

## Runner realignment ✅ (closed 2026-08-19 — [milestone](https://github.com/blac9216/waypoint/milestone/5), epic [#433](https://github.com/blac9216/waypoint/issues/433))

Extracted a reusable C# runner host from the former backend dispatcher; added
long-lived `compliance-runner` and `download-runner` services; moved project-owned
Dockerfiles, orchestration, and PowerShell from the sibling repositories into their
runner build contexts; runners now own filtered claims, leases, cancellation, events,
secret decryption, resource-aware concurrency, and readiness, per ADRs
[0013](adr/0013-control-plane-and-runners.md)/[0014](adr/0014-runner-job-ownership.md).
The ASP.NET backend is control-plane-only (issue #443) and no longer references the
PowerShell SDK or any job handler at build time. This closed ahead of the identity story, which
depended on it.

## Identity, RBAC & scheduling ✅ (closed 2026-08-20 — [milestone](https://github.com/blac9216/waypoint/milestone/6), epic [#14](https://github.com/blac9216/waypoint/issues/14))

Delivered: Keycloak in the Compose stack on its own Postgres database with scripted
realm bootstrap (four role groups, example LDAP federation config, CAC/PIV x.509 flow
documented for site enablement — [ADR-0004](adr/0004-identity-keycloak.md)); OIDC
bearer-token validation on the backend with canonical-issuer pinning derived from
one operator-set `Oidc:PublicUrl` (issue #842, decoupled from discovery so a real
browser-minted token validates correctly behind nginx); a hand-rolled authorization-code + PKCE login flow
in the SPA (no external OIDC libs) replacing the foundation story's local-auth form, with local auth
now an off-by-default dev-flag for e2e/smoke paths only; four-role RBAC
(Viewer/Cyber/Operator/Admin) enforced end to end, closed out by a reflection-driven
endpoint × role matrix test covering all 18 API controllers that fails closed on any
unregistered or unguarded endpoint; a cron-style scheduling engine restricted
server-side to read-only job types with per-job-type minimum-role floors; a Dashboard
screen and aggregate endpoint; Users & Roles and Audit (filter/paging/CSV export)
surfaces; and step-up re-authentication (fresh `auth_time`, `prompt=login`/
`max_age=0`) gating credential-secret overwrite. Live-verified end to end: real
browser-path PKCE login through Keycloak to `GET /auth/me` returning 200 with the
correct role claim. 12 PRs merged (#516, #517, #522, #525, #527, #529, #532, #533,
#535, #537, #539, #540), each through independent contextless review; deferred
follow-ups and owner-gated items (#100, #412) are listed on the epic's closing
comment.

## Scan & download readiness ✅ (closed 2026-08-24 — [milestone](https://github.com/blac9216/waypoint/milestone/7))

Five epics that made the deployed appliance able to complete and diagnose its two
operator workflows end to end after live testing exposed readiness gaps:
[#558](https://github.com/blac9216/waypoint/issues/558) readiness screens and runtime
contracts; [#577](https://github.com/blac9216/waypoint/issues/577) credential deletion
vs. result purge; [#582](https://github.com/blac9216/waypoint/issues/582)
purpose-specific credential bindings; [#588](https://github.com/blac9216/waypoint/issues/588)
global job observability with domain-owned results;
[#706](https://github.com/blac9216/waypoint/issues/706) global job history with roll-off
and the restored compliance Live Run console. The deferred sweep at its close seeded the
milestone-less hardening epics #768, #769 and #770.

## Download-tool verification ✅ (closed 2026-08-25 — [milestone](https://github.com/blac9216/waypoint/milestone/8), epic [#667](https://github.com/blac9216/waypoint/issues/667))

Real Broadcom VCF Download Tool releases installed from distribution archives with
supplied checksums; the Software Depot Activation Code + Depot ID credential model, with
the legacy Download Token kept separate; the connected Download Catalog populated from
vendor-published, authenticated metadata. Live-validated against the real depot.

## Compose & deploy overhaul ✅ (closed 2026-08-27 — [milestone](https://github.com/blac9216/waypoint/milestone/9), epics [#841](https://github.com/blac9216/waypoint/issues/841), [#933](https://github.com/blac9216/waypoint/issues/933))

A concise production Compose configuration with a generated development override,
file-backed secrets and one canonical browser-facing URL; deploy/ documentation cut to
lean operator guidance with the rationale-index convention (`docs/rationale/deploy.md`).
Zero-workaround live validation.

## Compliance parity 🔄 (open — [milestone](https://github.com/blac9216/waypoint/milestone/10); design record epic [#726](https://github.com/blac9216/waypoint/issues/726))

Supersedes the scan-slice story's shortcuts (profile picker, target-granular jobs,
profile-wide config documents) with Waypoint-native VMware compliance parity: a
closed catalog of exact-version baselines, stable component inventory, immutable
plans, component jobs with ordered attempts, per-control settings, managed trust,
and one compliance evidence graph. This is a docs-first/docs-last program that realigns
the shipped compliance slice in place and gates every subsequent compliance
implementation issue. Epic #726 reached GitHub's 100-sub-issue cap on 2026-08-29 and
was split into six domain epics under the milestone — content pipeline (#1174),
discovery & inventory (#1175), scan planning & execution (#1176), results, evidence &
STIG Manager (#1177), scan UI (#1178), docs & conformance gate (#1179). Current state
lives on the milestone; #726 is closed as the design record.

**Wave 0 — architectural truth (hard gate, blocks all implementation).**
[#727](https://github.com/blac9216/waypoint/issues/727) reconciled architecture/
domain/ADRs (merged: [ADR-0022](adr/0022-compliance-catalog-and-content-lifecycle.md),
[ADR-0023](adr/0023-compliance-inventory-and-immutable-plans.md),
[ADR-0024](adr/0024-compliance-execution-attempts-credentials-and-settings.md),
[ADR-0025](adr/0025-compliance-trust-cleanup-and-evidence.md)) →
[#785](https://github.com/blac9216/waypoint/issues/785) reconciled the API/security/
RBAC contracts ([api-contract.md](api-contract.md), [security.md](security.md)) →
[#786](https://github.com/blac9216/waypoint/issues/786) (this document plus
[`ui/design-brief.md`](ui/design-brief.md) and
[`ui/prototype/README.md`](ui/prototype/README.md)) reconciles roadmap sequencing and
UI/domain vocabulary. No implementation child begins before all three merge.

**Waves 1–5 — implementation, dependency-ordered.** Content foundation (catalog,
ingestion, XCCDF, lifecycle) → targeting and planning (component identity, scope,
plan compilation) → component execution (jobs, attempts, credentials, per-control
settings) → outputs and operations (artifacts, uploads, findings, scaled Live Run) →
operator workflows and transfer (trust, SSH cleanup, evidence retention, legacy
migration). Issue-level sequencing lives on the domain epics and the Project board, not here —
this roadmap tracks story-level status, not per-issue dependencies.

**Final conformance gate.** [#750](https://github.com/blac9216/waypoint/issues/750)
audits shipped code, migrations, APIs, UI, tests, ADRs, architecture, roadmap,
security, testing, deployment, and operator documentation against every owner
decision before the milestone can close.

**Remediation stays out of scope.** Remediation execution and parity remain a
separate story (*Remediation*, below) — this story defines and delivers scan/benchmark
architecture only; it does not fold remediation into the realigned compliance model,
and *Remediation* does not block or gate on its waves.

## Remediation 📋 (backlog — [milestone](https://github.com/blac9216/waypoint/milestone/12), epic [#15](https://github.com/blac9216/waypoint/issues/15))

- Admin-gated, typed-confirmation, never-schedulable remediation via child `pwsh`
  where process isolation is required. Remediation input documents from the config store.
- The scheduling engine's server-side read-only-only restriction (identity story) already
  guarantees remediation cannot be scheduled; the identity story's step-up re-authentication guard
  (`RequireFreshAuth`, shipped for credential-secret overwrite) is available
  infrastructure to reuse for remediation's typed-confirmation gate rather than a new
  mechanism.

## Download & depot parity 🔄 (open — [milestone](https://github.com/blac9216/waypoint/milestone/11); design record epic [#16](https://github.com/blac9216/waypoint/issues/16))

Waypoint-native parity with `vcf-docker-download` and beyond it: the appliance manages
the entire vendor catalog as a true VCF depot (vendor catalog as single artifact
identity; disk walk as presence sweep), with subscription-driven stores for ESX patches
(UMDS), Photon, VMware Tools, VKS and local VCSP content libraries. Research-first
(epic #1026, closed 2026-08-29), then Wave 0 doc reconciliation, then the lanes. The
owner's 2026-08-28 decision record lives on #16. Split 2026-08-29 into seven lane
epics: catalog & depot core (#1180), vendor acquisition (#1181), subscriptions,
retention & scheduling (#1182), ESX patch store (#1183), mirror lanes (#1184), content
libraries (#1185), docs, research & conformance gate (#1186; #1037 is the close gate).
The public project never distributes the vendor tool; operator-installed tooling is
managed appliance state and transfers with the functions that require it (ADR-0015).
Air-gapped `content-import` lands with the transfer story's bundle format.

## Transfer & enclave modes 📋 (backlog — [milestone](https://github.com/blac9216/waypoint/milestone/13), epic [#17](https://github.com/blac9216/waypoint/issues/17))

- Connected/disconnected instance modes (ADR-0010); signed bundle format shared with
  updates; export composer + import/validate/diff for locally built images, required
  installed tooling, managed content, and selected artifacts (ADR-0015).

## Self-update & appliance packaging 📋 (backlog — [milestone](https://github.com/blac9216/waypoint/milestone/14), epic [#18](https://github.com/blac9216/waypoint/issues/18))

- `upgrade.sh` consuming the update bundle → in-UI self-update via the updater
  sidecar (ADR-0009). Imported newer images are staged and shown as **Appliance update
  available**; applying them is a separate explicit Admin action. Optional Packer-built
  OVA wrapper remains an operator packaging path.

## Deliberately deferred

- External secrets backends (Vault/OpenBao), multi-node anything, non-VMware products,
  request/approve workflow for Cyber-initiated scans (see open questions in
  [`domain-model.md`](domain-model.md)).
