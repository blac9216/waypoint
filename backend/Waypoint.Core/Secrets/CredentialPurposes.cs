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

	/// <summary>
	/// Target kind -&gt; the ONE purpose <see cref="targets.CredentialId"/>-style
	/// dual-write logic (issue #584, migration 0043's data-migration/dual-write
	/// contract) mirrors. This is the single purpose every row in <see cref="Entries"/>
	/// for that kind requires unconditionally (present as a <c>RequiredPurposes</c>
	/// entry on every operation row for the kind) -- <c>vsphere</c>'s second purpose,
	/// <see cref="CredentialPurposes.VcsaSsh"/>, is required only for the optional VCSA
	/// scan component (ADR-0021 §3's note), so it is never inferable from the single
	/// legacy column and is deliberately excluded here.
	/// </summary>
	public static readonly IReadOnlyDictionary<string, string> DefaultPurposeByTargetKind = new Dictionary<string, string>
	{
		[TargetKinds.VSphere] = CredentialPurposes.VSphereApi,
		[TargetKinds.NsxApi] = CredentialPurposes.NsxApi,
		[TargetKinds.Ssh] = CredentialPurposes.SrgSsh,
	};

	/// <summary>Every purpose that appears anywhere in <see cref="Entries"/> for <paramref name="targetKind"/> (required or optional, across every operation) -- the applicable-purpose set a target of this kind may bind, used to reject an inapplicable purpose (issue #584 AC) before it ever reaches storage.</summary>
	public static IReadOnlyCollection<string> ApplicablePurposes(string targetKind)
	{
		return Entries
			.Where(e => string.Equals(e.TargetKind, targetKind, StringComparison.Ordinal))
			.SelectMany(e => e.RequiredPurposes.Concat(e.OptionalPurposes))
			.Distinct()
			.ToArray();
	}

	/// <summary>
	/// Issue #585: the purposes a scan of <paramref name="targetKind"/> unconditionally
	/// requires -- those present in <see cref="CredentialPurposeMatrixEntry.RequiredPurposes"/>
	/// of EVERY scan row for the kind (ADR-0021 §3). Run creation must resolve each of
	/// these for every selected target or reject the run (§6). Component-conditional
	/// purposes (required by only some scan components, e.g. <c>vcsa-ssh</c> for the
	/// VCSA component) are <see cref="ConditionalScanPurposes"/> instead: until scan
	/// component selection exists on the wire (issue #587's wizard slice), they resolve
	/// opportunistically when bound/overridden and are never a rejection.
	/// </summary>
	public static IReadOnlyCollection<string> RequiredScanPurposes(string targetKind)
	{
		CredentialPurposeMatrixEntry[] scanEntries = ScanEntries(targetKind);
		return scanEntries.Length == 0
			? []
			: scanEntries
				.Skip(1)
				.Aggregate(
					scanEntries[0].RequiredPurposes.AsEnumerable(),
					(intersection, entry) => intersection.Intersect(entry.RequiredPurposes, StringComparer.Ordinal))
				.ToArray();
	}

	/// <summary>
	/// Issue #585: purposes required by SOME but not ALL scan components of
	/// <paramref name="targetKind"/> (today: <c>vcsa-ssh</c> for <c>vsphere</c>'s VCSA
	/// component only) -- resolved into a job's snapshot when a binding or override
	/// exists, absent otherwise, mirroring the transport's own graceful VCSA skip
	/// (ADR-0021 §3 note). See <see cref="RequiredScanPurposes"/>.
	/// </summary>
	public static IReadOnlyCollection<string> ConditionalScanPurposes(string targetKind)
	{
		IReadOnlyCollection<string> required = RequiredScanPurposes(targetKind);
		return ScanEntries(targetKind)
			.SelectMany(e => e.RequiredPurposes.Concat(e.OptionalPurposes))
			.Distinct(StringComparer.Ordinal)
			.Where(p => !required.Contains(p, StringComparer.Ordinal))
			.ToArray();
	}

	/// <summary>True when <paramref name="credentialType"/> is in <paramref name="purpose"/>'s compatibility set (ADR-0021 §2). Unknown purposes are compatible with nothing.</summary>
	public static bool IsCompatible(string purpose, string credentialType)
	{
		return SatisfyingCredentialTypes.TryGetValue(purpose, out IReadOnlyCollection<string>? types)
			&& types.Contains(credentialType, StringComparer.Ordinal);
	}

	private static CredentialPurposeMatrixEntry[] ScanEntries(string targetKind)
	{
		return Entries
			.Where(e => string.Equals(e.TargetKind, targetKind, StringComparison.Ordinal)
				&& string.Equals(e.Operation, CredentialPurposeOperations.Scan, StringComparison.Ordinal))
			.ToArray();
	}
}
