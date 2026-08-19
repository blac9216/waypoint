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
using Waypoint.Core.Errors;

namespace Waypoint.Core.Authorization;

/// <summary>
/// Resolves the authenticated caller's username for identity-sensitive actions
/// (recording a run's initiator, an audit actor, an ownership comparison). Every
/// such call site must go through <see cref="GetRequiredUsername"/> rather than the
/// <c>User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "admin"</c>
/// idiom it replaces: issue #209's postmortem found that idiom silently collapsed
/// every caller lacking a <see cref="ClaimTypes.Name"/> claim to the literal
/// "admin" — the real bootstrap admin's username — both defeating the ownership
/// check it fed and misattributing audit records to a privileged account.
/// </summary>
public static class ClaimsPrincipalExtensions
{
	/// <summary>
	/// Returns the caller's <see cref="ClaimTypes.Name"/> claim. A
	/// <c>[Require*Role]</c>-guarded action reaching this without a resolvable name
	/// claim indicates a server-side authentication misconfiguration (the handler
	/// authenticated the request but never issued a name claim) — this fails closed
	/// with <see cref="ApiException.Unauthorized"/> rather than guessing an identity.
	/// </summary>
	public static string GetRequiredUsername(this ClaimsPrincipal principal)
	{
		ArgumentNullException.ThrowIfNull(principal);

		// FindFirstValue is an ASP.NET Core extension (Microsoft.AspNetCore.Authentication)
		// this project does not reference; FindFirst is the native ClaimsPrincipal API.
		string? username = principal.FindFirst(ClaimTypes.Name)?.Value;
		if (string.IsNullOrWhiteSpace(username))
		{
			throw ApiException.Unauthorized(
				"Your session does not carry a resolvable identity.",
				"The authenticated principal has no ClaimTypes.Name claim; this is a server-side authentication misconfiguration, not a caller error. Re-authenticate, and report this if it recurs.");
		}

		return username;
	}

	/// <summary>
	/// True when the caller carries a <see cref="WaypointClaimTypes.Role"/> claim at or
	/// above <paramref name="minimum"/>. Uses the same parse-and-compare rule (and the
	/// same fail-closed behaviour on a missing/unparseable claim) as
	/// <see cref="MinimumRoleAuthorizationHandler"/>, so an imperative per-request check
	/// (e.g. a role floor that varies by request body) stays consistent with the
	/// attribute-based policy. A pipeline-level <c>[Require*Role]</c> attribute still
	/// establishes the coarse floor; this refines it for the specific request.
	/// </summary>
	public static bool HasRoleAtLeast(this ClaimsPrincipal principal, WaypointRole minimum)
	{
		ArgumentNullException.ThrowIfNull(principal);

		string? roleClaim = principal.FindFirst(WaypointClaimTypes.Role)?.Value;
		return roleClaim is not null
			&& Enum.TryParse(roleClaim, ignoreCase: true, out WaypointRole role)
			&& role >= minimum;
	}
}
