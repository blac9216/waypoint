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
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Errors;
using Waypoint.Core.Pagination;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/config-docs` surface (issue #265, first slice of the #22
/// split): "Filter by kind/profile/layer", "PUT creates a new immutable version",
/// "Full history -- the auditor answer". Reads are Viewer+, matching every other
/// read-mostly resource; writes are Admin, matching "Configuration" screen's role
/// (docs/domain-model.md Roles: Admin has "STIG config", no other role does).
///
/// There is no documented POST -- the contract's PUT is the sole write path, and the
/// first PUT against a not-yet-existing id both creates the (kind, profile, layer) slot
/// and writes @v1 (see <see cref="ConfigDocRepository.SaveAsync"/>). No resolve endpoint
/// here -- that is issue #266.
/// </summary>
[ApiController]
[Route("api/v1/config-docs")]
public sealed partial class ConfigDocsController : ControllerBase
{
	private readonly ConfigDocRepository _configDocs;
	private readonly SiteRepository _sites;
	private readonly TargetRepository _targets;
	private readonly ILogger<ConfigDocsController> _logger;

	public ConfigDocsController(ConfigDocRepository configDocs, SiteRepository sites, TargetRepository targets, ILogger<ConfigDocsController> logger)
	{
		ArgumentNullException.ThrowIfNull(configDocs);
		ArgumentNullException.ThrowIfNull(sites);
		ArgumentNullException.ThrowIfNull(targets);
		ArgumentNullException.ThrowIfNull(logger);
		_configDocs = configDocs;
		_sites = sites;
		_targets = targets;
		_logger = logger;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ConfigDocResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ConfigDocResponse>>> List(
		[FromQuery] string? kind, [FromQuery] string? profile, [FromQuery] string? layer, [FromQuery] PageRequest page, CancellationToken cancellationToken)
	{
		if (kind is not null && !ConfigDocKinds.All.Contains(kind))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "invalid_kind", $"'{kind}' is not a valid kind. Expected one of: {string.Join(", ", ConfigDocKinds.All)}.");
		}

		(string? layerType, Guid? layerRef) = ParseOptionalLayerOrThrow(layer);

		(IReadOnlyList<ConfigDoc> items, long totalCount) = await _configDocs
			.ListAsync(kind, profile, layerType, layerRef, page, cancellationToken).ConfigureAwait(false);
		Response.Headers["X-Total-Count"] = totalCount.ToString(CultureInfo.InvariantCulture);

		List<ConfigDocResponse> responses = [];
		foreach (ConfigDoc doc in items)
		{
			ConfigDocVersion? latest = await _configDocs.GetLatestVersionAsync(doc.Id, cancellationToken).ConfigureAwait(false);
			if (latest is not null)
			{
				responses.Add(ConfigDocResponse.FromDomain(doc, latest));
			}
		}

		return Ok(responses);
	}

	/// <summary>
	/// The EFFECTIVE card (issue #266, second slice of the #22 split): resolves Global ->
	/// Site -> Target for <paramref name="target"/> across all three doc kinds (or just
	/// <paramref name="kind"/> when given), most specific wins per
	/// <see cref="ConfigDocResolver.Resolve"/>. <paramref name="control"/> matches the
	/// documented query shape (docs/api-contract.md: `resolve?profile&amp;control&amp;target`)
	/// but does not change resolution -- config-docs resolve as whole YAML bodies per
	/// (kind, profile, layer), never parsed per-control (docs/domain-model.md: "the schemas
	/// belong to Broadcom/MITRE"); it is accepted for parity with the documented signature
	/// and echoed nowhere else. An expired attestation resolves as "not applied" (falls
	/// through to a less-specific layer or to nothing) and logs a WARN -- docs/domain-model.md:
	/// "the control reports Open, the run logs a WARN, and Results lists expired
	/// attestations explicitly."
	/// </summary>
	[HttpGet("resolve")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ConfigDocResolutionResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ConfigDocResolutionResponse>>> Resolve(
		[FromQuery] string profile, [FromQuery] Guid target, [FromQuery] string? kind, [FromQuery] string? control, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(profile))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_error", "'profile' is required.");
		}

		if (kind is not null && !ConfigDocKinds.All.Contains(kind))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "invalid_kind", $"'{kind}' is not a valid kind. Expected one of: {string.Join(", ", ConfigDocKinds.All)}.");
		}

		Target? targetEntity = await _targets.GetAsync(target, cancellationToken).ConfigureAwait(false);
		if (targetEntity is null)
		{
			throw new ApiException(HttpStatusCode.BadRequest, "invalid_target", $"No target exists with id '{target}'.");
		}

		IReadOnlyCollection<string> kinds = kind is null ? ConfigDocKinds.All : [kind];
		DateTimeOffset now = DateTimeOffset.UtcNow;

		List<ConfigDocResolutionResponse> results = [];
		foreach (string k in kinds)
		{
			ConfigDocWithLatestVersion? global = await _configDocs
				.FindWithLatestVersionAsync(k, profile, ConfigDocLayers.Global, null, cancellationToken).ConfigureAwait(false);
			ConfigDocWithLatestVersion? site = await _configDocs
				.FindWithLatestVersionAsync(k, profile, ConfigDocLayers.Site, targetEntity.SiteId, cancellationToken).ConfigureAwait(false);
			ConfigDocWithLatestVersion? targetDoc = await _configDocs
				.FindWithLatestVersionAsync(k, profile, ConfigDocLayers.Target, target, cancellationToken).ConfigureAwait(false);

			ConfigDocResolution resolution = ConfigDocResolver.Resolve(k, profile, global, site, targetDoc, now);
			if (resolution.AttestationExpired)
			{
				LogExpiredAttestation(profile, target, resolution.AttestationExpiresAt);
			}

			results.Add(ConfigDocResolutionResponse.FromDomain(resolution));
		}

		return Ok(results);
	}

	/// <summary>
	/// A doc row with no versions is an orphan slot from a pre-#270 mid-save crash (the
	/// atomic-write fix makes new orphans unreachable, but does not retroactively clean up
	/// any that already exist) -- treated the same as "no such config-doc" since, from the
	/// caller's perspective, a slot with nothing ever successfully saved to it is
	/// indistinguishable from one that was never created (docs/api-contract.md does not
	/// document a partial-creation state).
	/// </summary>
	[HttpGet("{id:guid}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ConfigDocResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ConfigDocResponse>> Get(Guid id, CancellationToken cancellationToken)
	{
		ConfigDoc? doc = await _configDocs.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (doc is null)
		{
			throw NotFoundError(id);
		}

		ConfigDocVersion? latest = await _configDocs.GetLatestVersionAsync(id, cancellationToken).ConfigureAwait(false);
		if (latest is null)
		{
			throw NotFoundError(id);
		}

		return Ok(ConfigDocResponse.FromDomain(doc, latest));
	}

	/// <summary>See <see cref="Get"/> for why a versionless doc 404s rather than returning an empty array.</summary>
	[HttpGet("{id:guid}/versions")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ConfigDocVersionResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ConfigDocVersionResponse>>> ListVersions(Guid id, CancellationToken cancellationToken)
	{
		ConfigDoc? doc = await _configDocs.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (doc is null)
		{
			throw NotFoundError(id);
		}

		IReadOnlyList<ConfigDocVersion> versions = await _configDocs.ListVersionsAsync(id, cancellationToken).ConfigureAwait(false);
		if (versions.Count == 0)
		{
			throw NotFoundError(id);
		}

		return Ok(versions.Select(ConfigDocVersionResponse.FromDomain).ToArray());
	}

	/// <summary>See <see cref="Get"/> for why a versionless doc 404s here too (same code/message as the unknown-doc case).</summary>
	[HttpGet("{id:guid}/versions/{version:int}")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ConfigDocVersionResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ConfigDocVersionResponse>> GetVersion(Guid id, int version, CancellationToken cancellationToken)
	{
		ConfigDoc? doc = await _configDocs.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (doc is null)
		{
			throw NotFoundError(id);
		}

		ConfigDocVersion? found = await _configDocs.GetVersionAsync(id, version, cancellationToken).ConfigureAwait(false);
		return found is null
			? throw new ApiException(HttpStatusCode.NotFound, "not_found", $"Config-doc '{id}' has no version {version}.")
			: Ok(ConfigDocVersionResponse.FromDomain(found));
	}

	/// <summary>
	/// Writes a new version at <paramref name="id"/>'s slot. The first PUT against an id
	/// with no existing doc creates the slot from the request's kind/profile/layer and
	/// writes @v1; a PUT against an existing id versions that same slot and ignores any
	/// kind/profile/layer in the body (the slot is fixed once created).
	/// </summary>
	[HttpPut("{id:guid}")]
	[RequireAdminRole]
	[ProducesResponseType(typeof(ConfigDocResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<ConfigDocResponse>> Save(Guid id, [FromBody] ConfigDocSaveBody request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		string? validationError = YamlDocumentValidator.Validate(request.Body);
		if (validationError is not null)
		{
			throw new ApiException(HttpStatusCode.BadRequest, "invalid_yaml", validationError);
		}

		ConfigDoc? existing = await _configDocs.GetAsync(id, cancellationToken).ConfigureAwait(false);
		string author = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin";

		ConfigDoc doc;
		ConfigDocVersion version;
		if (existing is not null)
		{
			ConfigDocVersion? saved = await _configDocs.SaveVersionAsync(id, author, request.Body!, cancellationToken).ConfigureAwait(false);
			if (saved is null)
			{
				throw NotFoundError(id);
			}

			doc = existing;
			version = saved;
		}
		else
		{
			if (string.IsNullOrWhiteSpace(request.Kind))
			{
				throw new ApiException(HttpStatusCode.BadRequest, "validation_error", "'kind' is required when creating a new config-doc.");
			}

			if (string.IsNullOrWhiteSpace(request.Profile))
			{
				throw new ApiException(HttpStatusCode.BadRequest, "validation_error", "'profile' is required when creating a new config-doc.");
			}

			(string layerType, Guid? layerRef) = ParseRequiredLayerOrThrow(request.Layer);
			await EnsureLayerRefExistsAsync(layerType, layerRef, cancellationToken).ConfigureAwait(false);

			(ConfigDocSaveOutcome outcome, ConfigDoc? createdDoc, ConfigDocVersion? createdVersion) = await _configDocs
				.SaveAsync(id, request.Kind, request.Profile, layerType, layerRef, author, request.Body!, cancellationToken).ConfigureAwait(false);

			switch (outcome)
			{
				case ConfigDocSaveOutcome.InvalidKind:
					throw new ApiException(HttpStatusCode.BadRequest, "invalid_kind", $"'{request.Kind}' is not a valid kind. Expected one of: {string.Join(", ", ConfigDocKinds.All)}.");
				case ConfigDocSaveOutcome.InvalidLayer:
					throw new ApiException(HttpStatusCode.BadRequest, "invalid_layer", $"'{request.Layer}' is not a valid layer.");
			}

			doc = createdDoc!;
			version = createdVersion!;

			// A concurrent first-save at the same (kind, profile, layer) slot can win the
			// race under a different id than this request's -- the repository resolves
			// that to whichever row actually exists, so this request's id may not be the
			// one that landed. Surface that as a conflict rather than silently returning
			// a resource at a different id than the caller addressed.
			if (doc.Id != id)
			{
				throw new ApiException(
					HttpStatusCode.Conflict, "slot_already_exists",
					$"A config-doc for kind '{request.Kind}', profile '{request.Profile}', layer '{request.Layer}' already exists at a different id ('{doc.Id}'). PUT to that id instead.");
			}
		}

		return Ok(ConfigDocResponse.FromDomain(doc, version));
	}

	/// <summary>
	/// Parses the <c>global|site:{id}|target:{id}</c> wire format
	/// (docs/api-contract.md `/config-docs` filter) for the list endpoint, where a
	/// missing <c>layer</c> query param means "no filter".
	/// </summary>
	private static (string? LayerType, Guid? LayerRef) ParseOptionalLayerOrThrow(string? layer)
	{
		return string.IsNullOrWhiteSpace(layer) ? (null, null) : ParseLayerOrThrow(layer);
	}

	/// <summary>Same wire format as <see cref="ParseOptionalLayerOrThrow"/>, but the save path always requires one.</summary>
	private static (string LayerType, Guid? LayerRef) ParseRequiredLayerOrThrow(string? layer)
	{
		if (string.IsNullOrWhiteSpace(layer))
		{
			throw new ApiException(HttpStatusCode.BadRequest, "validation_error", "'layer' is required (expected 'global', 'site:{id}', or 'target:{id}').");
		}

		return ParseLayerOrThrow(layer);
	}

	private static (string LayerType, Guid? LayerRef) ParseLayerOrThrow(string layer)
	{
		if (layer == ConfigDocLayers.Global)
		{
			return (ConfigDocLayers.Global, null);
		}

		string[] parts = layer.Split(':', 2);
		if (parts.Length == 2 && (parts[0] == ConfigDocLayers.Site || parts[0] == ConfigDocLayers.Target) && Guid.TryParse(parts[1], out Guid parsedRef))
		{
			return (parts[0], parsedRef);
		}

		throw new ApiException(HttpStatusCode.BadRequest, "invalid_layer", $"'{layer}' is not a valid layer. Expected 'global', 'site:{{id}}', or 'target:{{id}}'.");
	}

	private async Task EnsureLayerRefExistsAsync(string layerType, Guid? layerRef, CancellationToken cancellationToken)
	{
		if (layerType == ConfigDocLayers.Site && layerRef.HasValue
			&& await _sites.GetAsync(layerRef.Value, cancellationToken).ConfigureAwait(false) is null)
		{
			throw new ApiException(HttpStatusCode.BadRequest, "invalid_layer", $"No site exists with id '{layerRef}'.");
		}

		if (layerType == ConfigDocLayers.Target && layerRef.HasValue
			&& await _targets.GetAsync(layerRef.Value, cancellationToken).ConfigureAwait(false) is null)
		{
			throw new ApiException(HttpStatusCode.BadRequest, "invalid_layer", $"No target exists with id '{layerRef}'.");
		}
	}

	private static ApiException NotFoundError(Guid id) =>
		new(HttpStatusCode.NotFound, "not_found", $"No config-doc exists with id '{id}'.");

	[LoggerMessage(Level = LogLevel.Warning, Message = "config-doc resolve: expired attestation for profile '{Profile}' target '{Target}' (expired {ExpiresAt:O}) resolved as not-applied")]
	private partial void LogExpiredAttestation(string profile, Guid target, DateTimeOffset? expiresAt);
}
