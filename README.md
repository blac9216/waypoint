# Waypoint

[![Backend](https://github.com/blac9216/waypoint/actions/workflows/backend.yml/badge.svg?branch=main)](https://github.com/blac9216/waypoint/actions/workflows/backend.yml)
[![Frontend](https://github.com/blac9216/waypoint/actions/workflows/frontend.yml/badge.svg?branch=main)](https://github.com/blac9216/waypoint/actions/workflows/frontend.yml)
[![Deployment](https://github.com/blac9216/waypoint/actions/workflows/deploy.yml/badge.svg?branch=main)](https://github.com/blac9216/waypoint/actions/workflows/deploy.yml)
[![Sanitization](https://github.com/blac9216/waypoint/actions/workflows/sanitize.yml/badge.svg?branch=main)](https://github.com/blac9216/waypoint/actions/workflows/sanitize.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![React](https://img.shields.io/badge/React-TypeScript-149ECA.svg)](https://react.dev/)
[![Docker Compose](https://img.shields.io/badge/deployment-Docker%20Compose-2496ED.svg)](https://docs.docker.com/compose/)
[![Air-gapped](https://img.shields.io/badge/runtime-air--gapped-success.svg)](docs/architecture.md)

A self-hosted appliance for managing VMware Cloud Foundation compliance, software,
and cross-enclave operations from one web interface.

Waypoint is distributed as source, Dockerfiles, and Compose configuration. Operators
build the appliance in their own environment, acquire entitled tools and content with
their own accounts, and move the resulting capabilities into disconnected enclaves.

## What Waypoint does

- **Maps infrastructure** — organizes sites and targets, validates credentials, and
  caches vCenter inventory for repeatable operations.
- **Runs compliance workflows** — manages profiles, benchmarks, inputs, attestations,
  scans, results, exports, and explicitly authorized remediation across VCF systems.
- **Manages software content** — indexes depots, downloads and verifies artifacts,
  tracks repository presence, and manages content needed by connected and disconnected
  environments.
- **Bridges air gaps** — composes signed transfer bundles containing the selected
  software, compliance content, installed tooling, and appliance updates required on
  the receiving side.
- **Preserves control and evidence** — encrypts stored secrets, enforces role-based
  actions, records job and audit history, streams live progress, and separates import
  from intentional remediation or update application.
- **Deploys as one appliance** — an ASP.NET control plane coordinates dedicated
  compliance and download runners through a durable PostgreSQL job and event boundary.

## Documentation

The documentation records what is implemented, what is planned, and why the system is
designed this way:

- [Architecture](docs/architecture.md) — components, job engine, modes, and update flow
- [Domain model](docs/domain-model.md) — sites, targets, credentials, roles
- [Security](docs/security.md) — secrets threat model and leakage controls
- [ADRs](docs/adr/) — the decisions and why
- [Roadmap](docs/roadmap.md) — build sequencing
- [UI design brief](docs/ui/design-brief.md) — screen inventory and prototype reconciliation
- [API contract](docs/api-contract.md) — REST/SSE contract, state machines, and data ledger
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
- Waypoint's execution services, Dockerfiles, orchestration, and PowerShell are built
  from this repository. Entitlement-restricted executables and compliance content
  remain operator-acquired managed appliance state.

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
