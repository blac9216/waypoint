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

**Current status: planning/design.** No application code yet. The architecture and all
agreed decisions live in `docs/` — read `docs/architecture.md` and the ADRs in
`docs/adr/` before proposing or building anything, and keep them updated as decisions
evolve. Do not contradict an accepted ADR without recording a superseding one.

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
  than rewrite accepted ones.

## License & Borrowing Policy

Waypoint is licensed **Apache-2.0** (see `LICENSE`). When incorporating third-party
code ("cribbing" from prior art):

- **Allowed**: Apache-2.0, MIT, BSD-2/3, ISC — keep the original copyright/license
  header on copied files or fragments, and note the source in a `NOTICE` file (create
  it on first use). Check the actual `LICENSE` in the source repo at copy time; do not
  assume from memory.
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
