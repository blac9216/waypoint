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

using System.Net;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.Components;
using Waypoint.Core.Errors;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The docs/api-contract.md planned <c>/targets/{id}/components</c> ·
/// <c>/components/{id}</c> surface (issue #732, epic #726, ADR-0023): the stable
/// endpoint/component identity layer beneath a top-level <see cref="Target"/>. Reads
/// are Viewer+; the only mutation this slice ships is the Admin-only configured-fact
/// write and retired-component purge (docs/api-contract.md: "never lifecycle or
/// identity, which are discovery/refresh-owned").
///
/// Component materialization now runs: <see cref="IComponentRepository.UpsertDiscoveredAsync"/>
/// is called by <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/> on
/// every successful vSphere discovery pass (issue #732's discovery-wiring remainder),
/// so <c>GET /targets/{id}/components</c> reflects real discovered components for any
/// target that has completed at least one discovery boundary since. NSX and other
/// non-vSphere sources remain future work.
/// </summary>
[ApiController]
public sealed class ComponentsController : ControllerBase
{
	private readonly TargetRepository _targets;
	private readonly IComponentRepository _components;
	private readonly ICatalogRepository _catalog;

	public ComponentsController(TargetRepository targets, IComponentRepository components, ICatalogRepository catalog)
	{
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(components);
		ArgumentNullException.ThrowIfNull(catalog);
		_targets = targets;
		_components = components;
		_catalog = catalog;
	}

	/// <summary>
	/// Every known component beneath this target regardless of lifecycle (docs/api-
	/// contract.md: "for Configuration-screen visibility"). Pass
	/// <paramref name="includeRetired"/><c>=false</c> to exclude retired rows for a
	/// scan-scoped view; defaults to including everything since this is the
	/// Configuration-screen superset endpoint, not the scan-scoped one.
	/// </summary>
	[HttpGet("api/v1/targets/{targetId:guid}/components")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComponentResponse[]), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<IReadOnlyList<ComponentResponse>>> ListForTarget(Guid targetId, [FromQuery] bool includeRetired, CancellationToken cancellationToken)
	{
		if (await _targets.GetAsync(targetId, cancellationToken).ConfigureAwait(false) is null)
		{
			throw TargetNotFoundError(targetId);
		}

		IReadOnlyList<Component> items = await _components.ListForTargetAsync(targetId, includeRetired: true, cancellationToken).ConfigureAwait(false);
		if (!includeRetired)
		{
			items = [.. items.Where(c => c.Lifecycle != ComponentLifecycleStates.Retired)];
		}

		return Ok(items.Select(ComponentResponse.FromDomain).ToArray());
	}

