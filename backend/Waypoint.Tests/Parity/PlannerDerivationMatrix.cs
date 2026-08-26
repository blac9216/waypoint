// Copyright 2026 Justin Black
//
// Licensed under the Apache License, Version 2.0 (the "License").
// You may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Waypoint.Core.ComplianceContent;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Machine-readable transcription of docs/compliance-parity.md's provenance matrix,
/// re-purposed for issue #749's PLANNER-PARITY slice (the follow-on to the merged
/// catalog-parity suite, PR #836): given an invented catalog+components+active-baseline
/// setup shaped like each documented family row, <c>ScanPlannerService.CompileAsync</c>
/// (PR #857, issue #734) must yield exactly the expected <em>plan-item expansion</em> --
/// one accepted <c>ScanPlanItem</c> per concrete instance of each family's selector kind
/// (one vCenter, one ESXi host, one VM, one VCSA/NSX/appliance sub-service, ...), with
/// the documented transport, selector, priority/report-group, benchmark identity
/// (STIG-mapped vs. SRG/none), and output kind.
///
/// <b>Honest boundary (issue #749 dispatch instructions):</b> job fan-out remains
/// target-granular until issue #737 closes the queue-fan-out gap (see PR #857's own body,
/// "Target-granular vs. component-granular fan-out": "RunCreationService's job fan-out
/// [is] entirely target-granular, unchanged from #733... implementing component-granular
/// jobs here would be scope creep into [#735-#737]'s] explicitly reserved follow-on
/// work"). This suite therefore asserts PLAN-ITEM expansion -- the planner's own output,
/// which is already component-granular today -- never JOB-row counts, which are a later,
/// separate concern this slice does not touch. Exactly as PR #836 documented its own
/// scope boundary (catalog/importer parity only, not planner/job-count), this file
/// documents its boundary in the same place: here, not silently.
///
/// <b>Scope note on credential purposes (coordination with the in-flight #736 agent):</b>
/// issue #736 is actively changing HOW required credential purposes are derived (moving
/// from a coarse target-kind matrix to catalog-capability + planned-component
/// derivation). This matrix and its tests therefore assert only that
/// <see cref="ScanPlanItem.RequiredPurposes"/> is non-empty and deterministic (sorted)
/// for components that declare a credential requirement in the invented catalog fixture
/// -- never a specific purpose SET beyond what PR #857 itself already froze and tested
/// (<c>ScanPlannerServiceTests</c>'s existing purpose-list assertions, unchanged by this
/// PR). This avoids asserting on #736's active internals.
///
/// All product-version keys, component keys, benchmark ids, and vendor/host identifiers
/// below are INVENTED for this test suite -- shaped like docs/compliance-parity.md's
/// rows, never exported from any real system or the sibling repository (CLAUDE.md
/// sanitization policy).
/// </summary>
public static class PlannerDerivationMatrix
{
	/// <summary>
	/// Rows from the provenance matrix that are documented but not yet automatable by
	/// this planner-parity slice, with the reason -- mirrors
	/// <see cref="CatalogDerivationMatrix.OwnerLiveOnlyRows"/>'s mechanics so
	/// <c>PlannerMatrixCompletenessTests</c> can positively confirm each is a deliberate,
	/// reviewed omission.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> OwnerLiveOnlyRows = new Dictionary<string, string>
	{
		["vcf-9-x-ssh"] =
			"VCF 9-x ssh/named-service row: the underlying catalog-derivation row is itself " +
			"owner-live-only (CatalogDerivationMatrix.OwnerLiveOnlyRows['vcf-9-x-ssh']) because " +
			"VendorHierarchyInterpreter's closed family table does not yet include a 'vcf' " +
			"family. A planner-parity fixture cannot seed a catalog execution profile that " +
			"cannot be imported in the first place; this is #729/#730 catalog-authority " +
			"follow-up work, not this planner-test slice.",
		["vcf-9-x-vcf-api"] =
			"VCF 9-x vcf-api/named-service row: same underlying gap as 'vcf-9-x-ssh' " +
			"(CatalogDerivationMatrix.OwnerLiveOnlyRows['vcf-9-x-vcf-api']) -- the vcf-api " +
			"credential purpose is 'planned under #807' and is not yet closed catalog " +
			"vocabulary, so no execution profile requiring it can be seeded today.",
	};

	/// <summary>The automatable rows, in the same order as docs/compliance-parity.md's table.</summary>
	public static IReadOnlyList<PlannerParityRow> Rows { get; } = BuildRows();

