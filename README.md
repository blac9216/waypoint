# waypoint

A tool for DoD users of VCF to manage STIG compliance and software downloads in a
friendly web UI with cross-enclave capability.

Waypoint is a control plane plus dedicated execution runners that unifies two existing
tools behind one appliance:

- [vmware-stig-docker](https://github.com/blac9216/vmware-stig-docker) — STIG scanning
  and remediation for vSphere/NSX/Aria/Photon
- [vcf-docker-download](https://github.com/blac9216/vcf-docker-download) — VCF artifact
  download and offline repository management

The public project distributes source, Dockerfiles, and Compose configuration — not
completed images or entitled tools. An operator builds the appliance on the connected
side, installs account-gated tools through the UI, and exports locally built images,
required tooling, and selected content for disconnected instances.

## Status

**In active implementation and architecture realignment.** Planning (M0) closed 2026-08-02; the foundation and
download vertical slice (M1, epic [#1](https://github.com/blac9216/waypoint/issues/1))
and the sites/credentials/STIG-scan slice (M2, epic
[#13](https://github.com/blac9216/waypoint/issues/13)) are both closed and live-stack
validated at their original scope. ADRs 0013–0015 now separate the ASP.NET control
plane from compliance/download runners and establish operator-built/exported
packaging; the implementation has not yet been migrated. M3 — identity via Keycloak, RBAC enforcement, and scheduling (epic
[#14](https://github.com/blac9216/waypoint/issues/14)) — is next; see
[roadmap.md](docs/roadmap.md) for what that leaves built vs. planned. Start here:

- [Architecture](docs/architecture.md) — components, job engine, modes, update flow
  (status-annotated: built vs. planned)
- [Domain model](docs/domain-model.md) — sites, targets, credentials, roles
- [Security](docs/security.md) — secrets threat model and leakage controls
- [ADRs](docs/adr/) — the decisions and why
- [Roadmap](docs/roadmap.md) — build sequencing
- [UI design brief](docs/ui/design-brief.md) — screen inventory and prototype reconciliation
- [API contract](docs/api-contract.md) — REST/SSE contract, state machines, data ledger (M0 output)
- [Testing](docs/testing.md) — required reading before running the Compose stack

## License

Copyright 2026 Justin Black. Licensed under the [Apache License, Version 2.0](LICENSE).

Third-party code may only be incorporated under the borrowing policy in
[CLAUDE.md](CLAUDE.md) — permissive licenses with attribution, no copyleft, no vendor
binaries — and is recorded in [NOTICE](NOTICE).

### No warranty

Waypoint scans and, when explicitly instructed, **modifies production infrastructure**.
It is provided "AS IS", without warranties or conditions of any kind, and without
liability, as set out in sections 7 and 8 of the License. Remediation is destructive by
design: review what a run will change before you confirm it.

### Vendor software is not redistributed

Waypoint orchestrates tools an operator is authorized to acquire; the project does not
publish completed images or entitlement-restricted artifacts.

- Broadcom's `vcf-download-tool` is absent from the public source repository and runner
  build. A connected operator installs it through Waypoint using their own authorized
  upstream or a local/manual source; operator-created transfer bundles carry required
  installed tooling across the operator's air gap.
- Project-owned Dockerfiles, orchestration, and PowerShell migrate from the two sibling
  repositories into Waypoint. Entitlement-restricted executables and compliance
  content remain operator-acquired managed appliance state.

Obtaining these under your own entitlement, and complying with their licenses, is your
responsibility as the operator.

### Trademarks

"VMware", "vSphere", "VCF", "vCenter", "ESXi", "NSX", "Aria", "Photon" and "Broadcom"
are trademarks of Broadcom Inc. and/or its subsidiaries. Other product and company
names are the marks of their respective owners.

They are used here **only to describe the systems this software interoperates with**,
which is nominative use. This project is not affiliated with, endorsed by, sponsored by,
or supported by Broadcom Inc., VMware, or any government agency. Section 6 of the
License grants no trademark rights.

## Contributing note

This is a **public repository** developed against private infrastructure. No real
hostnames, IPs, credentials, or Broadcom/VMware account data may appear here — see
[CLAUDE.md](CLAUDE.md) for the full sanitization policy. All examples use fictional
placeholders.