	/// <summary>Full component record. 404 when unknown.</summary>
	[HttpGet("api/v1/components/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComponentResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ComponentResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Component? component = await _components.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (component is null)
		{
			throw NotFoundError(id);
		}

		return Ok(ComponentResponse.FromDomain(component));
	}

	/// <summary>
	/// Admin-only: sets <c>configured_fact</c> (the exact product version/capability
	/// Waypoint cannot discover) -- never lifecycle or identity, which stay discovery/
	/// refresh-owned (docs/api-contract.md). Issue #1000: a null/whitespace/omitted
	/// <c>exact_version</c> is an explicit CLEAR, not a validation error -- ADR-0023's
	/// requirement that clearing the configured fact "honestly unlinks" needs a real
	/// way to clear it, which this endpoint never had before (every prior body had to
	/// supply a non-empty value). <see cref="IComponentRepository.SetConfiguredFactAsync"/>
	/// re-resolves catalog linkage either way -- from the new value, or (on clear) from
	/// whatever discovered fact remains.
	/// </summary>
	[HttpPut("api/v1/components/{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ComponentResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ComponentResponse>> Put(Guid id, [FromBody] ComponentConfiguredFactBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		ComponentWriteOutcome outcome = await _components.SetConfiguredFactAsync(id, request.ExactVersion, cancellationToken).ConfigureAwait(false);
		if (outcome == ComponentWriteOutcome.NotFound)
		{
			throw NotFoundError(id);
		}

		Component updated = (await _components.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
		return Ok(ComponentResponse.FromDomain(updated));
	}

	/// <summary>Admin-only audited purge. 409 <c>component_not_retired</c> unless <c>lifecycle == "retired"</c>.</summary>
	[HttpDelete("api/v1/components/{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(StatusCodes.Status204NoContent)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	[ProducesResponseType(StatusCodes.Status409Conflict)]
	public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
	{
		Component? component = await _components.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (component is null)
		{
			throw NotFoundError(id);
		}

		if (component.Lifecycle != ComponentLifecycleStates.Retired)
		{
			throw new ApiException(
				HttpStatusCode.Conflict, "component_not_retired",
				$"Component '{id}' must be retired before it can be purged (current lifecycle: '{component.Lifecycle}').");
		}

		ComponentWriteOutcome outcome = await _components.PurgeRetiredAsync(id, cancellationToken).ConfigureAwait(false);
		return outcome == ComponentWriteOutcome.Ok ? NoContent() : throw NotFoundError(id);
	}

	/// <summary>Immutable discovery/configuration provenance -- audit/troubleshooting read, Cyber+ per docs/api-contract.md.</summary>
	[HttpGet("api/v1/components/{id:guid}/observations")]
	[RequireCyberRole]
	[ProducesResponseType(typeof(ComponentObservationResponse[]), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<IReadOnlyList<ComponentObservationResponse>>> ListObservations(Guid id, CancellationToken cancellationToken)
	{
		if (await _components.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
		{
			throw NotFoundError(id);
		}

		IReadOnlyList<ComponentObservation> observations = await _components.ListObservationsAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(observations.Select(ComponentObservationResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Catalog compatibility for one component (issue #732 AC: "capability matching
	/// against catalog selectors and product/build/version facts ... exact reasons for
	/// unsupported product/build/component/transport combinations"). Viewer+ -- a read
	/// projection over already-persisted catalog + component state, never a mutation.
	/// </summary>
	[HttpGet("api/v1/components/{id:guid}/capability")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ComponentCapabilityResponse), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<ComponentCapabilityResponse>> GetCapability(Guid id, CancellationToken cancellationToken)
	{
		Component? component = await _components.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (component is null)
		{
			throw NotFoundError(id);
		}

		// Issue #741: a catalog-declared child (named VCSA service) inherits the parent
		// appliance component's version facts through the same shared rule
		// ScopeResolutionService applies, so this read surface and scope resolution can
		// never disagree about a child's capability.
		if (ComponentFactInheritance.IsCatalogDeclaredChild(component) && component.ParentComponentId is { } parentComponentId)
		{
			Component? parent = await _components.GetAsync(parentComponentId, cancellationToken).ConfigureAwait(false);
			if (parent is not null)
			{
				component = ComponentFactInheritance.WithInheritedFacts(component, parent);
			}
		}

		Guid? linkedProductVersionId = null;
		string? linkedProductVersionKey = null;
		IReadOnlyList<CatalogExecutionProfileDetail> candidateProfiles = [];
		if (component.CatalogComponentId is { } catalogComponentId)
		{
			candidateProfiles = await _catalog.ListExecutionProfilesByComponentAsync(catalogComponentId, cancellationToken).ConfigureAwait(false);
			if (candidateProfiles.Count > 0)
			{
				linkedProductVersionId = candidateProfiles[0].ProductVersion.Id;
				linkedProductVersionKey = candidateProfiles[0].ProductVersion.VersionKey;
			}
		}

		ComponentCapabilityMatch match = ComponentCapabilityMatcher.Match(component, linkedProductVersionId, linkedProductVersionKey, candidateProfiles);
		return Ok(ComponentCapabilityResponse.FromDomain(match));
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No component exists with id '{id}'.");

	private static ApiException TargetNotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No target exists with id '{id}'.");
}