	private static IReadOnlyList<PlannerParityRow> BuildRows()
	{
		return
		[
			// vSphere 8-0 STIG / vmware transport / object-kind selectors: two vCenters,
			// three ESXi hosts, two VMs -- one plan item per concrete instance (epic #726
			// §4 "Each concrete vCenter/ESXi/VM/VCSA service/NSX/SSH component becomes an
			// independent... planned item").
			new PlannerParityRow(
				MatrixRowId: "vsphere-8-0-stig-vmware",
				VendorFamily: "vsphere",
				ProductVersionKey: "8.0.3",
				Kind: CatalogKinds.Stig,
				BenchmarkKey: "invented-vsphere-8-vmware-stig",
				Transport: CatalogTransports.VMware,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				Instances:
				[
					new PlannerParityInstance("vcenter", CatalogSelectorKinds.VCenter, null, "vcenter-stig", 3, ["vsphere-api"], InstanceCount: 2),
					new PlannerParityInstance("esxi", CatalogSelectorKinds.Esxi, null, "esxi-stig", 4, ["vsphere-api"], InstanceCount: 3),
					new PlannerParityInstance("vm", CatalogSelectorKinds.Vm, null, "vm-stig", 5, ["vsphere-api"], InstanceCount: 2),
				]),

			// vSphere 8-0 STIG / ssh transport / named VCSA service selectors: two named
			// services, each present on one VCSA appliance instance in this fixture.
			new PlannerParityRow(
				MatrixRowId: "vsphere-8-0-stig-vcsa-ssh",
				VendorFamily: "vcsa",
				ProductVersionKey: "8.0.3",
				Kind: CatalogKinds.Stig,
				BenchmarkKey: "invented-vcsa-8-ssh-stig",
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				Instances:
				[
					new PlannerParityInstance("eam", CatalogSelectorKinds.Service, "eam", "vcsa-stig", 2, ["vsphere-api", "vcsa-ssh"], InstanceCount: 1),
					new PlannerParityInstance("postgresql", CatalogSelectorKinds.Service, "postgresql", "vcsa-stig", 2, ["vsphere-api", "vcsa-ssh"], InstanceCount: 1),
				]),

			// vSphere 9-0 SRG / vmware transport / object-kind selectors: same shape as the
			// STIG row, no benchmark, HDF-only output, priority 6 (every SRG).
			new PlannerParityRow(
				MatrixRowId: "vsphere-9-0-srg-vmware",
				VendorFamily: "vsphere",
				ProductVersionKey: "9.0.1",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.VMware,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("vcenter", CatalogSelectorKinds.VCenter, null, "srg", 6, ["vsphere-api"], InstanceCount: 1),
					new PlannerParityInstance("esxi", CatalogSelectorKinds.Esxi, null, "srg", 6, ["vsphere-api"], InstanceCount: 2),
					new PlannerParityInstance("vm", CatalogSelectorKinds.Vm, null, "srg", 6, ["vsphere-api"], InstanceCount: 1),
				]),

			// NSX 4-x STIG / nsx-api transport / named function selectors: two functions,
			// one instance each (one NSX Manager, one distributed-firewall function).
			new PlannerParityRow(
				MatrixRowId: "nsx-4-x-stig",
				VendorFamily: "nsx",
				ProductVersionKey: "4.1.2",
				Kind: CatalogKinds.Stig,
				BenchmarkKey: "invented-nsx-4-stig",
				Transport: CatalogTransports.NsxApi,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				Instances:
				[
					new PlannerParityInstance("manager", CatalogSelectorKinds.Service, "manager", "nsx-stig", 1, ["nsx-api"], InstanceCount: 1),
					new PlannerParityInstance("dfw", CatalogSelectorKinds.Service, "dfw", "nsx-stig", 1, ["nsx-api"], InstanceCount: 1),
				]),

			// Aria Operations 8-x SRG / ssh transport / whole-appliance target selector:
			// two appliance instances, each its own plan item (no fabricated sub-service).
			new PlannerParityRow(
				MatrixRowId: "aria-operations-8-x-srg",
				VendorFamily: "aria-operations",
				ProductVersionKey: "8.18.0",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("aria-operations", CatalogSelectorKinds.Target, null, "srg", 6, ["srg-ssh"], InstanceCount: 2),
				]),

