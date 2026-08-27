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
/// Issue #749 EXECUTION-PARITY slice (epic #726), the follow-on to the merged catalog-
/// parity (PR #836) and planner-parity (PR #869) suites, now that all four transport
/// families are merged (PRs #907 vmware/vcenter, #914 vmware esxi/vm, #915 ssh/VCSA+SRG,
/// #916 nsx-api). Each <see cref="ExecutionParityRow"/> below asserts one documented
/// family's COMMAND-CONSTRUCTION shape: which PowerShell invocation
/// <see cref="Waypoint.Infrastructure.Execution.Scans.ScanJobHandler"/> issues for that
/// transport, whether/how the invocation is narrowed to a selector, what the resolved
/// activated-revision profile path looks like, input-file ordering (operator-authored
/// config-doc inputs first, platform/auth-generated inputs last -- issue #911/#742),
/// which credential purpose is consumed, and what output kind is produced.
///
/// <b>Honest boundary:</b> this suite drives the REAL <c>ScanJobHandler</c> against a
/// real Postgres-backed catalog/baseline/target/credential graph (the same seeding
/// helpers <c>ScanJobHandlerEndToEndTests</c> established), but substitutes a fake
/// <see cref="Waypoint.Core.PowerShell.IPowerShellExecutor"/> for the real PowerShell
/// runspace pool -- the SAME "in-memory fake executor" idiom
/// <c>ContentPullJobHandlerTests.FakePowerShellExecutor</c> already established for unit-
/// level command-construction assertions, applied here to a Postgres-backed handler so
/// the resolved profile path/credential/input-file plumbing stays real while the actual
/// `inspec`/PowerShell-module boundary is captured rather than executed. This proves
/// exactly what command WOULD be issued -- command name, parameter dictionary, ordering
/// of any config-doc-derived inputs relative to platform-generated ones -- without
/// needing a real InSpec binary or a real vCenter/NSX Manager/ssh host. Live wrapper
/// execution against the real shipped PowerShell modules remains
/// <c>ScanJobHandlerEndToEndTests</c>' own scope (stub-module Write-Information echoing)
/// plus the owner-run live-lab acceptance pass documented in docs/testing.md; this suite
/// does not replace either.
///
/// All product-version keys, component keys, and vendor/host identifiers below are
/// INVENTED for this test suite -- shaped like docs/compliance-parity.md's rows, never
/// exported from any real system or the sibling repository (CLAUDE.md sanitization
/// policy).
/// </summary>
public static class ExecutionDerivationMatrix
{
	/// <summary>
	/// Rows from the provenance matrix that are documented but not yet automatable by
	/// this execution-parity slice, with the reason -- mirrors
	/// <see cref="CatalogDerivationMatrix.OwnerLiveOnlyRows"/>/
	/// <see cref="PlannerDerivationMatrix.OwnerLiveOnlyRows"/>'s mechanics.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> OwnerLiveOnlyRows = new Dictionary<string, string>
	{
		["vcf-9-x-ssh"] =
			"VCF 9-x ssh/named-service row: the underlying catalog-derivation row is itself " +
			"owner-live-only (CatalogDerivationMatrix.OwnerLiveOnlyRows['vcf-9-x-ssh']) because " +
			"VendorHierarchyInterpreter's closed family table does not yet include a 'vcf' " +
			"family, so no execution profile for it can be seeded to drive ScanJobHandler at all.",
		["vcf-9-x-vcf-api"] =
			"VCF 9-x vcf-api/named-service row: no runner path consumes the vcf-api transport " +
			"yet (ScanComponentNarrowing's own doc comment: 'no runner path consumes it yet') -- " +
			"ScanJobHandler has no branch to exercise for this transport. Issue #977 resolved the " +
			"vcf-api credential purpose into closed catalog vocabulary and seeded the catalog row, " +
			"but that is identity/capability data only; this execution-parity slice's gap remains " +
			"the missing runner-side transport handler, unrelated to credential-purpose closure.",
		["aria-operations-8-x-srg"] =
			"Whole-appliance ssh/target SRG row (Aria Operations): command-construction for " +
			"the ssh/target selector is already covered by this matrix's other ssh/target row " +
			"(vidm-3-3-x-srg) and by CatalogDerivationMatrix/PlannerDerivationMatrix's own " +
			"per-family coverage -- ScanJobHandler's ssh invocation path (Invoke-WaypointSrgScan) " +
			"branches on transport/selector-kind/sudo, never on vendor family, so a second " +
			"ssh/target fixture would duplicate the SAME command-construction assertions this " +
			"suite already runs for vidm-3-3-x-srg without proving anything new about invocation " +
			"shape. Live acceptance against the real Aria Operations wrapper remains an " +
			"owner-run docs/testing.md step, same as every product this repo cannot install.",
		["aria-automation-8-x-srg"] =
			"Same rationale as 'aria-operations-8-x-srg' above -- ssh/target command " +
			"construction is transport/selector-driven, not vendor-driven, and is already " +
			"proven by vidm-3-3-x-srg.",
		["aria-suite-lifecycle-8-x-srg"] =
			"Same rationale as 'aria-operations-8-x-srg' above.",
		["photon-5-0-srg"] =
			"Same rationale as 'aria-operations-8-x-srg' above -- ssh/target command " +
			"construction is transport/selector/sudo-driven; sudo-disabled is already exercised " +
			"by vidm-3-3-x-srg's own row, and Photon's sudo-enabled/passwordless shape is a " +
			"credential-tier concern (ResolveCredentialAsync's SudoEnabled field), not a " +
			"per-vendor command-construction difference this suite has not already asserted.",
		["vsphere-9-0-srg-vmware"] =
			"vSphere 9-0 SRG / vmware transport row: ScanJobHandler's vmware invocation branch " +
			"(Invoke-WaypointScan, SelectorKind/SelectorName narrowing, the resolved-Input " +
			"InputsFilePath channel) is driven entirely by transport + selector kind, never by " +
			"output_kind/catalog kind -- the STIG vs. SRG distinction changes ONLY the post-scan " +
			"pipeline routing (attest -> convert -> upload vs. attest -> done, already asserted " +
			"directly by this suite's ScanJobHandler_FamilyRow_InvokesDocumentedCommandWithDocumentedShape " +
			"theory reading each row's own OutputKind), not the scan command itself. This matrix's " +
			"'vsphere-8-0-stig-vmware-*' rows already prove the vmware invocation shape for every " +
			"selector kind (vcenter/esxi/vm); an SRG sibling would duplicate those same " +
			"command-construction assertions under a different output_kind that the terminal-state " +
			"assertion already covers row-by-row.",
		["vsphere-9-0-srg-vcsa-ssh"] =
			"vSphere 9-0 SRG / ssh transport (named VCSA service) row: same rationale as " +
			"'vsphere-9-0-srg-vmware' above, one transport over -- Invoke-WaypointSrgScan's " +
			"invocation shape is transport/selector/sudo-driven, already proven by this matrix's " +
			"'vsphere-8-0-stig-vcsa-ssh-service' row; the SRG sibling changes only OutputKind, " +
			"which the shared theory already reads per-row.",
		["nsx-9-x-srg"] =
			"NSX 9-x SRG / nsx-api transport row: same rationale one transport further -- " +
			"Invoke-WaypointNsxScan's invocation shape (Manager/session-token acquisition/" +
			"SelectorName-for-diagnostics) is identical for STIG and SRG NSX components, already " +
			"proven by this matrix's 'nsx-4-x-stig-service' row; the SRG sibling changes only " +
			"OutputKind. (Also note NSX 9.x additionally carries the #917-gated auth leg for " +
			"real live-lab validation, which is owner-live-only for a DIFFERENT, deeper reason " +
			"than this row's command-construction shape -- see docs/testing.md's VCFDT/live-lab " +
			"discipline; this allow-list entry is about command-construction duplication only.)",
	};

