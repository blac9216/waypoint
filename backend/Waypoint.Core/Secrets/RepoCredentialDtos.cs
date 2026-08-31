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

namespace Waypoint.Core.Secrets;

/// <summary>
/// The closed set of repo stores (issue #1502, <c>deploy/nginx/conf.d/default.conf</c>'s
/// merged location tree): every subtree the nginx repo path-space actually serves
/// today -- the VCF depot proper, the ESX patch store (UMDS), the Photon mirror,
/// VMTools, VKS, and content libraries. There is no DB-level "repo store" row (#1502
/// stood up nginx <c>location</c> blocks and volume mounts, not a table) -- this is the
/// literal set of <c>/repo/&lt;store&gt;/</c>-style path segments a repo-serving
/// credential (<see cref="CredentialTypes.RepoBasicAuth"/>) can be bound to via
/// <see cref="RepoCredentialBinding"/>. Same closed-set-as-inert-data convention
/// <see cref="Waypoint.Core.Sites.TargetKinds"/> and <see cref="CredentialPurposes"/>
/// already use: API-layer validation here, backed by migration 0103's DB CHECK for
/// defense-in-depth.
/// </summary>
public static class RepoStores
{
	public const string Depot = "depot";
	public const string Umds = "umds";
	public const string Photon = "photon";
	public const string VmTools = "vmtools";
	public const string Vks = "vks";
	public const string ContentLibraries = "content-libraries";

	public static readonly IReadOnlyCollection<string> All = [Depot, Umds, Photon, VmTools, Vks, ContentLibraries];

	public static bool IsValid(string? store) => store is not null && All.Contains(store);
}

/// <summary>
/// One repo store -&gt; credential binding (issue #1517, migration 0103) -- at most one
/// <see cref="CredentialId"/> per <see cref="Store"/> (UNIQUE on <c>store</c> alone:
/// unlike <see cref="Waypoint.Core.Sites.TargetCredentialBinding"/>, there is exactly
/// one purpose in this bounded context -- Basic-auth repo serving -- so a store never
/// needs more than one binding at once). Setting a new binding for a store that
/// already has one REPLACES it (the same override semantics ADR-0021 SS4 established
/// for target credential bindings), it does not add a second row.
/// </summary>
public sealed record RepoCredentialBinding(
	Guid Id,
	string Store,
	Guid CredentialId,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

public enum RepoCredentialBindingWriteOutcome
{
	Ok,

	/// <summary>The store is not in <see cref="RepoStores"/>'s closed set.</summary>
	InvalidStore,

	CredentialNotFound,

	/// <summary>The credential's type is not <see cref="CredentialTypes.RepoBasicAuth"/> -- only a repo-serving credential may bind a repo store.</summary>
	IncompatibleCredentialType,
}

public enum RepoCredentialBindingDeleteOutcome
{
	Deleted,
	NotFound,
}