			// vSphere 9-0 SRG / ssh transport / named VCSA service selectors: same shape
			// as the STIG vcsa-ssh row, no benchmark, HDF-only.
			new PlannerParityRow(
				MatrixRowId: "vsphere-9-0-srg-vcsa-ssh",
				VendorFamily: "vcsa",
				ProductVersionKey: "9.0.1",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("envoy", CatalogSelectorKinds.Service, "envoy", "srg", 6, ["vsphere-api", "vcsa-ssh"], InstanceCount: 1),
					new PlannerParityInstance("vami", CatalogSelectorKinds.Service, "vami", "srg", 6, ["vsphere-api", "vcsa-ssh"], InstanceCount: 1),
				]),

			// NSX 9-x SRG / nsx-api transport / named function selectors: same shape as
			// the NSX STIG row, no benchmark, HDF-only, priority 6 (every SRG).
			new PlannerParityRow(
				MatrixRowId: "nsx-9-x-srg",
				VendorFamily: "nsx",
				ProductVersionKey: "9.1.0",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.NsxApi,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("manager", CatalogSelectorKinds.Service, "manager", "srg", 6, ["nsx-api"], InstanceCount: 1),
					new PlannerParityInstance("routing", CatalogSelectorKinds.Service, "routing", "srg", 6, ["nsx-api"], InstanceCount: 2),
				]),

			// Aria Automation 8-x SRG / ssh transport / whole-appliance target selector.
			new PlannerParityRow(
				MatrixRowId: "aria-automation-8-x-srg",
				VendorFamily: "aria-automation",
				ProductVersionKey: "8.18.0",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("aria-automation", CatalogSelectorKinds.Target, null, "srg", 6, ["srg-ssh"], InstanceCount: 1),
				]),

			// Aria Suite Lifecycle 8-x SRG / ssh transport / whole-appliance target selector.
			new PlannerParityRow(
				MatrixRowId: "aria-suite-lifecycle-8-x-srg",
				VendorFamily: "aria-suite-lifecycle",
				ProductVersionKey: "8.18.0",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("aria-suite-lifecycle", CatalogSelectorKinds.Target, null, "srg", 6, ["srg-ssh"], InstanceCount: 1),
				]),

			// Workspace ONE Access 3-3-x SRG / ssh transport / whole-appliance target selector.
			new PlannerParityRow(
				MatrixRowId: "vidm-3-3-x-srg",
				VendorFamily: "vidm",
				ProductVersionKey: "3.3.7",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("vidm", CatalogSelectorKinds.Target, null, "srg", 6, ["srg-ssh"], InstanceCount: 1),
				]),

			// Photon OS 5-0 SRG / ssh transport / whole-appliance target selector.
			new PlannerParityRow(
				MatrixRowId: "photon-5-0-srg",
				VendorFamily: "photon",
				ProductVersionKey: "5.0.0",
				Kind: CatalogKinds.Srg,
				BenchmarkKey: null,
				Transport: CatalogTransports.Ssh,
				OutputKind: CatalogOutputKinds.Hdf,
				Instances:
				[
					new PlannerParityInstance("photon", CatalogSelectorKinds.Target, null, "srg", 6, ["srg-ssh"], InstanceCount: 3),
				]),
		];
	}
}

/// <summary>
/// One documented family's per-selector-kind instance shape: how many concrete
/// components of this kind the invented fixture seeds, and the expected per-item tuple
/// every resulting <c>ScanPlanItem</c> must match (docs/compliance-parity.md's Transport/
/// Selector/Priority/Purpose/Output columns for this row, at plan-item granularity).
/// </summary>
public sealed record PlannerParityInstance(
	string ComponentKey,
	string SelectorKind,
	string? SelectorName,
	string ReportGroupKey,
	int Priority,
	string[] CredentialPurposes,
	int InstanceCount);

/// <summary>
/// One row of the planner-parity matrix: one documented capability-group family, split
/// into one-or-more <see cref="PlannerParityInstance"/> selector-kind groups, each seeded
/// with a caller-chosen number of concrete component instances so the test can assert
/// the planner expands to exactly that many accepted <c>ScanPlanItem</c>s per group.
/// </summary>
public sealed record PlannerParityRow(
	string MatrixRowId,
	string VendorFamily,
	string ProductVersionKey,
	string Kind,
	string? BenchmarkKey,
	string Transport,
	string OutputKind,
	IReadOnlyList<PlannerParityInstance> Instances)
{
	/// <summary>Total concrete component instances (and therefore expected accepted plan items) across every selector-kind group in this row.</summary>
	public int TotalInstanceCount => Instances.Sum(i => i.InstanceCount);
}
