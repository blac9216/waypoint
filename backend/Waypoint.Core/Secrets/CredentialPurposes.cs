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

using Waypoint.Core.Sites;

namespace Waypoint.Core.Secrets;

/// <summary>
/// The closed set of credential-purpose identifiers
/// (<see href="../../docs/adr/0021-credential-purpose-matrix.md">ADR-0021</see>, issue
/// #583). Explicit named purposes, never generic numbered slots -- same shape as
/// <see cref="CredentialTypes"/>/<see cref="Waypoint.Core.Sites.TargetKinds"/>.
///
/// This is a design/contracts-only slice: nothing outside this file's own unit tests
/// and <see cref="CredentialPurposeMatrix"/> consumes these values yet. Persistence
/// lands in issue #584; execution resolution in #585/#586; the wizard UI in #587.
/// </summary>
public static class CredentialPurposes
{
	/// <summary>vSphere SSO session -- vCenter/ESXi/VM API access (PowerCLI / <c>vmware://</c> InSpec transport).</summary>
	public const string VSphereApi = "vsphere-api";

	/// <summary>VCSA appliance root SSH -- VCSA OS-level STIG components only. Distinct from <see cref="VSphereApi"/>: issue #580/PR #606 proved the two are independently satisfiable (<c>-SkipVCSACredential</c>).</summary>
	public const string VcsaSsh = "vcsa-ssh";

	/// <summary>NSX Manager REST API session (<c>Get-NsxSessionToken</c>).</summary>
	public const string NsxApi = "nsx-api";

	/// <summary>SRG product SSH login (Photon, Aria Operations, Aria Lifecycle, vIDM), sudo-capable. Not split per product -- all four share the same transport and credential shape, differing only in <c>sudo_enabled</c>.</summary>
	public const string SrgSsh = "srg-ssh";

	public static readonly IReadOnlyCollection<string> All = [VSphereApi, VcsaSsh, NsxApi, SrgSsh];

	public static bool IsValid(string? purpose) => purpose is not null && All.Contains(purpose);
}

/// <summary>
/// One target-kind operation the credential-purpose matrix covers (ADR-0021 §3).
/// <see cref="Component"/> distinguishes multiple credential-test/scan rows that share
/// a <see cref="TargetKind"/> and <see cref="Operation"/> (e.g. <c>vsphere</c>
/// credential-test has one row per purpose it can test independently) -- null when the
/// operation has exactly one row for that target kind.
/// </summary>
public sealed record CredentialPurposeMatrixEntry(
	string TargetKind,
	string Operation,
	string? Component,
	IReadOnlyCollection<string> RequiredPurposes,
	IReadOnlyCollection<string> OptionalPurposes);

/// <summary>The closed set of operations the matrix covers (ADR-0021 §3).</summary>
public static class CredentialPurposeOperations
{
	public const string Discovery = "discovery";
	public const string CredentialTest = "credential-test";
	public const string Scan = "scan";
	public const string RemediationReadyPlanning = "remediation-ready-planning";

	public static readonly IReadOnlyCollection<string> All = [Discovery, CredentialTest, Scan, RemediationReadyPlanning];
}

/// <summary>
/// The authoritative target-kind x operation -&gt; required/optional credential-purpose
/// matrix (ADR-0021 §3), and the purpose -&gt; satisfying credential-type compatibility
/// map (ADR-0021 §2), as plain inert data. Derived from the real transport code
/// (<c>runners/compliance-runner/powershell/module.transport.vmware.ps1</c>,
/// <c>module.transport.nsxapi.ps1</c>, and the <c>WaypointDiscovery</c>/
/// <c>WaypointCredentialTest</c>/<c>WaypointScan</c> wrapper modules) -- not invented.
///
/// Nothing outside this file's own unit tests consumes this yet; see the type doc
/// comment on <see cref="CredentialPurposes"/>.
/// </summary>
public static class CredentialPurposeMatrix
{
	/// <summary>
	/// Purpose -&gt; the credential type(s) that can satisfy it (ADR-0021 §2). A
	/// credential type appearing here does not mean every credential of that type
	/// satisfies every purpose it's listed under at once -- compatibility is evaluated
	/// per binding, not granted globally by type (ADR-0021 §2/§4).
	/// </summary>
	public static readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> SatisfyingCredentialTypes =
		new Dictionary<string, IReadOnlyCollection<string>>
		{
			[CredentialPurposes.VSphereApi] = [CredentialTypes.VCenter],
			[CredentialPurposes.VcsaSsh] = [CredentialTypes.Ssh],
			[CredentialPurposes.NsxApi] = [CredentialTypes.Nsx],
			[CredentialPurposes.SrgSsh] = [CredentialTypes.Ssh],
		};

	/// <summary>
	/// The full target-kind x operation matrix (ADR-0021 §3). Discovery is present only
	/// for <see cref="TargetKinds.VSphere"/> -- <c>nsx-api</c> and <c>ssh</c> targets
	/// have no discovery operation today (only vsphere is inventory-capable; mirrors
	/// <c>INVENTORY_CAPABLE_TARGET_KINDS</c> in frontend/src/screens/configuration/sites.ts).
	/// </summary>
	public static readonly IReadOnlyCollection<CredentialPurposeMatrixEntry> Entries =
	[
		// vsphere
		new(TargetKinds.VSphere, CredentialPurposeOperations.Discovery, null, [CredentialPurposes.VSphereApi], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.CredentialTest, "vcenter-api", [CredentialPurposes.VSphereApi], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.CredentialTest, "vcsa-ssh", [CredentialPurposes.VcsaSsh], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.Scan, "vcenter", [CredentialPurposes.VSphereApi], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.Scan, "esxi", [CredentialPurposes.VSphereApi], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.Scan, "vm", [CredentialPurposes.VSphereApi], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.Scan, "vcsa", [CredentialPurposes.VSphereApi, CredentialPurposes.VcsaSsh], []),
		new(TargetKinds.VSphere, CredentialPurposeOperations.RemediationReadyPlanning, null, [CredentialPurposes.VSphereApi], [CredentialPurposes.VcsaSsh]),

		// nsx-api (no discovery operation exists for this kind)
		new(TargetKinds.NsxApi, CredentialPurposeOperations.CredentialTest, null, [CredentialPurposes.NsxApi], []),
		new(TargetKinds.NsxApi, CredentialPurposeOperations.Scan, null, [CredentialPurposes.NsxApi], []),
		new(TargetKinds.NsxApi, CredentialPurposeOperations.RemediationReadyPlanning, null, [CredentialPurposes.NsxApi], []),

		// ssh (SRG) (no discovery operation exists for this kind)
		new(TargetKinds.Ssh, CredentialPurposeOperations.CredentialTest, null, [CredentialPurposes.SrgSsh], []),
		new(TargetKinds.Ssh, CredentialPurposeOperations.Scan, null, [CredentialPurposes.SrgSsh], []),
		new(TargetKinds.Ssh, CredentialPurposeOperations.RemediationReadyPlanning, null, [CredentialPurposes.SrgSsh], []),
	];
}
