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
using Waypoint.Core.Downloads;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #1470 (epic #1181, split B of design record #1159): the ESX acquisition
/// subscription/preset MODEL -- platform vocabulary lookup plus subscription CRUD.
/// Neither the tool wrapper (#1459) nor the sync job that will consume these
/// subscriptions (#1484) is implemented here; this controller only manages the
/// preset rows a future sync job reads.
///
/// Platform selection is Admin-scoped (epic #16's approved RBAC design: "Admin:
/// subscriptions/presets ... retention dials"); the vocabulary lookup itself is
/// Viewer+ read-only chrome, matching every other read-only lookup on the downloads
/// surface (<see cref="DownloadsController.GetReadiness"/>).
/// </summary>
[ApiController]
[Route("api/v1/downloads/esx")]
public sealed class EsxAcquisitionController : ControllerBase
{
	private readonly IEsxPlatformVocabularyReader _vocabulary;
	private readonly IEsxAcquisitionSubscriptionRepository _subscriptions;

	public EsxAcquisitionController(
		IEsxPlatformVocabularyReader vocabulary, IEsxAcquisitionSubscriptionRepository subscriptions)
	{
		ArgumentNullException.ThrowIfNull(vocabulary);
		ArgumentNullException.ThrowIfNull(subscriptions);
		_vocabulary = vocabulary;
		_subscriptions = subscriptions;
	}

	/// <summary>
	/// The <c>lcm.esx.supported.host.platforms</c> vendor vocabulary, read fresh at
	/// request time (issue #1470 AC: "never hardcoded").
	/// </summary>
	[HttpGet("platforms")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(EsxPlatformVocabularyResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<EsxPlatformVocabularyResponse>> GetPlatforms(CancellationToken cancellationToken)
	{
		IReadOnlyList<string> platforms = await _vocabulary.GetSupportedPlatformsAsync(cancellationToken).ConfigureAwait(false);
		return Ok(new EsxPlatformVocabularyResponse(platforms));
	}

	[HttpGet("subscriptions")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(EsxAcquisitionSubscriptionResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<EsxAcquisitionSubscriptionResponse>>> ListSubscriptions(CancellationToken cancellationToken)
	{
		IReadOnlyList<EsxAcquisitionSubscription> items = await _subscriptions.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(items.Select(EsxAcquisitionSubscriptionResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// Creates a subscription. Every requested platform key must be a member of the
	/// CURRENT vendor vocabulary (issue #1470 AC: selection is sourced, not
	/// hardcoded) -- an unknown key is a 400, not a silently accepted value.
	/// </summary>
	[HttpPost("subscriptions")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(EsxAcquisitionSubscriptionResponse), StatusCodes.Status201Created)]
	public async Task<ActionResult<EsxAcquisitionSubscriptionResponse>> CreateSubscription(
		CreateEsxAcquisitionSubscriptionRequest request, CancellationToken cancellationToken)
	{
		if (request is null || string.IsNullOrWhiteSpace(request.Name))
		{
			throw ApiException.Validation("A subscription name is required.");
		}

		IReadOnlyList<string> selected = request.SelectedPlatforms ?? [];
		await ValidateAgainstVocabularyAsync(selected, cancellationToken).ConfigureAwait(false);

		EsxAcquisitionSubscription created = await _subscriptions
			.CreateAsync(request.Name, selected, request.Enabled ?? true, cancellationToken).ConfigureAwait(false);

		EsxAcquisitionSubscriptionResponse response = EsxAcquisitionSubscriptionResponse.FromDomain(created);
		return CreatedAtAction(nameof(GetSubscription), new { id = created.Id }, response);
	}

	[HttpGet("subscriptions/{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(EsxAcquisitionSubscriptionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<EsxAcquisitionSubscriptionResponse>> GetSubscription(Guid id, CancellationToken cancellationToken)
	{
		EsxAcquisitionSubscription? subscription = await _subscriptions.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (subscription is null)
		{
			throw ApiException.NotFound("ESX acquisition subscription not found.", $"Subscription '{id}' does not exist.");
		}

		return Ok(EsxAcquisitionSubscriptionResponse.FromDomain(subscription));
	}

	/// <summary>
	/// Partial update. Setting <c>enabled: false</c> alone (the disable action) never
	/// deletes the row or touches <c>selected_platforms</c>/<c>name</c> -- issue #1470
	/// AC "disabling a subscription doesn't delete its history".
	/// </summary>
	[HttpPatch("subscriptions/{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(EsxAcquisitionSubscriptionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<EsxAcquisitionSubscriptionResponse>> UpdateSubscription(
		Guid id, UpdateEsxAcquisitionSubscriptionRequest request, CancellationToken cancellationToken)
	{
		if (request is null)
		{
			throw ApiException.Validation("A request body is required.");
		}

		if (request.Name is not null && string.IsNullOrWhiteSpace(request.Name))
		{
			throw ApiException.Validation("Subscription name cannot be blank.");
		}

		if (request.SelectedPlatforms is not null)
		{
			await ValidateAgainstVocabularyAsync(request.SelectedPlatforms, cancellationToken).ConfigureAwait(false);
		}

		EsxAcquisitionSubscription? updated = await _subscriptions
			.UpdateAsync(id, request.Name, request.SelectedPlatforms, request.Enabled, cancellationToken).ConfigureAwait(false);
		if (updated is null)
		{
			throw ApiException.NotFound("ESX acquisition subscription not found.", $"Subscription '{id}' does not exist.");
		}

		return Ok(EsxAcquisitionSubscriptionResponse.FromDomain(updated));
	}

	private async Task ValidateAgainstVocabularyAsync(IReadOnlyList<string> selectedPlatforms, CancellationToken cancellationToken)
	{
		if (selectedPlatforms.Count == 0)
		{
			return;
		}

		IReadOnlyList<string> vocabulary = await _vocabulary.GetSupportedPlatformsAsync(cancellationToken).ConfigureAwait(false);
		HashSet<string> valid = new(vocabulary, StringComparer.Ordinal);
		string[] unknown = [.. selectedPlatforms.Where(platform => !valid.Contains(platform)).Distinct(StringComparer.Ordinal)];
		if (unknown.Length > 0)
		{
			throw ApiException.Validation(
				"One or more selected platforms are not in the current vendor vocabulary.",
				$"Unknown platform key(s): {string.Join(", ", unknown)}.");
		}
	}
}
