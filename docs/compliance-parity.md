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

## Recognized on-disk import layouts

`VendorHierarchyInterpreter` (`backend/Waypoint.Core/ComplianceContent/SemanticImport/
VendorHierarchyInterpreter.cs`) is a closed, data-driven family table: it recognizes
ONLY the vendor-repository directory layouts below and quarantines everything else,
never guessing an unrecognized shape into the nearest-looking family (issue #729,
extended by issue #959). `Waypoint.Tests.Core.ComplianceContent.SemanticImport.
LayoutTableParityTests` parses this table directly out of this file and asserts it
against the interpreter's recognized-family table, so the two cannot silently drift
apart again the way they did before issue #959.

| Vendor directory literal | Maps to family | Path shape | Notes |
|---|---|---|---|
| `vsphere` | `vsphere` | `vsphere/<version>/<release>/inspec/<baseline>/[vcenter\|esxi\|vm]` | Object-kind split directly under the baseline directory (vSphere 8.0 and earlier). |
| `vcf` (grouped-baseline) | `vsphere` | `vcf/<version>/<release>/inspec/<baseline>/vsphere/[vcenter\|esx\|vm]` | Issue #1079 (live validation round 11, superseding issue #959's row): upstream `master` nests the 9.x vSphere/vCenter/ESXi/VM baselines under this consolidated tree, but with an EXTRA grouping segment (`vsphere/`) between the baseline directory and the object-kind leaf, and names the ESXi leaf `esx`, not `esxi` (normalized to `esxi` on import). Same vsphere product family and object-kind vocabulary otherwise. |
| `vcf` (grouped-baseline) | `nsx` | `vcf/<version>/<release>/inspec/<baseline>/nsx/<function-name>` | Issue #1079: the same consolidated `vcf/9.x` tree also carries NSX 9.x named-function content under an `nsx/` grouping segment, one level below the baseline directory -- same nsx product family and named-function shape as the top-level `nsx` row below. |
| `vcf` (named-service) | `vcf` | `vcf/<version>/<release>/inspec/<baseline>/<service-name>` | Issue #1079: named VCF-native application/service leaves that sit DIRECTLY under a `vmware-cloud-foundation-*-stig-baseline` directory (no grouping segment) -- the six API/token-authenticated app profiles under the umbrella baseline directory (`application`, `automation`, `operations`, `opshcx`, `opsnet`, `sddcmgr`) import as `vcf-api` transport; every other named service leaf (SDDC Manager nginx/PostgreSQL, Operations httpd/PostgreSQL, Operations HCX httpd, Operations Networks nginx-platform, ...), each under its own distinctly-named baseline directory, imports as `ssh` transport. A profile found AT the baseline directory itself (empty tail) is still the aggregate parent, unchanged from every other family. |
| `vsphere` (object-kind-before-inspec) | `vsphere` | `vsphere/<version>/<release>/<object-kind>/inspec/<baseline>/<leaf>`, `<object-kind>` one of `vcsa`, `vsphere` | Issue #959: the current upstream `vsphere/7.0` and `vsphere/8.0` trees split by object kind ONE segment before `inspec` rather than after the baseline directory. Still the vsphere family; still fails closed if `<object-kind>` is anything other than `vcsa`/`vsphere`. Issue #1064: the two subtrees carry different leaf vocabularies -- the `vsphere` subtree's leaves are `[vcenter\|esxi\|vm]` (`vmware` transport); the `vcsa` subtree's leaves are named VCSA services (`<service-name>`, `ssh` transport, same named-sub-service shape as the top-level `vcsa` row below). |
| `vcsa` | `vsphere` | `vcsa/<version>/<release>/inspec/<baseline>/<service-name>` | Named-sub-service split, `ssh` transport (EAM, Lookup, PostgreSQL, ...). Issue #1064 (owner decision): VCSA services are implied subcomponents of every vCenter appliance, so this literal promotes into the `vsphere` product exactly as `vcf` -> `vsphere` above -- the provenance matrix's vSphere `ssh` rows (`vsphere-api` + `vcsa-ssh`) and #741's catalog-declared service expansion then apply; a separate `vcsa` product is never created. |
| `nsx` | `nsx` | `nsx/<version>/<release>/inspec/<baseline>/<function-name>` | Named-function split, `nsx-api` transport. |
| `photon` | `photon` | `photon/<version>/<release>/inspec/<baseline>` | Whole-appliance, `ssh`/`target` selector. |
| `aria-operations` | `aria-operations` | `aria-operations/<version>/<release>/inspec/<baseline>` | Whole-appliance, `ssh`/`target` selector. |
| `aria-automation` | `aria-automation` | `aria-automation/<version>/<release>/inspec/<baseline>` | Whole-appliance, `ssh`/`target` selector. |
| `aria-suite-lifecycle` | `aria-suite-lifecycle` | `aria-suite-lifecycle/<version>/<release>/inspec/<baseline>` | Whole-appliance, `ssh`/`target` selector. |
| `vidm` | `vidm` | `vidm/<version>/<release>/inspec/<baseline>` | Whole-appliance (Workspace ONE Access), `ssh`/`target` selector. |

Any other top-level directory (`aria` unqualified, `vcd`, `avi`, and anything else not
listed above) is unrecognized and quarantined, never guessed, per the closed-family-
table rule above -- this table only grows when a new row is added here first and the
interpreter's family table is updated to match (issue #959's fix for the defect class
where the interpreter's table drifted from upstream layout with no test catching it).

## Release ordering and newest-release-wins

Issue #986 (owner decision, 2026-08-28): a live round-5 pull showed the upstream
repository shipping MULTIPLE releases of the same (product-version, kind, component)
scope side by side (e.g. `v2r2-stig` and `v2r3-stig` both present under one baseline).
Before this decision the importer collapsed every additional release onto the same
`component_key` and quarantined ALL of them as colliding, rejecting content a lab
actually needs. The resolution: within one declared version scope, the **newest
release** promotes as the pending-approval candidate; every older release quarantines
with an honest `superseded by release '<release>' (profile '<profile-key>')` reason
naming the winner, and the winner itself flows through the normal promotion path (it is
not merely accepted-but-not-promoted). Side-by-side releases occur only on an initial or
long-stale pull, so the operator-facing approval flow is unchanged: whatever is newest
is what shows up pending approval.

Release ordering is a pure, closed two-form parser/comparator
(`Waypoint.Core.ComplianceContent.SemanticImport.VendorReleaseOrder`), never a general
version-string heuristic:

| Form | Shape | Ordering key | Example |
|---|---|---|---|
| `V#R#` | vendor's own STIG/SRG version-and-release numbering, with an optional `-stig`/`-srg` kind suffix | `(major, release)` numeric compare | `v2r3-stig` |
| `Y##M##-srg` | vendor's year/month SRG generation numbering | `(year, month)` numeric compare | `Y26M05-srg` |

A release segment that matches NEITHER closed form fails closed: the whole scope
quarantines every candidate with the `component_key ... collides` reason extended by a
parenthesized diagnostic naming which release could not be parsed, rather than guessing
an order. Two candidates that tie under the same form's ordering (including two
profiles literally sharing one release key) also fail closed -- "newest wins" presumes
a strict order, and a tie is a genuine shape ambiguity, not a supersession; this tie
class predates #986 (it is issue #729's same-release collision), so its reason string
is preserved byte-for-byte with no appended diagnostic.

