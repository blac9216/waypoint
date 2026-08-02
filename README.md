# waypoint

A tool for DoD users of VCF to manage STIG compliance and software downloads in a
friendly web UI with cross-enclave capability.

Waypoint is the control plane that unifies two existing tools behind one appliance:

- [vmware-stig-docker](https://github.com/blac9216/vmware-stig-docker) — STIG scanning
  and remediation for vSphere/NSX/Aria/Photon
- [vcf-docker-download](https://github.com/blac9216/vcf-docker-download) — VCF artifact
  download and offline repository management

One appliance image deploys on both sides of an air gap: a connected instance downloads
software and builds signed export bundles; disconnected instances import them and run
compliance operations locally.

## Status

**Planning / design phase.** No application code yet. Start here:

- [Architecture](docs/architecture.md) — components, job engine, modes, update flow
- [Domain model](docs/domain-model.md) — sites, targets, credentials, roles
- [Security](docs/security.md) — secrets threat model and leakage controls
- [ADRs](docs/adr/) — the decisions and why
- [Roadmap](docs/roadmap.md) — build sequencing
- [UI design brief](docs/ui/design-brief.md) — screen inventory and prototype reconciliation
- [API contract](docs/api-contract.md) — REST/SSE contract, state machines, data ledger (M0 output)

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

Waypoint orchestrates vendor tooling; it does not ship it.

- Broadcom's `vcf-download-tool` is **never bundled** in this repository or in any
  Waypoint appliance image. It is installed at runtime by the operator, from their own
  entitled copy.
- Broadcom's STIG remediation scripts and the VMware DoD compliance content are used as
  **unmodified vendor code**, executed from a repository the operator mounts. They are
  not forked, vendored, or redistributed here.

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
