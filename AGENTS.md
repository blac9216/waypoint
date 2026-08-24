# AGENTS.md

This file provides canonical guidance to coding agents working in this repository.

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

Their project-owned Dockerfiles, orchestration, and PowerShell are migrating into this
repository as the execution layer. Waypoint separates the ASP.NET control plane from
long-lived `compliance-runner` and `download-runner` services; runners claim their own
Postgres jobs and host PowerShell in-process (ADRs 0013/0014). The same Compose
topology deploys on both sides of an air gap — a **connected** instance builds signed
export bundles and **disconnected** instances consume them.

**Current status: architecture realignment before milestone M3 continues.** M1 (foundation + download vertical
slice, epic [#1](https://github.com/blac9216/waypoint/issues/1)) and M2 (sites,
credentials & STIG scan slice, epic [#13](https://github.com/blac9216/waypoint/issues/13))
are both closed, but their backend-hosted execution packaging is being replaced by the
runner topology approved in ADRs 0013–0015. M3 (identity/RBAC/scheduling, epic
[#14](https://github.com/blac9216/waypoint/issues/14)) follows that realignment — see
`docs/roadmap.md` for what's built vs. still planned. Planning (M0) is complete:
architecture, decisions, security model, UI prototype, and the API contract live in
`docs/` — read `docs/architecture.md`, `docs/api-contract.md`, and the ADRs in
`docs/adr/` before building anything, and keep them updated as decisions evolve. Do
not contradict an accepted ADR without recording a superseding one. All work is
issue-driven via the `github-workflow` skill.

## Tech Stack (agreed — see ADRs for rationale)

| Concern | Choice |
|---|---|
| Packaging | Public source + Dockerfiles; operators build with Compose, provision entitled tools, and export their own images/bundles |
| Database | PostgreSQL (app data, JSONB catalogs, job queue, Keycloak DB) |
| Reverse proxy | nginx, operator-provided TLS certs |
| Identity | Keycloak (OIDC; CAC/PIV x.509 + LDAP federation); app is a plain OIDC client |
| Secrets | AES-256-GCM envelope encryption in Postgres; API encrypts writes, trusted runners decrypt claimed-job credentials |
| Backend | ASP.NET Core (C#) control plane: REST/RBAC, enqueue/control, queries, SSE; no job execution |
| Runners | Long-lived .NET Generic Host services; shared C# worker library + in-process PowerShell SDK + domain handlers |
| Job engine | Runner-claimed Postgres queue (`FOR UPDATE SKIP LOCKED`), leases/cancellation/events owned by executing runner |
| Frontend | React + TypeScript PWA, static build, **zero external assets** (air-gap) |
| Execution | Project-owned Dockerfiles/PowerShell migrate from sibling repos into compliance/download runner build contexts |

## Running & Testing — read before `docker compose`

**[`docs/testing.md`](docs/testing.md) is required reading before you run the stack.**

Multiple agents run this stack on the same Docker host concurrently, so every
bring-up needs its own Compose project name and host port — `docs/testing.md` is the
single source of truth for the current isolation recipe (issue #68 removed the
compose file's fixed `container_name:` values, so `-p <name>` alone now isolates
containers/networks/volumes together), how to verify isolation *before* trusting a
result, cleanup discipline, and the two standing verification-honesty rules: never
claim a check you did not execute, and run every Suggested Test Step exactly as
written before posting it. Do not duplicate the recipe here — it drifts; go read it.

## Repository Layout

```
├── AGENTS.md            # Canonical guidance for all coding agents
├── CLAUDE.md            # Claude Code compatibility pointer to AGENTS.md
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
├── backend/             # today: combined API/worker; target: API + shared runner + two runner hosts (ADRs 0013/0014)
├── frontend/            # React + TypeScript PWA (M1/M2 screens delivered; M3 auth/RBAC UI in progress)
├── deploy/              # today: three-service dev stack; target adds runners, updater, and bundle tooling
├── .agents/skills       # Codex discovery link to the repository skills
└── .claude/skills/      # Canonical github-workflow + github-pr-review skill sources
```

## Workflow

- All work is issue-driven via the `github-workflow` skill; PRs are reviewed with the
  `github-pr-review` skill. Consult `github-workflow` before writing code.
- Planning-phase deliverables are docs: keep ADRs short and numbered; supersede rather
  than rewrite accepted ones (see `docs/adr/README.md` for what may be amended).

### Labels are a closed set — never invent one

The **Issue Labels** catalog in the `github-workflow` skill is the only source of
valid project labels, including their names, colours, descriptions, and usage rules.
Applying a label outside that catalog does **not** fail: GitHub silently creates it,
colourless and undescribed, and the taxonomy quietly rots. This has already happened
(`concern:correctness`, `concern:accessibility` — see issue #95).

At most one `concern:*` per item. If none fits, pick the closest and explain in the
body — do **not** coin a new one. If a genuinely new dimension is needed, propose it
in an issue and update the skill's catalog first: the catalog change is authoritative,
and provisioning the GitHub label is its consequence.

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
- **Never project-publish vendor binaries.** Broadcom's `vcf-download-tool` is not
  included in source or immutable runner images. An authenticated operator installs it
  through the appliance from an authorized upstream, local repository, or manual
  upload. Once installed, it is managed appliance state and is included when that
  operator creates an air-gap bundle requiring download functionality (ADR-0015).

## Key Constraints

- Everything must work **fully air-gapped**: no CDN assets, no phone-home, no ACME, no
  external font/icon/package fetches at runtime. Connected-mode features degrade
  gracefully to hidden/disabled when disconnected.
- Scans are read-only and schedulable; **remediation is never schedulable** and always
  requires explicit human confirmation.
- The execution scripts in the sibling repositories are project-owned code and will
  move into this repository with their Dockerfiles and attribution/history preserved.
  Account-gated third-party tools remain operator-installed managed state.
- Operations must be idempotent; individual target failures must not halt a run.