**Cross-form ruling.** Every documented family in the provenance matrix below uses
exactly one form consistently for a given generation of content (the STIG-era releases
are all `V#R#`; the 9.x/SRG generation is all `Y##M##-srg`) -- the matrix never
documents a scope mixing both forms. Whether a single declared version scope could
genuinely contain a `V#R#` release AND a `Y##M##-srg` release at once is therefore an
open design question this issue deliberately does NOT answer with an invented ordering:
there is no principled way to say a year/month SRG generation is "newer" or "older" than
a vendor STIG revision number. `VendorReleaseOrder.Compare` throws rather than compare
across forms, and the reconciler treats that as a fail-closed collision (reason names
the ambiguity as `cross-form ... issue #986`) for every candidate in that tie, exactly
like an unparseable release. If a future real pull ever proves this scenario occurs, it
needs its own owner decision, not a guess baked into this importer.

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

**Catalog product-version key = the vendor's declared version scope, verbatim**
(issue #998's CORRECTED owner decision, 2026-08-28, superseding an earlier "minor-level
keys" comment posted prematurely on the same issue). The vendor repo is HETEROGENEOUS:
some product trees declare a minor-scoped directory (`vsphere/7.0`, `vsphere/8.0`) and
others declare a major-line-scoped directory (`vcf/9.x`; the vendor's own profile titles
for NSX/Aria/vIDM literally say "9.X"/"8.X"/"3.3.X"). The "Key form" column below records
which shape the sibling itself claims for that row (`exact` = the sibling's own directory
is minor-scoped; `family` = major-line-scoped) -- and the catalog's `Sibling
product/version key` column is now written in the SAME verbatim form the catalog
product-version key actually stores (`8.0`, `9.x`, `3.3.x`, ...), not a Waypoint-invented
patch-level triple. Neither form is a range Waypoint infers or expands: it is exactly
what the vendor's directory name already declares. Matching an observed/configured exact
version against a declared-scope key is a CLOSED TWO-FORM scope test performed at lookup
time (`Waypoint.Core.Components.VersionScopeMatcher`): an observed version matches a
`N.M` key iff it starts with that exact major.minor; it matches a `N.x` (or `N.M.x`) key
iff it starts with that key's concrete leading segment(s). No other key forms are
recognized -- an unrecognized key form, or an unparseable observed version, fails closed
(matches nothing) rather than guessing. This is vendor-declared range identity, never
nearest-version inference: Waypoint still resolves to exactly one declared scope per
execution, then requires the exact approved active profile baseline (and exact approved
XCCDF baseline for STIG) within that scope. If any exact identity or approval is absent,
ambiguous, or mismatched, that component is unsupported and does not execute. Source
families cannot be copied into executable catalog entries, expanded by inference beyond
their own declared scope, or matched by nearest-version substitution.

Issue #1080 (live validation round 11): the vSphere `9.x` rows below were previously
seeded under the exact key `9.0`, modeled on a top-level `vsphere/9.0` vendor directory
that issue #1079 proved does not exist -- the 9.x vSphere content actually lives under
`vcf/9.x`, whose declared scope is the major-line-scoped `9.x`, exactly like the `nsx
9.x` and `vcf 9.x` rows already below. A real VCF 9.1 host discovers `9.1.0`, which the
closed two-form matcher above never matched against the exact `9.0` key, so every
discovered 9.1 component stayed catalog-unlinked. The rows are corrected to `9.x` /
`family` here; migration `0076_vsphere_9x_declared_scope_rekey.sql` re-keys the seed to
match, using this same precedented major-line scope-key form -- not a new key form, not
a version range, not nearest-version fallback.

Hosts store exactly two facts about their own version: the full observed product version
and the build number. The declared-scope key lives only on the catalog side; no third,
derived "scope" fact is ever stored on a host or component row.

Issue #1081 (live validation round 11): the discovered vCenter component now stores the
identical two facts -- its authoritative instance UUID as vendor identity, and the same
observed-version/build pair a host reports, sourced from vSphere's `content.about` -- so
it is never `absent-by-omission`-only-plannable-by-hand the way it was before. A VM's
`inventory_items.build` column, previously overloaded with the VMware Tools version, is
now honestly left unpopulated rather than misrepresented as a platform fact.

Issue #1063 (epic #726 section 3, "the vSphere VM STIG's version scope follows the
platform version"): a VM's own version/build fact is never independently observed --
vSphere exposes no such thing -- so discovery DERIVES it from the same pass's managing
vCenter fact and stamps it onto every VM in bulk, no per-VM configuration. The derived
fact is marked `derived_from_parent = true` in the VM's `discovered_fact` JSONB
(`ComponentFact.DerivedFromParent`) so it is never indistinguishable from a directly
observed fact -- provenance stays visible and snapshotted, per section 3's own rule.
When the parent has no version fact this pass (`content.about` unavailable, or issue
#1115's exact-name-only Enhanced Linked Mode session-match miss), every VM under it
ends the pass version-absent and unlinked: never a guess, and never left holding a
derived fact from an earlier pass. That second half is enforced at the write, not just
at the mapping -- `ComponentRepository.UpsertDiscoveredAsync` normally RETAINS a
component's last known discovered fact and catalog link across a pass that rendered no
version opinion (issue #1000, last-known-good for a fact that was genuinely observed on
that component at least once), but a fact marked `derived_from_parent` is instead
CLEARED, because it is a copy of a parent fact that this pass no longer has. A VM whose
`configured_fact` an Admin set keeps that fact and the link resolved from it; only the
derived discovered fact is cleared. Each VM's vSphere instance UUID (`inventory_items.instance_uuid`,
migration 0079) is recorded alongside its existing moref-keyed identity so identically
named VMs stay deconflictable across discovery passes -- component identity itself
remains keyed on moref, unchanged by this issue.

| Sibling product/version key | Key form | Kind / source profile revision | Components | Transport / selector | Purpose | Output |
|---|---|---|---|---|---|---|
| vSphere `8.0` | exact | STIG / `v2r3-stig` | vCenter; ESXi; VM | `vmware` / object kind | `vsphere-api` | HDF + CKL |
| vSphere `8.0` | exact | STIG / `v2r3-stig` | VCSA EAM, Lookup, PerfCharts, Photon, PostgreSQL, STS, UI, VAMI, Envoy | `ssh` / named VCSA service | `vsphere-api` + `vcsa-ssh` | HDF + CKL |
| vSphere `9.x` | family | SRG / `Y26M05-srg` | vCenter; ESXi; VM | `vmware` / object kind | `vsphere-api` | HDF |
| vSphere `9.x` | family | SRG / `Y26M05-srg` | VCSA Envoy, PostgreSQL, VAMI, Photon | `ssh` / named VCSA service | `vsphere-api` + `vcsa-ssh` | HDF |
| NSX `4.x` | family | STIG / `v1r2-stig` | Manager, distributed firewall, tier-0 firewall, tier-0 router, tier-1 firewall, tier-1 router | `nsx-api` / named function | `nsx-api` | HDF + CKL |
| NSX `9.x` | family | SRG / `Y26M05-srg` | Manager, routing | `nsx-api` / named function | `nsx-api` | HDF |
| Aria Operations `8.x` | family | SRG / `v1r4-srg` | Aria Operations | `ssh` / target | `srg-ssh` | HDF |
| Aria Automation `8.x` | family | SRG / `v1r6-srg` | Aria Automation | `ssh` / target | `srg-ssh` | HDF |
| Aria Suite Lifecycle `8.x` | family | SRG / `v1r2-srg` | Aria Suite Lifecycle | `ssh` / target | `srg-ssh` | HDF |
| Workspace ONE Access `3.3.x` | family | SRG / `v1r3-srg` | Workspace ONE Access | `ssh` / target | `srg-ssh` | HDF |
| Photon OS `5.0` | exact | SRG / `v3r3-srg` | Photon OS | `ssh` / target | `srg-ssh` | HDF |
| VCF `9.x` | family | SRG / `Y26M05-srg` | SDDC Manager nginx, PostgreSQL, Photon; Operations httpd, PostgreSQL, Photon; Operations HCX httpd, Photon; Operations Networks nginx platform, Ubuntu | `ssh` / named service | `srg-ssh` | HDF |
| VCF `9.x` | family | SRG / `Y26M05-srg` | SDDC Manager application; Automation application | `vcf-api` / named service | catalog-declared API purpose (#807) | HDF |

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
