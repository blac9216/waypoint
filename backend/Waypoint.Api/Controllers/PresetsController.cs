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

using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Errors;
using Waypoint.Core.Subscriptions;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #1450 (epic #1182, split from design record #1045): shipped presets are
/// read-only (Viewer+); <c>POST .../clone</c> produces an independent custom preset an
/// Admin can then edit via <c>PUT</c> -- a shipped preset itself is never writable
/// through this API (it updates only via appliance updates). There is deliberately no
/// <c>DELETE</c> here: <see cref="IPresetRepository.DeleteAsync"/> exists for a future
/// consumer, but #1450's own Proposed Changes names only list/get/clone/edit-clone.
/// </summary>
[ApiController]
[Route("api/v1/presets")]
public sealed class PresetsController : ControllerBase
{
	private readonly PresetService _service;

	public PresetsController(PresetService service)
	{
		ArgumentNullException.ThrowIfNull(service);
		_service = service;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(IReadOnlyList<PresetResponse>), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<PresetResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<Preset> presets = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(presets.Select(Map).ToList());
	}

	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(PresetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<PresetResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		Preset? preset = await _service.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (preset is null)
		{
			throw ApiException.NotFound($"Preset '{id}' was not found.");
		}
		return Ok(Map(preset));
	}

	[HttpPost("{id:guid}/clone")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(PresetResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<PresetResponse>> Clone(Guid id, [FromBody] PresetCloneRequest? request, CancellationToken cancellationToken)
	{
		PresetWriteResult result = await _service.CloneAsync(id, request?.Name, cancellationToken).ConfigureAwait(false);
		return result.Outcome switch
		{
			PresetWriteOutcome.Ok => CreatedAtAction(nameof(Get), new { id = result.Preset!.Id }, Map(result.Preset)),
			PresetWriteOutcome.NotFound => throw ApiException.NotFound(result.Error!),
			_ => throw ApiException.Validation(result.Error!),
		};
	}

	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(PresetResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<PresetResponse>> Update(Guid id, [FromBody] PresetUpdateRequest? request, CancellationToken cancellationToken)
	{
		PresetUpdateFields fields = new(request?.Name, request?.LineGranularity, request?.AnchorVersion);
		PresetWriteResult result = await _service.UpdateAsync(id, fields, cancellationToken).ConfigureAwait(false);
		return result.Outcome switch
		{
			PresetWriteOutcome.Ok => Ok(Map(result.Preset!)),
			PresetWriteOutcome.NotFound => throw ApiException.NotFound(result.Error!),
			PresetWriteOutcome.NotCustom => throw new ApiException(System.Net.HttpStatusCode.Conflict, "preset_not_custom", result.Error!),
			_ => throw ApiException.Validation(result.Error!),
		};
	}

	private static PresetResponse Map(Preset preset) => new(
		Id: preset.Id,
		Stack: preset.Stack,
		Generation: preset.Generation,
		Name: preset.Name,
		LineGranularity: SubscriptionLineGranularityValues.ToDbValue(preset.LineGranularity),
		AnchorVersion: preset.AnchorVersion,
		IsCustom: preset.IsCustom,
		SourcePresetId: preset.SourcePresetId,
		CreatedAt: preset.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
		UpdatedAt: preset.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
}
