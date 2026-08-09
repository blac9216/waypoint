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
using Waypoint.Api.Controllers;
using Waypoint.Core.Errors;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// security.md control 3, enforced at the serialization layer (epic #8 slice 3):
/// secret material is ABSENT from every response DTO, repo-wide -- not just
/// <see cref="CredentialsController"/>'s. Issue #189 introduced the walk/heuristic
/// and scoped it to credentials only, explicitly calling out "eventually all
/// controllers" as future work. Issue #370 is that widening: every controller
/// deriving from <see cref="ControllerBase"/> in the <c>Waypoint.Api</c> assembly is
/// discovered via reflection, its declared <c>[ProducesResponseType]</c> types are
/// walked (plus the shared <see cref="ErrorResponse"/> envelope every 4xx/5xx uses),
/// and the same no-secret-field name heuristic from <see cref="ResponseShapeWalk"/>
/// is applied. A secret-suggesting field on ANY controller's response DTO is now a
/// build-red event, not something only caught if it happens to land on
/// CredentialsController.
/// </summary>
public sealed class AllControllersResponseShapeTests
{
	/// <summary>
	/// Fields the name heuristic flags that are, on inspection, not secret material.
	/// Each entry names exactly one "Type.Property" and must carry a comment
	/// justifying why it is safe to exclude -- this is a narrow allowlist, not a
	/// blanket exception for the fragment that matched.
	/// </summary>
	private static readonly HashSet<string> Allowlist = new()
	{
		// AuthContracts.LoginResponse.Token is the session bearer token this
		// dev-grade local-auth endpoint (ADR-0004 rollout note; issue #29 replaces it
		// with Keycloak OIDC) issues TO the caller it just authenticated -- the same
		// shape a Keycloak token endpoint returns. security.md control 3 is about
		// connection/credential secret material read back OUT of storage (vCenter
		// passwords, API keys, etc.); this is the opposite direction and is exactly
		// what a login response is supposed to carry. Distinct from a leaked
		// credential: it identifies the caller's own session, not a stored secret.
		"LoginResponse.Token",
	};

	[Fact]
	public void NoControllerResponseType_CarriesASecretBearingProperty()
	{
		HashSet<Type> reachable = [];
		foreach (Type controllerType in DiscoverControllerTypes())
		{
			foreach (Type responseType in ResponseShapeWalk.DeclaredResponseTypes(controllerType))
			{
				ResponseShapeWalk.Collect(responseType, reachable);
			}
		}

		// The error envelope every 4xx/5xx returns from every controller -- not
		// declared via [ProducesResponseType] anywhere, so it must be added explicitly.
		ResponseShapeWalk.Collect(typeof(ErrorResponse), reachable);

		// Sanity: the sweep actually found controllers and their DTOs, not an empty set.
		Assert.True(reachable.Count > 10, $"Expected the sweep to reach many response DTOs, only found {reachable.Count}.");
		Assert.Contains(typeof(ErrorResponse), reachable);
		Assert.Contains(typeof(ErrorDetail), reachable);

		List<string> violations = ResponseShapeWalk.FindSecretSuggestingProperties(reachable, Allowlist);
		Assert.True(
			violations.Count == 0,
			"Data-bearing propert" + (violations.Count == 1 ? "y" : "ies") + " with a secret-suggesting name found -- " +
			"no controller's response may carry secret material (security.md control 3). If this is a real leak, " +
			"fix the DTO. If it is a false positive, add a narrowly-justified entry to the Allowlist above: " +
			string.Join(", ", violations));
	}

	/// <summary>
	/// Proves the sweep discriminates: a fake response DTO with a secret-shaped field,
	/// fed through the same walk/heuristic, must fail. Without this, an accidentally
	/// no-op walk (e.g. an empty controller list) would pass "green" for the wrong
	/// reason.
	/// </summary>
	[Fact]
	public void Walk_FlagsASecretBearingPropertyOnAFakeDto()
	{
		HashSet<Type> reachable = [];
		ResponseShapeWalk.Collect(typeof(FakeLeakyResponse), reachable);

		List<string> violations = ResponseShapeWalk.FindSecretSuggestingProperties(reachable);

		Assert.Contains("FakeLeakyResponse.Password", violations);
	}

	private static IEnumerable<Type> DiscoverControllerTypes()
	{
		return typeof(CredentialsController).Assembly.GetTypes()
			.Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ControllerBase).IsAssignableFrom(t));
	}

	/// <summary>
	/// Test-only fixture: a fake response DTO carrying an obviously secret-shaped
	/// field, used solely to prove the walk/heuristic actually discriminates rather
	/// than passing vacuously. Never returned by any real controller. Namespaced
	/// under Waypoint.* so <see cref="ResponseShapeWalk.Collect"/> (which only
	/// descends into "Waypoint" types) will walk it.
	/// </summary>
	private sealed record FakeLeakyResponse(string Username, string Password);
}
