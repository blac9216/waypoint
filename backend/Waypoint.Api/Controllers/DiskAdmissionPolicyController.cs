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
using Microsoft.AspNetCore.Mvc;
using Waypoint.Api.Contracts;
using Waypoint.Core.Authorization;
using Waypoint.Core.Capacity;
using Waypoint.Core.Errors;

namespace Waypoint.Api.Controllers;

/// <summary>
/// Issue #1531 (epic #1180, split from #1042): read/set the disk-admission reserve
/// from #1529's <see cref="IDiskAdmissionPolicyRepository"/> -- Admin-only, mirroring
/// <see cref="RetentionPolicyController"/>'s shape exactly, per this issue's AC. The
/// new value is honored by the very next <see cref="IDiskAdmissionService.AdmitAsync"/>
/// call: nothing here caches the singleton row.
/// </summary>
[ApiController]
[Route("api/v1/disk-admission-policy")]
public sealed class DiskAdmissionPolicyController : ControllerBase
{
	private readonly IDiskAdmissionPolicyRepository _policy;

	public DiskAdmissionPolicyController(IDiskAdmissionPolicyRepository policy)
	{
		ArgumentNullException.ThrowIfNull(policy);
		_policy = policy;
	}

	/// <summary>Reads the current disk-admission reserve.</summary>
	[HttpGet]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DiskAdmissionPolicyResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DiskAdmissionPolicyResponse>> Get(CancellationToken cancellationToken)
	{
		DiskAdmissionPolicy? policy = await _policy.GetAsync(cancellationToken).ConfigureAwait(false);
		if (policy is null)
		{
			// Migration 20260913060100 seeds this row unconditionally and nothing
			// deletes it -- reaching here means the singleton was removed out of band,
			// a server-side integrity problem, matching RetentionPolicyController.Get's
			// own contract for its sibling singleton.
			throw new ApiException(HttpStatusCode.InternalServerError, "disk_admission_policy_missing", "disk_admission_policy row is missing.");
		}

		return Ok(Map(policy));
	}

	/// <summary>
	/// Sets the disk-admission reserve (AC3). Rejects a negative byte count as a 400
	/// <c>validation_error</c> before calling the repository, so
	/// <see cref="IDiskAdmissionPolicyRepository.SetAsync"/>'s own
	/// <see cref="ArgumentOutOfRangeException"/> guard is never reached from this path.
	/// </summary>
	[HttpPut]
	[RequireAdminRole]
	[ProducesResponseType(typeof(DiskAdmissionPolicyResponse), StatusCodes.Status200OK)]
	public async Task<ActionResult<DiskAdmissionPolicyResponse>> Put(
		[FromBody] DiskAdmissionPolicyUpdateRequest? request, CancellationToken cancellationToken)
	{
		if (request?.ReserveBytes is not long reserveBytes || reserveBytes < 0)
		{
			throw ApiException.Validation(
				"Disk-admission reserve must be a non-negative byte count.",
				"Set \"reserve_bytes\" to a non-negative integer in the request body.");
		}

		string actor = User.GetRequiredUsername();
		DiskAdmissionPolicy updated = await _policy.SetAsync(reserveBytes, actor, cancellationToken).ConfigureAwait(false);
		return Ok(Map(updated));
	}

	private static DiskAdmissionPolicyResponse Map(DiskAdmissionPolicy policy) => new(
		ReserveBytes: policy.ReserveBytes,
		UpdatedBy: policy.UpdatedBy,
		UpdatedAt: policy.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
}
