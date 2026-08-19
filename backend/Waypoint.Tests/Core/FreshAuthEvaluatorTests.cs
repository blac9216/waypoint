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
using Waypoint.Core.Authorization;
using Xunit;

namespace Waypoint.Tests.Core;

/// <summary>
/// Unit coverage for the freshness decision itself (issue #521) -- see
/// <c>FreshAuthCredentialOverwriteTests</c> for the same three cases proven end to end
/// through the real HTTP pipeline. This file is the fast, no-Postgres level: every
/// branch of <see cref="FreshAuthEvaluator.IsFresh"/> in isolation.
/// </summary>
public sealed class FreshAuthEvaluatorTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
	private static readonly TimeSpan FreshnessWindow = TimeSpan.FromMinutes(5);

	[Fact]
	public void IsFresh_AuthTimeWithinWindow_ReturnsTrue()
	{
		ClaimsPrincipal principal = OidcPrincipal(Now - TimeSpan.FromMinutes(2));

		Assert.True(FreshAuthEvaluator.IsFresh(principal, FreshnessWindow, Now));
	}

	[Fact]
	public void IsFresh_AuthTimeExactlyAtWindowBoundary_ReturnsTrue()
	{
		ClaimsPrincipal principal = OidcPrincipal(Now - FreshnessWindow);

		Assert.True(FreshAuthEvaluator.IsFresh(principal, FreshnessWindow, Now));
	}

	[Fact]
	public void IsFresh_AuthTimeOlderThanWindow_ReturnsFalse()
	{
		ClaimsPrincipal principal = OidcPrincipal(Now - TimeSpan.FromMinutes(30));

		Assert.False(FreshAuthEvaluator.IsFresh(principal, FreshnessWindow, Now));
	}

	[Fact]
	public void IsFresh_MissingAuthTimeClaim_FailsClosed()
	{
		ClaimsPrincipal principal = new(new ClaimsIdentity(claims: [], authenticationType: "Bearer"));

		Assert.False(FreshAuthEvaluator.IsFresh(principal, FreshnessWindow, Now));
	}

	[Fact]
	public void IsFresh_UnparseableAuthTimeClaim_FailsClosed()
	{
		ClaimsIdentity identity = new([new Claim(WaypointClaimTypes.AuthTime, "not-a-number")], "Bearer");
		ClaimsPrincipal principal = new(identity);

		Assert.False(FreshAuthEvaluator.IsFresh(principal, FreshnessWindow, Now));
	}

	[Fact]
	public void IsFresh_LocalSessionScheme_IsAlwaysFresh_EvenWithNoAuthTimeClaim()
	{
		ClaimsIdentity identity = new(claims: [], authenticationType: WaypointClaimTypes.LocalSessionAuthenticationType);
		ClaimsPrincipal principal = new(identity);

		Assert.True(FreshAuthEvaluator.IsFresh(principal, FreshnessWindow, Now));
	}

	private static ClaimsPrincipal OidcPrincipal(DateTimeOffset authTime)
	{
		ClaimsIdentity identity = new(
			[new Claim(WaypointClaimTypes.AuthTime, authTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))],
			"Bearer");
		return new ClaimsPrincipal(identity);
	}
}
