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

using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Catalog;
using Waypoint.Core.Pagination;
using Waypoint.Core.SystemState;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md "Library &amp; content library" surface's repository half (issue
/// #36): <c>GET /library/items</c> (mode-aware presence over the existing depot catalog)
/// and <c>GET /library/request-manifest</c> (air-gapped want-list export). Both are
/// Viewer+ read endpoints, matching <see cref="CatalogController"/>'s gating -- the
/// Library tab is browsable by anyone who can see the appliance, same reasoning
/// (docs/domain-model.md open question 4). Deliberately reuses
/// <see cref="IDepotArtifactRepository"/> and <see cref="IApplianceStateRepository"/>
/// rather than a new store; <see cref="LibraryPresenceEvaluator"/> owns the mode-aware
/// projection logic so it stays unit-testable without Postgres.
/// </summary>
[ApiController]
[Route("api/v1/library")]
public sealed class LibraryController : ControllerBase
{
	/// <summary>
	/// High enough that a real deployment's depot catalog (hundreds, not tens of
	/// thousands of artifacts -- see CatalogController's own listing) fits in one page;
	/// the Library tab has no server-side pagination UI in this issue's scope (prototype
	/// screen 7 renders the whole table with a client-side search/filter, not a pager).
	/// </summary>
	private const int LibraryPageLimit = 500;

	private readonly IDepotArtifactRepository _artifacts;
	private readonly IApplianceStateRepository _applianceState;

	public LibraryController(IDepotArtifactRepository artifacts, IApplianceStateRepository applianceState)
	{
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(applianceState);
		_artifacts = artifacts;
		_applianceState = applianceState;
	}

	/// <summary>
	/// The Repository tab's item list plus the product-family rail, evaluated against the
	/// current <c>appliance_state.mode</c> (docs/api-contract.md: "Presence model per
	/// mode"). A missing/deleted <c>appliance_state</c> row (see
	/// <see cref="IApplianceStateRepository.GetAsync"/>'s doc comment) is treated as
	/// disconnected -- the same fail-safe <c>ScheduleDispatchService</c> and the frontend's
	/// <c>ModeState</c> already use: never assume connected without positive confirmation.
	/// </summary>
	[HttpGet("items")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(LibraryItemsResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<LibraryItemsResponse>> ListItems(CancellationToken cancellationToken)
	{
		bool connected = await IsConnectedAsync(cancellationToken).ConfigureAwait(false);

		IReadOnlyList<LibraryItem> items = await LoadLibraryItemsAsync(connected, cancellationToken).ConfigureAwait(false);
		IReadOnlyList<LibraryFamily> families = LibraryPresenceEvaluator.GroupByFamily(items);

		return Ok(new LibraryItemsResponse(
			connected ? "connected" : "disconnected",
			items.Select(LibraryItemResponse.FromDomain).ToArray(),
			families.Select(LibraryFamilyResponse.FromDomain).ToArray()));
	}

	/// <summary>
	/// The air-gapped "Export request manifest" action (docs/api-contract.md
	/// `/library/request-manifest`; prototype screen 7's primary action when
	/// disconnected): a machine-readable want-list of everything not currently present,
	/// meant to be handed to a connected instance (e.g. to pre-seed its
	/// <c>/downloads</c> queue). Available in either mode -- a connected operator may
	/// still want to preview/export the same want-list (e.g. before flipping air-gapped,
	/// or to hand to a separate air-gapped instance's connected peer) -- but the entries
	/// always describe artifacts this appliance itself does not yet have, regardless of
	/// which mode produced them.
	/// </summary>
	[HttpGet("request-manifest")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(LibraryRequestManifestResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<LibraryRequestManifestResponse>> RequestManifest(CancellationToken cancellationToken)
	{
		bool connected = await IsConnectedAsync(cancellationToken).ConfigureAwait(false);
		IReadOnlyList<LibraryItem> items = await LoadLibraryItemsAsync(connected, cancellationToken).ConfigureAwait(false);

		LibraryRequestManifestEntry[] wanted = items
			.Where(i => i.Presence is LibraryPresenceStates.InDepot or LibraryPresenceStates.Missing)
			.Select(i => new LibraryRequestManifestEntry(i.ExternalId, i.Product, i.Version, i.Provenance))
			.ToArray();

		return Ok(new LibraryRequestManifestResponse(
			DateTimeOffset.UtcNow,
			connected ? "connected" : "disconnected",
			wanted));
	}

	private async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
	{
		ApplianceState? state = await _applianceState.GetAsync(cancellationToken).ConfigureAwait(false);
		return string.Equals(state?.Mode, "connected", StringComparison.Ordinal);
	}

	private async Task<IReadOnlyList<LibraryItem>> LoadLibraryItemsAsync(bool connected, CancellationToken cancellationToken)
	{
		(IReadOnlyList<DepotArtifact> artifacts, _) = await _artifacts
			.ListAsync(new DepotArtifactFilter(null, null, null), new PageRequest { Limit = LibraryPageLimit }, cancellationToken)
			.ConfigureAwait(false);
		return LibraryPresenceEvaluator.Evaluate(artifacts, connected);
	}
}
