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
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Errors;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Sites;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/profiles` surface's inventory read (issue #40 AC "Profile
/// inventory drives the Benchmarks profile list", #559) plus the per-profile control
/// inventory (issue #598, `/profiles/{id}/controls`).
/// </summary>
[ApiController]
[Route("api/v1/profiles")]
public sealed class ProfilesController : ControllerBase
{
	private readonly IProfileRepository _profiles;
	private readonly IProfileControlRepository _profileControls;
	private readonly ConfigDocRepository _configDocs;
	private readonly TargetRepository _targets;

	public ProfilesController(
		IProfileRepository profiles, IProfileControlRepository profileControls, ConfigDocRepository configDocs, TargetRepository targets)
	{
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(profileControls);
		ArgumentNullException.ThrowIfNull(configDocs);
		ArgumentNullException.ThrowIfNull(targets);
		_profiles = profiles;
		_profileControls = profileControls;
		_configDocs = configDocs;
		_targets = targets;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ProfileResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ProfileResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<Profile> profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(profiles.Select(ProfileResponse.FromDomain).ToArray());
	}

	/// <summary>
	/// The per-control inventory (docs/api-contract.md `/profiles/{id}/controls`:
	/// "Control, severity, title, effective input + scope, attest status"). Controls
	/// come from <c>profile_controls</c> (parsed at content-pull time, migration 0038);
	/// an installed profile with zero parsed controls returns an empty array (distinct
	/// from an unknown profile id, which 404s) -- issue #598 AC "empty vs. no-content
	/// distinction".
	///
	/// <paramref name="target"/> is optional. Without it, every row's effective-input/
	/// attest fields are null (no scope to resolve against). With it, this resolves the
	/// profile's Global -> Site -> Target `input`/`attestation` config-docs for that
	/// target once and applies the SAME resolution to every control row -- see
	/// <see cref="ProfileControlResponse"/>'s doc comment for why this is profile-level,
	/// not truly per-control (no per-control structured storage exists to resolve
	/// against).
	/// </summary>
	[HttpGet("{id:guid}/controls")]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ProfileControlResponse[]), StatusCodes.Status200OK)]
	[ProducesResponseType(StatusCodes.Status404NotFound)]
	public async Task<ActionResult<IReadOnlyList<ProfileControlResponse>>> ListControls(
		Guid id, [FromQuery] Guid? target, CancellationToken cancellationToken)
	{
		Profile? profile = await _profiles.GetAsync(id, cancellationToken).ConfigureAwait(false);
		if (profile is null)
		{
			throw new ApiException(HttpStatusCode.NotFound, "not_found", $"No profile exists with id '{id}'.");
		}

		ConfigDocResolution? inputResolution = null;
		ConfigDocResolution? attestationResolution = null;
		if (target.HasValue)
		{
			Target? targetEntity = await _targets.GetAsync(target.Value, cancellationToken).ConfigureAwait(false);
			if (targetEntity is null)
			{
				throw new ApiException(HttpStatusCode.BadRequest, "invalid_target", $"No target exists with id '{target}'.");
			}

			DateTimeOffset now = DateTimeOffset.UtcNow;
			inputResolution = await ResolveAsync(ConfigDocKinds.Input, profile.ProfileKey, targetEntity, now, cancellationToken).ConfigureAwait(false);
			attestationResolution = await ResolveAsync(ConfigDocKinds.Attestation, profile.ProfileKey, targetEntity, now, cancellationToken).ConfigureAwait(false);
		}

		IReadOnlyList<ProfileControl> controls = await _profileControls.ListByProfileAsync(id, cancellationToken).ConfigureAwait(false);
		return Ok(controls.Select(control => ProfileControlResponse.FromDomain(control, inputResolution, attestationResolution)).ToArray());
	}

	private async Task<ConfigDocResolution> ResolveAsync(string kind, string profileKey, Target target, DateTimeOffset now, CancellationToken cancellationToken)
	{
		ConfigDocWithLatestVersion? global = await _configDocs
			.FindWithLatestVersionAsync(kind, profileKey, ConfigDocLayers.Global, null, cancellationToken).ConfigureAwait(false);
		ConfigDocWithLatestVersion? site = await _configDocs
			.FindWithLatestVersionAsync(kind, profileKey, ConfigDocLayers.Site, target.SiteId, cancellationToken).ConfigureAwait(false);
		ConfigDocWithLatestVersion? targetDoc = await _configDocs
			.FindWithLatestVersionAsync(kind, profileKey, ConfigDocLayers.Target, target.Id, cancellationToken).ConfigureAwait(false);

		return ConfigDocResolver.Resolve(kind, profileKey, global, site, targetDoc, now);
	}
}
