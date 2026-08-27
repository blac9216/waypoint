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

namespace Waypoint.Core.Discovery;

/// <summary>
/// The closed set of inventory item types (migration 0011's
/// <c>inventory_items_type_check</c>) -- the three-level tree api-contract.md names
/// for the start-a-scan checkbox tree: cluster -&gt; host -&gt; vm.
/// </summary>
public static class InventoryItemTypes
{
	public const string Cluster = "cluster";
	public const string Host = "host";
	public const string Vm = "vm";

	public static readonly IReadOnlyCollection<string> All = [Cluster, Host, Vm];

	public static bool IsValid(string? type) => type is not null && All.Contains(type);
}

/// <summary>
/// A discovered inventory item as stored (docs/domain-model.md, docs/api-contract.md
/// `/targets/{id}/inventory`). <see cref="ParentId"/> builds the cluster -&gt; host -&gt;
/// vm tree; <see cref="Moref"/> is the vSphere managed-object reference, the stable
/// identity re-discovery upserts against. <see cref="RemovedAt"/> is non-null once a
/// discovery pass no longer sees the item in vCenter (soft-delete removal detection,
/// issue #21 acceptance criterion). <see cref="Version"/> (issue #974) is the host's
/// semantic product version (e.g. "8.0.3") as reported by vSphere alongside
/// <see cref="Build"/> -- present only for `host` rows; <see cref="Build"/> remains the
/// raw build number, retained unconditionally as a discovered fact even though (issue
/// #974) it is no longer what a host's catalog match is keyed on.
/// </summary>
public sealed record InventoryItem(
	Guid Id,
	Guid TargetId,
	Guid? ParentId,
	string Type,
	string Moref,
	string Name,
	string? Build,
	bool? MaintenanceMode,
	DateTimeOffset LastSeenAt,
	DateTimeOffset? RemovedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	string? Version = null);

/// <summary>
/// One discovered item as reported by the PowerShell discovery invocation, before it
/// is persisted. <see cref="ParentMoref"/> (not a DB id -- discovery has no row ids
/// yet) is resolved to the parent's <see cref="InventoryItem.Id"/> by
/// <c>InventoryRepository.UpsertDiscoveryResultsAsync</c>, which processes items in
/// the order given -- clusters and hosts (potential parents) must precede their
/// children in <see cref="DiscoveryResult.Items"/> for the resolution to succeed; the
/// PowerShell module's output order (cluster, then its hosts, then their VMs)
/// satisfies this, and the handler does not reorder it.
///
/// <see cref="Version"/> (issue #974, owner decision on #967's options analysis,
/// "Option A"): the host's semantic vSphere product version (vCenter's
/// `ExtensionData.Config.Product.Version`-style field, e.g. "8.0.3"), populated only
/// for `host` rows -- null for `cluster`/`vm` rows and for a host where the field was
/// unavailable this pass. This is a NEW, additive fact alongside <see cref="Build"/>,
/// which continues to be captured and stored exactly as before; nothing reads
/// <see cref="Build"/> as a fallback for a missing <see cref="Version"/> (fail-closed:
/// an unavailable semantic version must read as "no exact version," never an inferred
/// one from the build number -- that inference is precisely what ADR-0022's
/// byte-for-byte exact-match contract exists to forbid, see issue #974's
/// options-analysis comment on #967).
/// </summary>
public sealed record DiscoveredInventoryItem(
	string Type,
	string Moref,
	string Name,
	string? ParentMoref,
	string? Build,
	bool? MaintenanceMode,
	string? Version = null);

/// <summary>The full set of items one discovery pass found for a target, in parent-before-child order.</summary>
public sealed record DiscoveryResult(IReadOnlyList<DiscoveredInventoryItem> Items);

/// <summary>Outcome of one upsert pass: how many items were created/updated and how many previously-seen items were marked removed.</summary>
public sealed record InventoryUpsertOutcome(int Upserted, int Removed);
