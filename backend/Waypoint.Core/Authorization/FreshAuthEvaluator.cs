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
using System.Security.Claims;

namespace Waypoint.Core.Authorization;

/// <summary>
/// The single freshness-decision function step-up re-authentication (issue #521)
/// relies on — shared by <see cref="FreshAuthAuthorizationHandler"/> (the declarative
/// ASP.NET Core policy) and <see cref="RequireFreshAuthAttribute.Check"/> (the
/// imperative controller-facing call that can throw the distinct
/// <c>step_up_required</c> <c>ApiException</c>), so the two never drift apart. See
/// <c>docs/security.md</c> "Step-up re-authentication" for the design this implements.
/// </summary>
public static class FreshAuthEvaluator
{
	/// <summary>
	/// True when <paramref name="principal"/>'s authentication counts as fresh enough:
	/// either it authenticated via the dev-flag local-session scheme (no IdP behind it
	/// to re-prompt — an explicit, documented carve-out), or it carries a parseable
	/// <see cref="WaypointClaimTypes.AuthTime"/> claim no older than
	/// <paramref name="freshnessWindow"/>. Fails closed on anything else, including a
	/// missing or unparseable <c>auth_time</c> claim on an OIDC principal.
	/// </summary>
	public static bool IsFresh(ClaimsPrincipal principal, TimeSpan freshnessWindow, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(principal);

		if (string.Equals(
			principal.Identity?.AuthenticationType,
			WaypointClaimTypes.LocalSessionAuthenticationType,
			StringComparison.Ordinal))
		{
			return true;
		}

		string? authTimeClaim = principal.FindFirst(WaypointClaimTypes.AuthTime)?.Value;
		if (string.IsNullOrWhiteSpace(authTimeClaim))
		{
			return false;
		}

		// auth_time is Unix-epoch seconds (OIDC Core §2), the same numeric-string shape
		// JwtSecurityTokenHandler leaves every unmapped numeric claim in.
		if (!long.TryParse(authTimeClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out long authTimeSeconds))
		{
			return false;
		}

		DateTimeOffset authTime = DateTimeOffset.FromUnixTimeSeconds(authTimeSeconds);
		return now - authTime <= freshnessWindow;
	}
}
