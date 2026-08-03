# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ⚠️ THIS IS A PUBLIC REPOSITORY — SANITIZATION IS MANDATORY

This repository is **public on GitHub**. It is developed against a private lab and a
personal Broadcom/VMware entitlement, and **nothing identifying either may ever be
committed**. Before every commit, verify that the diff contains **none** of the following:

- **Lab hostnames, FQDNs, or IP addresses** (vCenters, ESXi hosts, VMs, NSX managers,
  Aria/vIDM/Photon appliances, STIG Manager instances, DNS/AD domains) — real or partial
- **Credentials of any kind**: passwords, API tokens, session tokens, SSH keys,
  certificates/private keys, Keycloak client secrets, vault passwords or vault files
- **Broadcom/VMware account identifiers**: download tokens, entitlement/site IDs,
  support contract numbers, the account email, depot URLs containing tokens
- **Real config artifacts**: an actual `site.json`, `secrets.vault`, ansible-vault
  content, kubeconfigs, exported inventories, scan results/CKLs/HDF from real systems
  (these embed hostnames, IPs, and MACs)
- **Logs or command output** captured from the lab (log lines embed hostnames and IPs)

Rules of thumb:

- All example data uses obviously fictional placeholders: `vcsa-01.example.internal`,
  `esxi-{01..04}.example.internal`, `192.0.2.0/24` / `198.51.100.0/24` (RFC 5737),
  `user@example.internal`. Never "sanitize" by lightly editing a real value.
- Test fixtures and seed data are invented, never exported.
- If a secret or lab identifier is committed, even briefly: rotating the secret and
  rewriting history are BOTH required — treat it as an incident, tell the user
  immediately, and do not push anything else until resolved.
- When in doubt, leave it out and ask.
- **Local testing**: dev depot tokens and depot configuration are borrowed at runtime
  from the private sibling repo (`vcf-docker-download`) via gitignored paths
  (`dev/local/`, `.env`, `config/` — see `.gitignore`). They are used for testing
  only and are NEVER copied into this repository, echoed into committed fixtures,
  or pasted into logs/docs. Seed data and test fixtures stay invented.

## Project Overview

**Waypoint** is a planned web appliance for DoD users of VMware VCF that unifies two
existing PowerShell/Docker tools behind one UI:

