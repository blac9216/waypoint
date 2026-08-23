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
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Api.Controllers;

/// <summary>
/// The api-contract.md `/profiles` surface's inventory read (issue #40 AC "Profile
/// inventory drives the Benchmarks profile list", #559). Only the list endpoint lands
/// in this slice; per-profile controls detail is a separate, later concern
/// (docs/api-contract.md `/profiles/{id}/controls`).
/// </summary>
[ApiController]
[Route("api/v1/profiles")]
public sealed class ProfilesController : ControllerBase
{
	private readonly IProfileRepository _profiles;

	public ProfilesController(IProfileRepository profiles)
	{
		ArgumentNullException.ThrowIfNull(profiles);
		_profiles = profiles;
	}

	[HttpGet]
	[RequireViewerRole]
	[ProducesResponseType(typeof(ProfileResponse[]), StatusCodes.Status200OK)]
	public async Task<ActionResult<IReadOnlyList<ProfileResponse>>> List(CancellationToken cancellationToken)
	{
		IReadOnlyList<Profile> profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
		return Ok(profiles.Select(ProfileResponse.FromDomain).ToArray());
	}
}
