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
/// Machine-readable transcription of docs/compliance-parity.md's "Sibling
/// source-capability provenance matrix" -- the single source of truth for issue #749's
/// first slice (catalog/importer parity). Every row below corresponds 1:1 to one body row
/// of that markdown table (13 capability-group rows, matching the doc's own reproducible
/// count: "13 capability-group rows because the two vSphere nodes are each split by
/// transport ... and the VCF node is split by transport").
///
/// This is a TEST-ONLY transcription -- never catalog authority (ADR-0022: the real
/// catalog is hand-curated and appliance-shipped, e.g. issue #729/#730/#731's pipeline).
/// It exists solely so <see cref="CatalogParityContractTests"/> can assert the full
/// derived tuple per row and so <see cref="ParityMatrixCompletenessTests"/> can prove
/// every documented row is either covered here or explicitly named in
/// <see cref="OwnerLiveOnlyRows"/> with a rationale (issue #749 AC).
///
/// All product-version keys, component keys, benchmark ids, and manifest content below
/// are INVENTED for this test suite -- shaped like docs/compliance-parity.md's rows, never
/// exported from any real system or the sibling repository (CLAUDE.md sanitization
/// policy).
/// </summary>
public static class CatalogDerivationMatrix
{
	/// <summary>
	/// Rows from the provenance matrix that are documented but not yet automatable by
	/// this slice, with the reason. Kept here (not silently dropped) so
	/// <see cref="ParityMatrixCompletenessTests"/> can positively confirm each is a
	/// deliberate, reviewed omission rather than test drift -- issue #749 AC "every row
	/// is covered OR explicitly marked owner-live-only with rationale".
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> OwnerLiveOnlyRows = new Dictionary<string, string>
	{
		["vcf-9-x-ssh"] =
			"VCF 9-x ssh/named-service row (SDDC Manager nginx/PostgreSQL/Photon; Operations " +
			"httpd/PostgreSQL/Photon; Operations HCX httpd/Photon; Operations Networks nginx " +
			"platform/Ubuntu): VendorHierarchyInterpreter's closed family table (PR #823) does not " +
			"yet include a 'vcf' family -- only vsphere/vcsa/nsx/photon/aria-*/vidm are recognized. " +
			"Adding it is #729/#730 catalog-authority follow-up work, not this contract-test slice.",
		["vcf-9-x-vcf-api"] =
			"VCF 9-x vcf-api/named-service row (SDDC Manager application; Automation application): " +
			"docs/compliance-parity.md itself states the vcf-api credential purpose is 'planned under " +
			"#807' and PR #822's body confirms catalog_credential_requirements' CHECK constraint " +
			"intentionally excludes vcf-api today. This row cannot derive a real credential-purpose " +
			"tuple until that purpose is approved catalog vocabulary.",
	};

	/// <summary>
	/// The automatable rows, in the same order as docs/compliance-parity.md's table.
	/// </summary>
	public static IReadOnlyList<CatalogParityRow> Rows { get; } = BuildRows();

