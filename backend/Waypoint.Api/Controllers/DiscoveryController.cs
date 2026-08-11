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

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Discovery;
using Waypoint.Core.Errors;
using Waypoint.Core.Jobs;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md <c>/targets/{id}/inventory</c> and <c>/targets/{id}/discover</c>
/// surface (issue #21, epic #13): serves the cached inventory tree for the
/// start-a-scan checkbox tree, and queues a <c>discover</c> job to (re)populate it.
/// Reads are Viewer+ (same floor <c>TargetsController</c> uses); queuing a discover
/// job is Admin-only for M1/M2 -- api-contract.md's Cyber/Operator role split does not
/// exist until M3/RBAC, so Admin-only is the stricter-for-now floor
/// (matches <c>DownloadsController.QueueDownloads</c>).
/// </summary>
[ApiController]
public sealed class DiscoveryController : ControllerBase
{
	/// <summary>
	/// No scan priority tier applies to discovery (domain-model.md's six-valued
	/// priority column is for scan/remediate targets); runs above download's lowest
	/// tier since a checkbox tree waiting on a fresh inventory is user-visible latency,
	/// but below where a scan job would sit once #23 lands.
	/// </summary>
	private const short DiscoverPriority = 4;

	private readonly TargetRepository _targets;
	private readonly InventoryRepository _inventory;
	private readonly IJobControlRepository _jobs;
	private readonly IOptions<DiscoveryOptions> _options;

	public DiscoveryController(TargetRepository targets, InventoryRepository inventory, IJobControlRepository jobs, IOptions<DiscoveryOptions> options)
	{
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(inventory);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(options);
		_targets = targets;
		_inventory = inventory;
		_jobs = jobs;
		_options = options;
	}

	/// <summary>
	/// Serves the cached inventory tree (cluster -&gt; host -&gt; vm). Never triggers a
	/// discovery pass itself -- <see cref="TargetInventoryResponse.Stale"/> tells the
	/// caller (the Start-a-Scan screen) whether the cache exceeds the configured
	/// staleness threshold so IT can decide to call <c>POST .../discover</c> first
	/// (domain-model.md open question 3's manual/on-demand half; the scan-initiation
	/// auto-trigger half lands with #23 -- see this PR's Split note).
	/// </summary>
	[HttpGet("api/v1/targets/{id:guid}/inventory")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(TargetInventoryResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<TargetInventoryResponse>> GetInventory(Guid id, CancellationToken cancellationToken)
	{
		Target? target = await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			throw NotFoundError(id);
		}

		IReadOnlyList<InventoryItem> flat = await _inventory.ListAsync(id, includeRemoved: false, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<InventoryItemResponse> tree = BuildTree(flat, parentId: null);

		bool stale = target.LastRefreshed is null ||
			target.LastRefreshed.Value < DateTimeOffset.UtcNow.AddMinutes(-_options.Value.StaleAfterMinutes);

		return Ok(new TargetInventoryResponse(id, target.DiscoveryStatus, target.LastRefreshed, stale, tree));
	}

	/// <summary>
	/// Queues a <c>discover</c> job for the target -- refresh-on-demand, callable
	/// regardless of staleness (a manual "Refresh" action on the Start-a-Scan screen).
	/// One run containing one job, the same one-run-per-initiation shape
	/// <c>DownloadsController.QueueDownloads</c> uses for its fan-out (here, a
	/// fan-out of exactly one).
	/// </summary>
	[HttpPost("api/v1/targets/{id:guid}/discover")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DiscoverQueuedResponse), StatusCodes.Status202Accepted)]
	public async Task<ActionResult<DiscoverQueuedResponse>> Discover(Guid id, CancellationToken cancellationToken)
	{
		Target? target = await _targets.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (target is null)
		{
			throw NotFoundError(id);
		}

		if (!string.Equals(target.Kind, TargetKinds.VSphere, StringComparison.Ordinal))
		{
			throw new ApiException(
				System.Net.HttpStatusCode.BadRequest, "unsupported_kind",
				$"discover only supports '{TargetKinds.VSphere}' targets; '{id}' is kind '{target.Kind}'.");
		}

		string initiatedBy = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		string payload = JsonSerializer.Serialize(new { target_id = id });
		Guid runId = await _jobs.CreateRunAsync("discover", "{}", target.CredentialId, initiatedBy, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<Guid> jobIds = await _jobs.FanOutJobsAsync(
			runId,
			[new JobSpec("discover", DiscoverPriority, TargetId: id, TargetName: target.Name, CredentialId: target.CredentialId, Payload: payload)],
			initiatedBy,
			cancellationToken).ConfigureAwait(false);

		return Accepted(new DiscoverQueuedResponse(runId.ToString(), jobIds[0].ToString()));
	}

	/// <summary>
	/// Nests the flat, parent-before-child-ordered list (<see cref="InventoryRepository.ListAsync"/>'s
	/// <c>ORDER BY type, created_at</c>) into the response tree. A dictionary keyed by
	/// item id makes each item's children list a single lookup rather than an O(n^2)
	/// scan for every node.
	/// </summary>
	private static List<InventoryItemResponse> BuildTree(IReadOnlyList<InventoryItem> flat, Guid? parentId)
	{
		Dictionary<Guid, List<InventoryItem>> childrenByParent = new();
		List<InventoryItem> roots = [];
		foreach (InventoryItem item in flat)
		{
			if (item.ParentId is { } parent)
			{
				if (!childrenByParent.TryGetValue(parent, out List<InventoryItem>? siblings))
				{
					siblings = [];
					childrenByParent[parent] = siblings;
				}

				siblings.Add(item);
			}
			else
			{
				roots.Add(item);
			}
		}

		return BuildNodes(parentId is null ? roots : childrenByParent.GetValueOrDefault(parentId.Value, []), childrenByParent);
	}

	private static List<InventoryItemResponse> BuildNodes(
		IReadOnlyList<InventoryItem> items, Dictionary<Guid, List<InventoryItem>> childrenByParent)
	{
		List<InventoryItemResponse> nodes = [];
		foreach (InventoryItem item in items)
		{
			IReadOnlyList<InventoryItemResponse> children = BuildNodes(childrenByParent.GetValueOrDefault(item.Id, []), childrenByParent);
			nodes.Add(InventoryItemResponse.FromDomain(item, children));
		}

		return nodes;
	}

	private static ApiException NotFoundError(Guid id) =>
		new(System.Net.HttpStatusCode.NotFound, "not_found", $"No target exists with id '{id}'.");
}
