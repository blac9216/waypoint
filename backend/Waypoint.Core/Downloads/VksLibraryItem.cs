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

namespace Waypoint.Core.Downloads;

/// <summary>The exact string values of <c>vks_library_items.source</c>, matching <c>vks_library_items_source_check</c> (migration 0111).</summary>
public static class VksItemSources
{
	public const string Depot = "depot";
	public const string Public = "public";

	public static readonly IReadOnlyCollection<string> All = [Depot, Public];
}

/// <summary>The exact string values of <c>vks_library_items.release_line</c>, matching <c>vks_library_items_release_line_check</c> (migration 0111).</summary>
public static class VksReleaseLines
{
	public const string Vkr = "vkr";
	public const string Tkg = "tkg";

	public static readonly IReadOnlyCollection<string> All = [Vkr, Tkg];
}

/// <summary>The exact string values of <c>vks_library_items.naming_era</c>, matching <c>vks_library_items_naming_era_check</c> (migration 0111). Research #1031 Layer B's four coexisting eras, oldest first, plus the fallback state for a name the grammar cannot match.</summary>
public static class VksNamingEras
{
	/// <summary>Oldest era: <c>*-k8s-*</c>, arch absent from the name (#1031).</summary>
	public const string LegacyK8s = "legacy_k8s";

	/// <summary>The <c>tkgs-ova-*</c> era, arch also absent from the name (#1031).</summary>
	public const string TkgsOva = "tkgs_ova";

	/// <summary>The <c>*-vmi-k8s-*</c> era, arch present.</summary>
	public const string VmiK8s = "vmi_k8s";

	/// <summary>Current era: no k8s token at all (<c>&lt;distro&gt;-&lt;ver&gt;-amd64-v&lt;k8s&gt;---vmware.N-vkr.N</c>), arch present.</summary>
	public const string Current = "current";

	/// <summary>The grammar could not match the raw name at all. Every dimension column is NULL; the row is stored anyway, never dropped (#1031 Risk).</summary>
	public const string Unparsed = "unparsed";

	public static readonly IReadOnlyCollection<string> All = [LegacyK8s, TkgsOva, VmiK8s, Current, Unparsed];
}

/// <summary>The exact string values of <c>vks_library_items.parse_status</c>, matching <c>vks_library_items_parse_status_check</c> (migration 0111).</summary>
public static class VksParseStatuses
{
	public const string Parsed = "parsed";
	public const string Unparsed = "unparsed";

	public static readonly IReadOnlyCollection<string> All = [Parsed, Unparsed];
}

/// <summary>
/// An item's change token from the public <c>items.json</c> file entry (#1031 Layer
/// B). This is a DISTINCT type from any hash/checksum representation on purpose: it
/// has no equality or conversion path to <see cref="VksLibraryItem.Sha256"/> or any
/// other hash type, so a code path that tries to compare or substitute one for the
/// other is a compile error rather than a silent integrity bug -- research #1031
/// measured 93/138 items publishing an identical etag across a 3.7 GB vmdk and a
/// 249-byte .mf alike, and one item where the served .mf's real MD5 differed from
/// its published etag. Treat strictly as opaque: refetch when the token or size
/// changes, never as an integrity value.
/// </summary>
public sealed record VksChangeToken(string Value)
{
	public override string ToString() => Value;
}

/// <summary>
/// The dimensions the name-grammar parser extracts from a VKS/VKR item name (#1031
/// Layer B), or all-null when <see cref="VksLibraryItem.ParseStatus"/> is
/// <see cref="VksParseStatuses.Unparsed"/>. <see cref="Arch"/> is nullable
/// independently of parse success -- 46/138 live items lack an arch token in the
/// name at all (the legacy_k8s and tkgs_ova eras), to be backfilled later from the
/// OVF ProductSection by whichever backend fetches it (not this issue).
/// </summary>
public sealed record VksItemDimensions(
	string? Distro,
	string? DistroVersion,
	string? Arch,
	string? K8sVersion,
	string? VmwareBuild,
	bool Fips,
	string? ReleaseLine,
	string? LineBuild,
	string? ObBuildId)
{
	/// <summary>All dimensions null and FIPS false -- the shape stored for an unparseable name.</summary>
	public static readonly VksItemDimensions Empty = new(null, null, null, null, null, false, null, null, null);
}

/// <summary>
/// One row of <c>vks_library_items</c> (migration 0111, issue #1480): the shared
/// dual-backend identity/dimension model for a VKS/VKR library item, from either the
/// depot-fed signed catalog (<see cref="VksItemSources.Depot"/>, #1031 Layer D) or
/// the public mirror (<see cref="VksItemSources.Public"/>, #1031 Layer B). Model-only
/// slice: no sync logic. Never merged into a local content library (design record
/// #16 §5) -- VKS stores stay a standalone library index.
/// </summary>
public sealed record VksLibraryItem(
	Guid Id,
	string Name,
	string Source,
	string? ItemUuid,
	VksItemDimensions Dimensions,
	string NamingEra,
	string ParseStatus,
	VksChangeToken? Etag,
	string? Sha256,
	long? SizeBytes,
	DateTimeOffset? CreatedUpstream,
	DateTimeOffset DiscoveredAt,
	DateTimeOffset LastSeenAt)
{
	/// <summary>Builds the row a fresh index observation maps to, before a repository upsert decides whether it is new or a re-seen name (discovered/last-seen are therefore both "now" here -- the repository is what advances <see cref="LastSeenAt"/> on a genuine re-seen row and leaves <see cref="DiscoveredAt"/> untouched).</summary>
	public static VksLibraryItem FromIndexObservation(
		string name,
		string source,
		string? itemUuid,
		VksItemDimensions dimensions,
		string namingEra,
		string parseStatus,
		VksChangeToken? etag,
		string? sha256,
		long? sizeBytes,
		DateTimeOffset? createdUpstream,
		DateTimeOffset observedAt)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(namingEra);
		ArgumentException.ThrowIfNullOrWhiteSpace(parseStatus);
		ArgumentNullException.ThrowIfNull(dimensions);
		return new VksLibraryItem(
			Guid.NewGuid(), name, source, itemUuid, dimensions, namingEra, parseStatus, etag, sha256, sizeBytes,
			createdUpstream, observedAt, observedAt);
	}
}
