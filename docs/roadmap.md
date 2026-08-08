# Waypoint — Build Sequencing

Status: draft. The ordering principle: **every technology here is a well-trodden path
individually; the project risk is trying to build auth + job engine + secrets + both
product integrations simultaneously.** Each milestone produces something demonstrable
and forces exactly one new subsystem into existence.

## M0 — Design & contracts ✅ (closed 2026-08-02)

- ✅ UI design pass — high-fidelity prototype in [`ui/prototype/`](ui/prototype/);
  reconciliation recorded in [`ui/design-brief.md`](ui/design-brief.md).
- ✅ Data ledger → API contract + DB schema sketch: [`api-contract.md`](api-contract.md).
- ✅ Job/target state machines and SSE event schema: [`api-contract.md`](api-contract.md).

Next: decompose M1 into epics/issues per the `github-workflow` skill.

## M1 — Foundation + download vertical slice (current — epic [#1](https://github.com/blac9216/waypoint/issues/1))

Reordered 2026-08-02: the download workflow goes first — it is the easiest end-to-end
slice (no vCenter discovery, no InSpec/SAF pipeline, one credential) while still
forcing every foundation into existence.

- Compose stack: nginx + backend + Postgres + frontend shell. **Local auth only.**
- Job engine (ADR-0008): queue, dispatcher, runspace hosting (ADR-0006), SSE
  streaming (global + per-run) — proves the riskiest integration (C# ↔ PowerShell)
  on the simplest workflow.
- Minimal secrets store (ADR-0005 subset): envelope encryption + write-only API,
  initially holding just the Broadcom depot token.
- Wired through end to end: **depot catalog indexing (`catalog-index`) + catalog
  browser + download jobs (`download`) with live progress, checksum verification,
  and disk usage**, using the vcf-docker-download modules as the execution layer.
- Dev-only shortcut: the download tool binary is provisioned into the dev environment
  by hand (the in-UI install flow stays in M5). **Test depot tokens/config come from
  the private sibling repo at runtime — gitignored mounts, never committed here.**

## M2 — Sites, credentials & the STIG scan slice (epic [#13](https://github.com/blac9216/waypoint/issues/13))

- Full credential store (ownership model) + sites/targets CRUD, all configured fresh
  in the UI. **No importer from the sibling repos' `secrets.vault`/`site.json`** —
  Waypoint replicates their functionality and borrows code where sensible, but is not
  tied to their data formats (decision 2026-08-08).
- Discovery job type + cached inventory (needed by the start-a-scan flow).
- **STIG scan of a vSphere site with live logs in the browser** (the hero screen),
  then the remaining transports (NSX, SRG) + attestation/input document store with
  versioning.

## M3 — Identity & RBAC (epic [#14](https://github.com/blac9216/waypoint/issues/14))

- Keycloak (ADR-0004), OIDC integration, role mapping (Viewer/Cyber/Operator/Admin).
- Scheduling for read-only jobs under service credentials.
- Audit trail surfaces (who ran what, config version history).

## M4 — Remediation (epic [#15](https://github.com/blac9216/waypoint/issues/15))

- Admin-gated, typed-confirmation, never-schedulable remediation via child `pwsh`
  (vendor scripts unmodified). Remediation input documents from the config store.

## M5 — Download manager & managed content (epic [#16](https://github.com/blac9216/waypoint/issues/16))

- Depot catalog indexing into Postgres; catalog browser UI; download jobs with
  progress/verification; content-library + Photon repo management; disk usage.
- Download-tool install flow (local repo / depot fetch / manual upload with signature
  verification) — the tool is never bundled in the image (licensing, decided
  2026-08-02); catalog stays browsable index-only without the tool.
- Compliance-content management: the profiles repo as appliance state (pinned tag or
  tracked branch, `content-pull` when connected; air-gapped `content-import` lands
  with the M6 bundle format).

## M6 — Transfer & modes (epic [#17](https://github.com/blac9216/waypoint/issues/17))

- Connected/disconnected instance modes (ADR-0010); signed bundle format
  (shared with updates); export composer + import/validate/diff.

## M7 — Updater & appliance polish (epic [#18](https://github.com/blac9216/waypoint/issues/18))

- `upgrade.sh` consuming the update bundle → in-UI self-update via the updater
  sidecar (ADR-0009) → (optional) Packer-built OVA wrapper (ADR-0001).

## Deliberately deferred

- External secrets backends (Vault/OpenBao), multi-node anything, non-VMware products,
  request/approve workflow for Cyber-initiated scans (see open questions in
  [`domain-model.md`](domain-model.md)).