- [`vmware-stig-docker`](https://github.com/blac9216/vmware-stig-docker) — STIG
  compliance scanning (parallel InSpec) and remediation (PowerCLI/Ansible)
- [`vcf-docker-download`](https://github.com/blac9216/vcf-docker-download) — VCF
  artifact download and offline-repository management for air-gapped deployments

Those repos remain the **execution layer**; Waypoint adds the control plane: web UI,
API, job engine, credential store, RBAC, and cross-enclave transfer. One appliance
image deploys on both sides of an air gap — a **connected** instance (all features,
builds signed export bundles) and **disconnected** instances (consume bundles).

**Current status: implementation, milestone M1** (foundation + download vertical
slice — see `docs/roadmap.md`). Planning (M0) is complete: architecture, decisions,
security model, UI prototype, and the API contract live in `docs/` — read
`docs/architecture.md`, `docs/api-contract.md`, and the ADRs in `docs/adr/` before
building anything, and keep them updated as decisions evolve. Do not contradict an
accepted ADR without recording a superseding one. All work is issue-driven via the
`github-workflow` skill.

## Tech Stack (agreed — see ADRs for rationale)

| Concern | Choice |
|---|---|
| Packaging | Docker Compose (v1) → optional Packer-built OVA wrapper (v2) |
| Database | PostgreSQL (app data, JSONB catalogs, job queue, Keycloak DB) |
| Reverse proxy | nginx, operator-provided TLS certs |
| Identity | Keycloak (OIDC; CAC/PIV x.509 + LDAP federation); app is a plain OIDC client |
| Secrets | AES-256-GCM envelope encryption in Postgres (AWX pattern); master key mounted at deploy |
| Backend | ASP.NET Core (C#) hosting PowerShell runspaces in-process via the PowerShell SDK |
| Job engine | Postgres-backed queue (`FOR UPDATE SKIP LOCKED`), priority-aware, SSE log streaming |
| Frontend | React + TypeScript PWA, static build, **zero external assets** (air-gap) |
| Execution | Existing PowerShell modules from the two sibling repos, unmodified where possible |

## Running & Testing — read before `docker compose`

**[`docs/testing.md`](docs/testing.md) is required reading before you run the stack.**

Multiple agents run this stack on the same Docker host concurrently. The compose file
pins explicit `container_name:` values, which Compose does **not** namespace by
project — so `docker compose -p <name> up` does *not* isolate you, and a second stack
silently recreates the first one's containers (issue #68). The failure mode is a
plausible wrong result, not an error: someone else's healthy container answers your
probe, or someone else's recreate fails your run.

`docs/testing.md` carries the isolation recipe (unique project + override file for
container names + unique host port), how to verify isolation *before* trusting a
result, cleanup discipline, and the two standing verification-honesty rules: never
claim a check you did not execute, and run every Suggested Test Step exactly as
written before posting it.

## Repository Layout

```
├── CLAUDE.md            # This file
├── LICENSE              # Apache-2.0 (see License & Borrowing Policy below)
├── docs/
│   ├── architecture.md  # System architecture: components, job engine, modes, update flow
│   ├── api-contract.md  # M0 output: REST resources, SSE events, state machines, schema, data ledger
│   ├── domain-model.md  # Sites, targets, credentials, runs, roles, open questions
│   ├── security.md      # Secrets threat model + mandatory leakage controls
│   ├── roadmap.md       # Build sequencing (what gets built first and why)
│   ├── ui/
│   │   ├── design-brief.md  # Screen inventory, reconciliation notes, data ledger
│   │   └── prototype/       # High-fidelity interactive HTML prototype + design handoff
│   └── adr/             # Architecture Decision Records (numbered, immutable once accepted)
├── backend/             # (skeleton) ASP.NET Core API + job engine + PS hosting
├── frontend/            # (skeleton) React + TypeScript PWA
├── deploy/              # (skeleton) compose file, nginx config, updater, bundle tooling
└── .claude/skills/      # github-workflow + github-pr-review (issue-driven development)
```

## Workflow

- All work is issue-driven via the `github-workflow` skill; PRs are reviewed with the
  `github-pr-review` skill. Consult `github-workflow` before writing code.
- Planning-phase deliverables are docs: keep ADRs short and numbered; supersede rather
  than rewrite accepted ones (see `docs/adr/README.md` for what may be amended).

### Labels are a closed set — never invent one

`scripts/provision-labels.sh` is the **only** source of valid labels, and it has
already been run. Applying a label outside it does **not** fail: GitHub silently
creates it, colourless and undescribed, and the taxonomy quietly rots. This has
already happened (`concern:correctness`, `concern:accessibility` — see issue #95).

| Group | Labels |
|---|---|
| Type | `bug` · `enhancement` · `documentation` · `chore` · `epic` |
| State | `backlog` · `blocked` · `deferred` · `help` |
| Priority | `priority:high` · `priority:medium` · `priority:low` |
| Severity | `severity:critical` · `severity:major` · `severity:minor` |
| Concern | `concern:security` · `concern:tests` · `concern:perf` · `concern:refactor` · `concern:lint` · `concern:style` |

At most one `concern:*` per item. If none fits, pick the closest and explain in the
body — do **not** coin a new one. If a genuinely new dimension is needed, propose it
in an issue and add it to the script first: the script is the change, the label is
the consequence.

## License & Borrowing Policy

Waypoint is licensed **Apache-2.0** (see `LICENSE`). When incorporating third-party
code ("cribbing" from prior art):

- **Allowed**: Apache-2.0, MIT, BSD-2/3, ISC — keep the original copyright/license
  header on the copied file or fragment, **and** add an entry to the root `NOTICE`
  file (it exists; use the documented entry format). Check the actual `LICENSE` in the
  source repo at copy time; do not assume from memory.
- **`NOTICE` is load-bearing, not bookkeeping.** Apache-2.0 §4(d) obliges anyone
  redistributing this work to carry that file forward. An attribution recorded only in
  a commit message, a PR body, or this file does not travel with the code.
- **Not allowed**: GPL/AGPL/LGPL, SSPL, BSL, or unlicensed code — copyleft would
  encumber the whole appliance; no-license means no permission.
- **Never redistribute vendor binaries.** Broadcom's `vcf-download-tool` is **not
  bundled** in the appliance image (decided 2026-08-02) — it is installed at runtime
  via the install flow (local repo / depot fetch / manual upload). The same applies to
  any other vendor-licensed artifact.

## Key Constraints

- Everything must work **fully air-gapped**: no CDN assets, no phone-home, no ACME, no
  external font/icon/package fetches at runtime. Connected-mode features degrade
  gracefully to hidden/disabled when disconnected.
- Scans are read-only and schedulable; **remediation is never schedulable** and always
  requires explicit human confirmation.
- The sibling repos' Broadcom/vendor scripts run as unmodified vendor code — Waypoint
  orchestrates them, it does not fork them.
- Operations must be idempotent; individual target failures must not halt a run.
