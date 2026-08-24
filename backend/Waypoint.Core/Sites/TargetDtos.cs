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

namespace Waypoint.Core.Sites;

/// <summary>
/// The closed set of target kinds (docs/domain-model.md "Target"). Multiple targets of
/// the same kind are allowed under one site (e.g. two vCenters) -- there is no
/// uniqueness constraint on (site_id, kind), only on (site_id, name).
/// </summary>
public static class TargetKinds
{
	public const string VSphere = "vsphere";
	public const string NsxApi = "nsx-api";
	public const string Ssh = "ssh";

	public static readonly IReadOnlyCollection<string> All = [VSphere, NsxApi, Ssh];

	public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// The closed set of discovery_status values. Not enumerated anywhere in
/// docs/domain-model.md or docs/api-contract.md (both name the field only) -- this set
/// is invented here to match the `/targets/{id}/discover` job lifecycle the contract
/// does describe (queued discover job -> running -> terminal), and is intentionally
/// small pending the real #13 discovery-job slice.
/// </summary>
public static class TargetDiscoveryStatuses
{
	public const string NeverDiscovered = "never_discovered";
	public const string Discovering = "discovering";
	public const string Discovered = "discovered";
	public const string Failed = "failed";

	public static readonly IReadOnlyCollection<string> All = [NeverDiscovered, Discovering, Discovered, Failed];
}

/// <summary>
/// A target as stored (docs/domain-model.md "Target"): a scannable/manageable endpoint
/// within a site. <see cref="ConnectionJson"/> carries kind-specific connection details
/// (hostname/address, etc.) as raw JSON text -- same convention as
/// <c>Site.StigmanOverrideJson</c> -- and is guaranteed by the API layer to never embed
/// secret material; the only path to a credential is <see cref="CredentialId"/>.
/// </summary>
public sealed record Target(
	Guid Id,
	Guid SiteId,
	string Kind,
	string Name,
	string ConnectionJson,
	Guid? CredentialId,
	string DiscoveryStatus,
	DateTimeOffset? LastRefreshed,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>Create request. <see cref="ConnectionJson"/> is validated by the controller to reject secret-shaped keys before it ever reaches the repository.</summary>
public sealed record TargetCreateRequest(string? Kind, string? Name, string? ConnectionJson, Guid? CredentialId);

/// <summary>Update request: full replacement update, same "only replace what's supplied" semantics as <see cref="SiteUpdateRequest"/>.</summary>
public sealed record TargetUpdateRequest(string? Kind, string? Name, string? ConnectionJson, Guid? CredentialId, bool ClearCredential);

public enum TargetWriteOutcome
{
	Ok,
	NotFound,
	NameTaken,
	SiteNotFound,
	CredentialNotFound,
}

public enum TargetDeleteOutcome
{
	Deleted,
	NotFound,
}

/// <summary>
/// One purpose-specific credential binding on a target (issue #584, migration 0043,
/// ADR-0021 docs/adr/0021-credential-purpose-matrix.md), keyed by
/// <c>(target_id, purpose)</c> -- at most one credential per purpose per target. This
/// is a normalized generalization of <see cref="Target.CredentialId"/>: a
/// <c>vsphere</c> target can carry both a <see cref="Waypoint.Core.Secrets.CredentialPurposes.VSphereApi"/>
/// and a <see cref="Waypoint.Core.Secrets.CredentialPurposes.VcsaSsh"/> binding at
/// once, independently overridable/removable (ADR-0021 §4).
/// </summary>
public sealed record TargetCredentialBinding(
	Guid Id,
	Guid TargetId,
	string Purpose,
	Guid CredentialId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

public enum TargetCredentialBindingWriteOutcome
{
	Ok,
	TargetNotFound,
	CredentialNotFound,

	/// <summary>The purpose is not in <see cref="Waypoint.Core.Secrets.CredentialPurposes"/>'s closed set.</summary>
	InvalidPurpose,

	/// <summary>The purpose does not apply to the target's kind (<c>CredentialPurposeMatrix.ApplicablePurposes</c>).</summary>
	PurposeNotApplicable,

	/// <summary>The credential's type does not satisfy the purpose (ADR-0021 §2, <c>CredentialPurposeMatrix.SatisfyingCredentialTypes</c>).</summary>
	IncompatibleCredentialType,
}

public enum TargetCredentialBindingDeleteOutcome
{
	Deleted,
	NotFound,
}
