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

using System.Text.Json.Serialization;
using Waypoint.Core.Discovery;

namespace Waypoint.Api.Contracts;

/// <summary>
/// One node of the <c>GET /targets/{id}/inventory</c> tree (docs/api-contract.md:
/// "Cached hosts/VMs tree (cluster -&gt; host -&gt; vm), build info, maintenance_mode").
/// <see cref="Children"/> nests the tree directly rather than returning a flat list
/// with parent ids, so the start-a-scan checkbox tree can render straight off the
/// response with no client-side tree-building step.
/// </summary>
public sealed record InventoryItemResponse(
	[property: JsonPropertyName("id")]
	Guid Id,

	[property: JsonPropertyName("type")]
	string Type,

	[property: JsonPropertyName("moref")]
	string MoRef,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("build")]
	string? Build,

	[property: JsonPropertyName("maintenance_mode")]
	bool? MaintenanceMode,

	[property: JsonPropertyName("last_seen_at")]
	DateTimeOffset LastSeenAt,

	[property: JsonPropertyName("removed")]
	bool Removed,

	[property: JsonPropertyName("children")]
	IReadOnlyList<InventoryItemResponse> Children,

	// Issue #974: the host's semantic vSphere product version (e.g. "8.0.3"), additive
	// alongside Build -- present only for `host` rows where discovery could report it;
	// Build is unchanged and always still reported when known.
	[property: JsonPropertyName("version")]
	string? Version = null,

	// Issue #1063: the VM's authoritative vSphere instance UUID, present only for `vm`
	// rows -- deconflicts identically named VMs across discovery passes.
	[property: JsonPropertyName("instance_uuid")]
	string? InstanceUuid = null)
{
	public static InventoryItemResponse FromDomain(InventoryItem item, IReadOnlyList<InventoryItemResponse> children)
	{
		ArgumentNullException.ThrowIfNull(item);
		ArgumentNullException.ThrowIfNull(children);
		return new InventoryItemResponse(
			item.Id, item.Type, item.Moref, item.Name, item.Build, item.MaintenanceMode,
			item.LastSeenAt, item.RemovedAt is not null, children, item.Version, item.InstanceUuid);
	}
}

/// <summary>
/// The full inventory response for a target: the tree roots plus the staleness the
/// Start-a-Scan screen needs to decide whether to offer a refresh (domain-model.md
/// open question 3, answered by <see cref="Waypoint.Core.Discovery.DiscoveryOptions"/>).
/// </summary>
public sealed record TargetInventoryResponse(
	[property: JsonPropertyName("target_id")]
	Guid TargetId,

	[property: JsonPropertyName("discovery_status")]
	string DiscoveryStatus,

	[property: JsonPropertyName("last_refreshed")]
	DateTimeOffset? LastRefreshed,

	[property: JsonPropertyName("stale")]
	bool Stale,

	[property: JsonPropertyName("items")]
	IReadOnlyList<InventoryItemResponse> Items);

/// <summary>Response for <c>POST /targets/{id}/discover</c> -- 202, the queued discover job's run/job ids.</summary>
public sealed record DiscoverQueuedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("job_id")]
	string JobId);
