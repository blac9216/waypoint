# Compliance execution parity contract

Status: **planned architecture** for epic
[#726](https://github.com/blac9216/waypoint/issues/726). The shipped M2 scan slice does
not yet implement this contract. This document normalizes the project-owned sibling's
supported scan behavior into Waypoint concepts; it neither copies sibling code/content
nor makes that repository a dependency. [ADR-0022](adr/0022-compliance-catalog-and-content-lifecycle.md)
is the governing decision.

## Closed capability vocabulary

The Waypoint-owned catalog is the sole execution authority. A row is runnable only
when its product, exact product version, content kind, component, selector, transport,
credential purposes, output semantics, and exact active baseline all use the following
reviewed vocabulary. Filesystem paths and profile leaf names are ingestion evidence,
never identity or support declarations.

| Dimension | Closed values and meaning |
|---|---|
| Kind | `stig`: exact profile/XCCDF pair, HDF + CKL; `srg`: profile closure, HDF only |
| Transport | `vmware` (vSphere API), `ssh`, `nsx-api`, `vcf-api` |
| Selector | `vcenter`, `esxi`, `vm`, `target` (whole appliance -- the `ssh / target` rows, no sub-service name), or `service` (a named VCSA/NSX/appliance sub-service) |
| Credential purpose | `vsphere-api`, `vcsa-ssh`, `nsx-api`, `srg-ssh`; `vcf-api` authentication is a catalog requirement whose final purpose is planned under #807 |
| Configuration | declared inputs and per-control Input/Attestation; future Remediation settings remain owned by #15 |
| Priority | NSX STIG 1; VCSA STIG 2; vCenter STIG 3; ESXi STIG 4; VM STIG 5; every SRG 6 |
| Output | STIG: complete exact-baseline HDF and CKL, eligible for direct STIG Manager upload; SRG: HDF only, never CKL/upload |
| Qualification | schema/capability validation, exact baseline, required purposes/inputs, complete dependency closure, and live wrapper integration tests |

Unknown values fail closed and remain visible as quarantined content or unsupported
coverage. Operators cannot add executable plugins, scripts, mappings, or products.

## Sibling source-capability provenance matrix

The entries below enumerate every scan component in the sibling catalog at the source
revision inspected for #805. Component lists are exact; comma-separated service names
are separate execution components. “Profile” is an immutable vendor content revision,
not a directory selected by a user.

This is a provenance inventory, not a list of runnable Waypoint baselines. The sibling
contains **44 scan components in 10 product/version/kind catalog nodes**. The table has
**13 capability-group rows** because the two vSphere nodes are each split by transport
(four rows instead of two) and the VCF node is split by transport (two rows instead of
one). These counts are reproducible by counting `stig`/`srg` component keys and their
parent product/version/kind nodes in `settings/catalog.json`, then counting the table's
body rows. Remediation nodes and components are excluded from all three counts.

A source key marked `family` records only what the sibling claims; it is never a
product version and can never identify an executable baseline. A key marked `exact`
is still source provenance rather than an activation candidate. For every execution,
Waypoint must resolve an exact observed or Admin-configured product version to a
catalog entry for that same exact version, then require the exact approved active
profile baseline (and exact approved XCCDF baseline for STIG). If any exact identity or
approval is absent, ambiguous, or mismatched, that component is unsupported and does
not execute. Source families cannot be copied into executable catalog entries,
expanded by inference, or used for range/nearest-version matching.

| Sibling product/version key | Key form | Kind / source profile revision | Components | Transport / selector | Purpose | Output |
|---|---|---|---|---|---|---|
| vSphere `8-0` | exact | STIG / `v2r3-stig` | vCenter; ESXi; VM | `vmware` / object kind | `vsphere-api` | HDF + CKL |
| vSphere `8-0` | exact | STIG / `v2r3-stig` | VCSA EAM, Lookup, PerfCharts, Photon, PostgreSQL, STS, UI, VAMI, Envoy | `ssh` / named VCSA service | `vsphere-api` + `vcsa-ssh` | HDF + CKL |
| vSphere `9-0` | exact | SRG / `Y26M05-srg` | vCenter; ESXi; VM | `vmware` / object kind | `vsphere-api` | HDF |
| vSphere `9-0` | exact | SRG / `Y26M05-srg` | VCSA Envoy, PostgreSQL, VAMI, Photon | `ssh` / named VCSA service | `vsphere-api` + `vcsa-ssh` | HDF |
| NSX `4-x` | family | STIG / `v1r2-stig` | Manager, distributed firewall, tier-0 firewall, tier-0 router, tier-1 firewall, tier-1 router | `nsx-api` / named function | `nsx-api` | HDF + CKL |
| NSX `9-x` | family | SRG / `Y26M05-srg` | Manager, routing | `nsx-api` / named function | `nsx-api` | HDF |
| Aria Operations `8-x` | family | SRG / `v1r4-srg` | Aria Operations | `ssh` / target | `srg-ssh` | HDF |
| Aria Automation `8-x` | family | SRG / `v1r6-srg` | Aria Automation | `ssh` / target | `srg-ssh` | HDF |
| Aria Suite Lifecycle `8-x` | family | SRG / `v1r2-srg` | Aria Suite Lifecycle | `ssh` / target | `srg-ssh` | HDF |
| Workspace ONE Access `3-3-x` | family | SRG / `v1r3-srg` | Workspace ONE Access | `ssh` / target | `srg-ssh` | HDF |
| Photon OS `5-0` | exact | SRG / `v3r3-srg` | Photon OS | `ssh` / target | `srg-ssh` | HDF |
| VCF `9-x` | family | SRG / `Y26M05-srg` | SDDC Manager nginx, PostgreSQL, Photon; Operations httpd, PostgreSQL, Photon; Operations HCX httpd, Photon; Operations Networks nginx platform, Ubuntu | `ssh` / named service | `srg-ssh` | HDF |
| VCF `9-x` | family | SRG / `Y26M05-srg` | SDDC Manager application; Automation application | `vcf-api` / named service | catalog-declared API purpose (#807) | HDF |

Exact XCCDF identities and mappings for each STIG component are baseline data staged
under ADR-0022, not hard-coded path inference. Version ranges, nearest-version
selection, cross-version evidence, and a scan-time profile picker are prohibited.

## Parity boundary and legacy disposition

Parity includes target/component derivation, transport and credential routing,
declared input/attestation resolution, priority ordering, complete HDF production,
STIG-only exact CKL conversion, and live qualification of the real Waypoint wrapper.
Inventory/plans (#806), jobs/credentials/settings (#807), and trust/evidence/retirement
(#808) supply the later architectural detail.

- [#595](https://github.com/blac9216/waypoint/issues/595) is retained but re-scoped:
  check/diff and metadata parsing feed staged catalog candidates; private/additive
  sources cannot bypass provenance, review, or activation.
- [#625](https://github.com/blac9216/waypoint/issues/625) is retained as parser
  correctness needed by control severity and complete-closure comparison.
- [#567](https://github.com/blac9216/waypoint/issues/567) is retained as source-fetch
  hardening; repository/ref inputs cannot become catalog authority.
- [#650](https://github.com/blac9216/waypoint/issues/650) is architecturally
  superseded: execution must resolve immutable content by baseline digest, so fixed
  paths, mutable profile mounts, and payload fallback have no end-state role. Its
  cleanup/tests remain tracked until implementation removes them.

Unsupported/non-goals: vSphere 7.0; unlisted product versions/components; aggregate
profiles; path-discovered profiles; arbitrary operator extensions; sibling runspaces,
watched-directory upload, or direct folder-copy import; remediation execution (#15);
and CKL/STIG Manager upload for SRGs. None may be guessed into support.
