# Waypoint — Build Sequencing

Status: draft. The ordering principle: **every technology here is a well-trodden path
individually; the project risk is trying to build auth + job engine + secrets + both
product integrations simultaneously.** Each milestone produces something demonstrable
and forces exactly one new subsystem into existence.

## M0 — Design & contracts (current)

- UI design pass (screens in [`ui/design-brief.md`](ui/design-brief.md)); hero screen =
  live run view.
- "Where does this data come from?" ledger from the design → API contract + DB schema.
- Job/target state machine and SSE event schema drafted (they constrain everything).

## M1 — Job engine wrapping ONE workflow, end to end

- Compose stack: nginx + backend + Postgres + frontend shell. **Local auth only.**
- Job engine (ADR-0008): queue, dispatcher, runspace hosting (ADR-0006), SSE streaming.
- One workflow wired through: **STIG scan of a vSphere site with live logs in the
  browser**, using the existing execution modules. Results/history persisted.
- Discovery job type + cached inventory (needed by the start-a-scan flow).
- This milestone proves the riskiest integration (C# ↔ PowerShell runspaces) first.

## M2 — Credential store & sites

- Envelope-encrypted secrets (ADR-0005), credential ownership model, write-only API.
- Sites/targets CRUD replacing hand-edited `site.json`; migration path from
  `secrets.vault` + `site.json`.
- Remaining scan transports (NSX, SRG) + attestation/input document store with
  versioning.

## M3 — Identity & RBAC

- Keycloak (ADR-0004), OIDC integration, role mapping (Viewer/Cyber/Operator/Admin).
- Scheduling for read-only jobs under service credentials.
- Audit trail surfaces (who ran what, config version history).

## M4 — Remediation

- Admin-gated, typed-confirmation, never-schedulable remediation via child `pwsh`
  (vendor scripts unmodified). Remediation input documents from the config store.

## M5 — Download manager & managed content

- Depot catalog indexing into Postgres; catalog browser UI; download jobs with
  progress/verification; content-library + Photon repo management; disk usage.
- Download-tool install flow (local repo / depot fetch / manual upload with signature
  verification) — pending the licensing confirmation in `domain-model.md` open
  questions; catalog stays browsable index-only without the tool.
- Compliance-content management: the profiles repo as appliance state (pinned tag or
  tracked branch, `content-pull` when connected; air-gapped `content-import` lands
  with the M6 bundle format).

## M6 — Transfer & modes

- Connected/disconnected instance modes (ADR-0010); signed bundle format
  (shared with updates); export composer + import/validate/diff.

## M7 — Updater & appliance polish

- `upgrade.sh` consuming the update bundle → in-UI self-update via the updater
  sidecar (ADR-0009) → (optional) Packer-built OVA wrapper (ADR-0001).

## Deliberately deferred

- External secrets backends (Vault/OpenBao), multi-node anything, non-VMware products,
  request/approve workflow for Cyber-initiated scans (see open questions in
  [`domain-model.md`](domain-model.md)).
