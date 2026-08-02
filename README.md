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

[Apache-2.0](LICENSE). Third-party code may only be incorporated under the borrowing
policy in [CLAUDE.md](CLAUDE.md) (permissive licenses with attribution; no copyleft;
no vendor binaries).

## Contributing note

This is a **public repository** developed against private infrastructure. No real
hostnames, IPs, credentials, or Broadcom/VMware account data may appear here — see
[CLAUDE.md](CLAUDE.md) for the full sanitization policy. All examples use fictional
placeholders.