	private static IReadOnlyList<CatalogParityRow> BuildRows()
	{
		return
		[
			// Row: vSphere 8-0 / STIG / v2r3-stig / vmware transport / object-kind selector.
			new CatalogParityRow(
				MatrixRowId: "vsphere-8-0-stig-vmware",
				ProductVersionKey: "8.0",
				VendorFamily: "vsphere",
				Kind: CatalogKinds.Stig,
				ReleaseKey: "v2r3-stig",
				Transport: CatalogTransports.VMware,
				SelectorKind: CatalogSelectorKinds.VCenter, // per-component override below
				ReportGroupPriority: 3, // overridden per component (vcenter=3, esxi=4, vm=5)
				ReportGroupKey: "vcenter-stig",
				HasBenchmark: true,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				RemediationSupported: true,
				Components:
				[
					new CatalogParityComponent("vcenter", "vCenter Server", null, [Purpose.VSphereApi],
						ReportGroupKeyOverride: "vcenter-stig", ReportGroupPriorityOverride: 3, SelectorKindOverride: CatalogSelectorKinds.VCenter),
					new CatalogParityComponent("esxi", "ESXi Host", null, [Purpose.VSphereApi],
						ReportGroupKeyOverride: "esxi-stig", ReportGroupPriorityOverride: 4, SelectorKindOverride: CatalogSelectorKinds.Esxi),
					new CatalogParityComponent("vm", "Virtual Machine", null, [Purpose.VSphereApi],
						ReportGroupKeyOverride: "vm-stig", ReportGroupPriorityOverride: 5, SelectorKindOverride: CatalogSelectorKinds.Vm),
				]),

			// Row: vSphere 8-0 / STIG / v2r3-stig / ssh transport / named VCSA service selector.
			new CatalogParityRow(
				MatrixRowId: "vsphere-8-0-stig-vcsa-ssh",
				ProductVersionKey: "8.0",
				VendorFamily: "vcsa",
				Kind: CatalogKinds.Stig,
				ReleaseKey: "v2r3-stig",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Service,
				ReportGroupPriority: 2,
				ReportGroupKey: "vcsa-stig",
				HasBenchmark: true,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				RemediationSupported: true,
				Components:
				[
					new CatalogParityComponent("eam", "VCSA EAM Service", "eam", [Purpose.VSphereApi, Purpose.VcsaSsh]),
					new CatalogParityComponent("postgresql", "VCSA PostgreSQL Service", "postgresql", [Purpose.VSphereApi, Purpose.VcsaSsh]),
				]),

			// Row: vSphere 9-0 / SRG / Y26M05-srg / vmware transport / object-kind selector.
			new CatalogParityRow(
				MatrixRowId: "vsphere-9-0-srg-vmware",
				ProductVersionKey: "9.0",
				VendorFamily: "vsphere",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "Y26M05-srg",
				Transport: CatalogTransports.VMware,
				SelectorKind: CatalogSelectorKinds.VCenter,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components:
				[
					new CatalogParityComponent("vcenter", "vCenter Server", null, [Purpose.VSphereApi], SelectorKindOverride: CatalogSelectorKinds.VCenter),
					new CatalogParityComponent("esxi", "ESXi Host", null, [Purpose.VSphereApi], SelectorKindOverride: CatalogSelectorKinds.Esxi),
					new CatalogParityComponent("vm", "Virtual Machine", null, [Purpose.VSphereApi], SelectorKindOverride: CatalogSelectorKinds.Vm),
				]),

			// Row: vSphere 9-0 / SRG / Y26M05-srg / ssh transport / named VCSA service selector.
			new CatalogParityRow(
				MatrixRowId: "vsphere-9-0-srg-vcsa-ssh",
				ProductVersionKey: "9.0",
				VendorFamily: "vcsa",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "Y26M05-srg",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Service,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components:
				[
					new CatalogParityComponent("envoy", "VCSA Envoy Service", "envoy", [Purpose.VSphereApi, Purpose.VcsaSsh]),
					new CatalogParityComponent("vami", "VCSA VAMI Service", "vami", [Purpose.VSphereApi, Purpose.VcsaSsh]),
				]),

			// Row: NSX 4-x / STIG / v1r2-stig / nsx-api transport / named function selector.
			new CatalogParityRow(
				MatrixRowId: "nsx-4-x-stig",
				ProductVersionKey: "4.x",
				VendorFamily: "nsx",
				Kind: CatalogKinds.Stig,
				ReleaseKey: "v1r2-stig",
				Transport: CatalogTransports.NsxApi,
				SelectorKind: CatalogSelectorKinds.Service,
				ReportGroupPriority: 1,
				ReportGroupKey: "nsx-stig",
				HasBenchmark: true,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				RemediationSupported: true,
				Components:
				[
					new CatalogParityComponent("manager", "NSX Manager", "manager", [Purpose.NsxApi]),
					new CatalogParityComponent("dfw", "NSX Distributed Firewall", "dfw", [Purpose.NsxApi]),
				]),

			// Row: NSX 9-x / SRG / Y26M05-srg / nsx-api transport / named function selector.
			new CatalogParityRow(
				MatrixRowId: "nsx-9-x-srg",
				ProductVersionKey: "9.x",
				VendorFamily: "nsx",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "Y26M05-srg",
				Transport: CatalogTransports.NsxApi,
				SelectorKind: CatalogSelectorKinds.Service,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components:
				[
					new CatalogParityComponent("manager", "NSX Manager", "manager", [Purpose.NsxApi]),
					new CatalogParityComponent("routing", "NSX Routing", "routing", [Purpose.NsxApi]),
				]),

			// Row: Aria Operations 8-x / SRG / v1r4-srg / ssh transport / whole-appliance target selector.
			new CatalogParityRow(
				MatrixRowId: "aria-operations-8-x-srg",
				ProductVersionKey: "8.x",
				VendorFamily: "aria-operations",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "v1r4-srg",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Target,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components: [new CatalogParityComponent("aria-operations", "Aria Operations", null, [Purpose.SrgSsh])]),

			// Row: Aria Automation 8-x / SRG / v1r6-srg / ssh transport / whole-appliance target selector.
			new CatalogParityRow(
				MatrixRowId: "aria-automation-8-x-srg",
				ProductVersionKey: "8.x",
				VendorFamily: "aria-automation",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "v1r6-srg",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Target,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components: [new CatalogParityComponent("aria-automation", "Aria Automation", null, [Purpose.SrgSsh])]),

			// Row: Aria Suite Lifecycle 8-x / SRG / v1r2-srg / ssh transport / whole-appliance target selector.
			new CatalogParityRow(
				MatrixRowId: "aria-suite-lifecycle-8-x-srg",
				ProductVersionKey: "8.x",
				VendorFamily: "aria-suite-lifecycle",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "v1r2-srg",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Target,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components: [new CatalogParityComponent("aria-suite-lifecycle", "Aria Suite Lifecycle", null, [Purpose.SrgSsh])]),

			// Row: Workspace ONE Access 3-3-x / SRG / v1r3-srg / ssh transport / whole-appliance target selector.
			new CatalogParityRow(
				MatrixRowId: "vidm-3-3-x-srg",
				ProductVersionKey: "3.3.x",
				VendorFamily: "vidm",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "v1r3-srg",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Target,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: false,
				Components: [new CatalogParityComponent("vidm", "Workspace ONE Access", null, [Purpose.SrgSsh])]),

			// Row: Photon OS 5-0 / SRG / v3r3-srg / ssh transport / whole-appliance target selector.
			new CatalogParityRow(
				MatrixRowId: "photon-5-0-srg",
				ProductVersionKey: "5.0",
				VendorFamily: "photon",
				Kind: CatalogKinds.Srg,
				ReleaseKey: "v3r3-srg",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Target,
				ReportGroupPriority: 6,
				ReportGroupKey: "srg",
				HasBenchmark: false,
				OutputKind: CatalogOutputKinds.Hdf,
				RemediationSupported: true, // invented: Photon SRG has a reversible sshd_config-shaped remediation
				Components: [new CatalogParityComponent("photon", "Photon OS", null, [Purpose.SrgSsh])]),
		];
	}

	private static class Purpose
	{
		public const string VSphereApi = "vsphere-api";
		public const string VcsaSsh = "vcsa-ssh";
		public const string NsxApi = "nsx-api";
		public const string SrgSsh = "srg-ssh";
	}
}
