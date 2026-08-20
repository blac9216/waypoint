# Waypoint — Build Sequencing

Status: draft. The ordering principle: **every technology here is a well-trodden path
individually; the project risk is trying to build auth + job engine + secrets + both
product integrations simultaneously.** Each milestone produces something demonstrable
and forces exactly one new subsystem into existence.

> **Architecture realignment (approved 2026-08-11, delivered via #433).** M1/M2 closed
> against the original combined-backend design, but a fresh appliance exposed that
> their execution dependencies were not packaged into a functional deployment. ADRs
> [0013](adr/0013-control-plane-and-runners.md)–[0015](adr/0015-source-build-and-operator-export.md)
> replaced backend-hosted execution with dedicated runners and clarified operator-built
> packaging. Epic #433 landed that realignment (split `compliance-runner`/
> `download-runner` services, control-plane-only backend, issue #443) ahead of M3 —
> see "Architecture realignment" below for what shipped.

## M0 — Design & contracts ✅ (closed 2026-08-02)

- ✅ UI design pass — high-fidelity prototype in [`ui/prototype/`](ui/prototype/);
  reconciliation recorded in [`ui/design-brief.md`](ui/design-brief.md).
- ✅ Data ledger → API contract + DB schema sketch: [`api-contract.md`](api-contract.md).
- ✅ Job/target state machines and SSE event schema: [`api-contract.md`](api-contract.md).

Next: decompose M1 into epics/issues per the `github-workflow` skill.

## M1 — Foundation + download vertical slice ✅ (closed 2026-08-08 — epic [#1](https://github.com/blac9216/waypoint/issues/1))

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
manual upload) remains scoped to M5, not duplicated here. Test depot tokens/config
still come from the private sibling repo at runtime — gitignored mounts, never
committed.

## M2 — Sites, credentials & the STIG scan slice ✅ (closed 2026-08-09 — epic [#13](https://github.com/blac9216/waypoint/issues/13))

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

## Architecture realignment — delivered (epic #433)

Extracted a reusable C# runner host from the former backend dispatcher; added
long-lived `compliance-runner` and `download-runner` services; moved project-owned
Dockerfiles, orchestration, and PowerShell from the sibling repositories into their
runner build contexts; runners now own filtered claims, leases, cancellation, events,
secret decryption, resource-aware concurrency, and readiness, per ADRs
[0013](adr/0013-control-plane-and-runners.md)/[0014](adr/0014-runner-job-ownership.md).
The ASP.NET backend is control-plane-only (issue #443) and no longer references the
PowerShell SDK or any job handler at build time. This closed ahead of M3, which
depended on it.

## M3 — Identity, RBAC & scheduling ✅ (closed 2026-08-20 — epic [#14](https://github.com/blac9216/waypoint/issues/14))

Delivered: Keycloak in the Compose stack on its own Postgres database with scripted
realm bootstrap (four role groups, example LDAP federation config, CAC/PIV x.509 flow
documented for site enablement — [ADR-0004](adr/0004-identity-keycloak.md)); OIDC
bearer-token validation on the backend with canonical-issuer pinning
(`Oidc:ValidIssuer`, decoupled from discovery so a real browser-minted token
validates correctly behind nginx); a hand-rolled authorization-code + PKCE login flow
in the SPA (no external OIDC libs) replacing the M1 local-auth form, with local auth
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

## M4 — Remediation (epic [#15](https://github.com/blac9216/waypoint/issues/15))

- Admin-gated, typed-confirmation, never-schedulable remediation via child `pwsh`
  where process isolation is required. Remediation input documents from the config store.
- The scheduling engine's server-side read-only-only restriction (M3) already
  guarantees remediation cannot be scheduled; M3's step-up re-authentication guard
  (`RequireFreshAuth`, shipped for credential-secret overwrite) is available
  infrastructure to reuse for remediation's typed-confirmation gate rather than a new
  mechanism.

## M5 — Download manager & managed content (epic [#16](https://github.com/blac9216/waypoint/issues/16))

Depot catalog indexing, catalog browser UI, and download jobs with
progress/verification/disk usage shipped early in **M1** (see above) rather than
here — what remains scoped to M5 is everything M1 explicitly deferred:

- Content-library + Photon repo management.
- Download-tool install flow (authorized upstream repository / local source / manual
  upload with signature verification). The public project does not distribute the
  tool; operator-installed tooling is managed appliance state and transfers with the
  functions that require it (ADR-0015).
- Compliance-content management: the profiles repo as appliance state (pinned tag or
  tracked branch, `content-pull` when connected; air-gapped `content-import` lands
  with the M6 bundle format).

## M6 — Transfer & modes (epic [#17](https://github.com/blac9216/waypoint/issues/17))

- Connected/disconnected instance modes (ADR-0010); signed bundle format shared with
  updates; export composer + import/validate/diff for locally built images, required
  installed tooling, managed content, and selected artifacts (ADR-0015).

## M7 — Updater & appliance polish (epic [#18](https://github.com/blac9216/waypoint/issues/18))

- `upgrade.sh` consuming the update bundle → in-UI self-update via the updater
  sidecar (ADR-0009). Imported newer images are staged and shown as **Appliance update
  available**; applying them is a separate explicit Admin action. Optional Packer-built
  OVA wrapper remains an operator packaging path.

## Deliberately deferred

- External secrets backends (Vault/OpenBao), multi-node anything, non-VMware products,
  request/approve workflow for Cyber-initiated scans (see open questions in
  [`domain-model.md`](domain-model.md)).