	/// <summary>The automatable rows.</summary>
	public static IReadOnlyList<ExecutionParityRow> Rows { get; } = BuildRows();

	private static IReadOnlyList<ExecutionParityRow> BuildRows()
	{
		return
		[
			// vSphere 8-0 STIG / vmware transport / vcenter selector: NARROWED
			// (SelectorKind carried, no SelectorName -- the whole vCenter IS the object),
			// no --input-file selector-scope narrowing key beyond vsphereSelectorKind.
			new ExecutionParityRow(
				MatrixRowId: "vsphere-8-0-stig-vmware-vcenter",
				Transport: CatalogTransports.VMware,
				SelectorKind: CatalogSelectorKinds.VCenter,
				SelectorName: null,
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				CredentialPurpose: "vsphere-api",
				ExpectedCommand: "Invoke-WaypointScan",
				ExpectedParameterKeys: ["VCenter", "Username", "Password", "ProfilePath", "ReportPath", "TimeoutSeconds", "SelectorKind"],
				CarriesSelectorName: false),

			// vSphere 8-0 STIG / vmware transport / esxi selector: NARROWED, SelectorName
			// carried (the object identity narrows the vmware:// invocation).
			new ExecutionParityRow(
				MatrixRowId: "vsphere-8-0-stig-vmware-esxi",
				Transport: CatalogTransports.VMware,
				SelectorKind: CatalogSelectorKinds.Esxi,
				SelectorName: "esxi-01",
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				CredentialPurpose: "vsphere-api",
				ExpectedCommand: "Invoke-WaypointScan",
				ExpectedParameterKeys: ["VCenter", "Username", "Password", "ProfilePath", "ReportPath", "TimeoutSeconds", "SelectorKind", "SelectorName"],
				CarriesSelectorName: true),

			// vSphere 8-0 STIG / vmware transport / vm selector: same shape as esxi.
			new ExecutionParityRow(
				MatrixRowId: "vsphere-8-0-stig-vmware-vm",
				Transport: CatalogTransports.VMware,
				SelectorKind: CatalogSelectorKinds.Vm,
				SelectorName: "vm-01",
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				CredentialPurpose: "vsphere-api",
				ExpectedCommand: "Invoke-WaypointScan",
				ExpectedParameterKeys: ["VCenter", "Username", "Password", "ProfilePath", "ReportPath", "TimeoutSeconds", "SelectorKind", "SelectorName"],
				CarriesSelectorName: true),

			// vSphere 8-0 STIG / ssh transport / named VCSA service selector: SRG-shaped
			// command (Invoke-WaypointSrgScan), sudo disabled (VCSA service credentials
			// carry no sudo flag in this suite's fixture), credential purpose vcsa-ssh.
			new ExecutionParityRow(
				MatrixRowId: "vsphere-8-0-stig-vcsa-ssh-service",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Service,
				SelectorName: "sts",
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				CredentialPurpose: "vcsa-ssh",
				ExpectedCommand: "Invoke-WaypointSrgScan",
				ExpectedParameterKeys: ["SshHost", "Username", "Password", "ProfilePath", "ReportPath", "TimeoutSeconds", "Sudo", "SudoRequiresPassword"],
				CarriesSelectorName: false), // SelectorName never rides the ssh invocation's own parameters -- it scopes which activated profile/credential resolve, not an Invoke-WaypointSrgScan argument.

			// NSX 4-x STIG / nsx-api transport / named function selector: NSX-shaped
			// command (Invoke-WaypointNsxScan), SelectorName carried for
			// logging/diagnostics only (docs/compliance-parity.md "NSX ... named
			// function"), credential purpose nsx-api.
			new ExecutionParityRow(
				MatrixRowId: "nsx-4-x-stig-service",
				Transport: CatalogTransports.NsxApi,
				SelectorKind: CatalogSelectorKinds.Service,
				SelectorName: "manager",
				OutputKind: CatalogOutputKinds.HdfAndCkl,
				CredentialPurpose: "nsx-api",
				ExpectedCommand: "Invoke-WaypointNsxScan",
				ExpectedParameterKeys: ["Manager", "Username", "Password", "ProfilePath", "ReportPath", "TimeoutSeconds", "SelectorName"],
				CarriesSelectorName: true),

			// Workspace ONE Access 3-3-x SRG / ssh transport / whole-appliance target
			// selector: SRG-shaped command, no SelectorName parameter (the component IS
			// the appliance -- ScanComponentNarrowing's "ssh / target" doc comment),
			// credential purpose srg-ssh.
			new ExecutionParityRow(
				MatrixRowId: "vidm-3-3-x-srg-target",
				Transport: CatalogTransports.Ssh,
				SelectorKind: CatalogSelectorKinds.Target,
				SelectorName: null,
				OutputKind: CatalogOutputKinds.Hdf,
				CredentialPurpose: "srg-ssh",
				ExpectedCommand: "Invoke-WaypointSrgScan",
				ExpectedParameterKeys: ["SshHost", "Username", "Password", "ProfilePath", "ReportPath", "TimeoutSeconds", "Sudo", "SudoRequiresPassword"],
				CarriesSelectorName: false),
		];
	}
}

/// <summary>
/// One row of the execution-parity matrix: one documented family's command-construction
/// contract -- which invocation <c>ScanJobHandler</c> issues, its expected parameter-key
/// SET (order-independent; ordering of platform/auth vs. operator input FILES is a
/// separate, file-level assertion -- see <see cref="ExecutionParityContractTests"/>'s
/// input-file-ordering theory), whether SelectorName rides the invocation's own
/// parameters, and the credential purpose the job must consume.
/// </summary>
public sealed record ExecutionParityRow(
	string MatrixRowId,
	string Transport,
	string SelectorKind,
	string? SelectorName,
	string OutputKind,
	string CredentialPurpose,
	string ExpectedCommand,
	IReadOnlyList<string> ExpectedParameterKeys,
	bool CarriesSelectorName);
