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

using Waypoint.Api.Controllers;
using Waypoint.Core.Errors;
using Waypoint.Core.Secrets;
using Xunit;

namespace Waypoint.Tests.Api;

/// <summary>
/// security.md control 3, enforced at the serialization layer (epic #8 slice 3):
/// secret material is ABSENT from every credential response type -- not masked,
/// absent. This test walks every response type the controller declares (and every
/// type reachable from them) and fails if a data-bearing property with a
/// secret-suggesting name ever appears. Adding one is a build-red event, not a
/// review catch.
///
/// Issue #189: the happy-path DTOs are not the only response shape a client
/// observes -- every 4xx/5xx from this controller (indeed, every controller) goes
/// out through <c>ErrorHandlingMiddleware</c> as an <see cref="ErrorResponse"/>
/// envelope. That envelope is walked here too, so the no-secret guarantee covers
/// error paths, not just success ones.
///
/// Issue #370 generalized the walk mechanism itself (now <see cref="ResponseShapeWalk"/>)
/// to sweep every controller in the API assembly -- see
/// <see cref="AllControllersResponseShapeTests"/>. This test is kept as the
/// credentials-specific pin: it is the highest-value single controller (it is the
/// only one that ever touches decrypted secret material), and keeping a dedicated,
/// narrowly-scoped assertion here means a regression on this controller specifically
/// still reads as "credentials leaked a secret," not just "some controller did."
/// </summary>
public sealed class CredentialResponseShapeTests
{
	[Fact]
	public void NoCredentialResponseType_CarriesASecretBearingProperty()
	{
		HashSet<Type> reachable = [];
		foreach (Type responseType in ResponseShapeWalk.DeclaredResponseTypes(typeof(CredentialsController)))
		{
			ResponseShapeWalk.Collect(responseType, reachable);
		}

		// The error envelope every 4xx/5xx returns -- not declared via
		// [ProducesResponseType] on the controller, so it must be added explicitly.
		ResponseShapeWalk.Collect(typeof(ErrorResponse), reachable);

		Assert.Contains(typeof(CredentialResponse), reachable);
		Assert.Contains(typeof(ErrorResponse), reachable);
		Assert.Contains(typeof(ErrorDetail), reachable);

		List<string> violations = ResponseShapeWalk.FindSecretSuggestingProperties(reachable);
		Assert.True(
			violations.Count == 0,
			"Data-bearing propert" + (violations.Count == 1 ? "y" : "ies") + " with a secret-suggesting name found -- " +
			"credential responses must not carry secret material (security.md control 3): " +
			string.Join(", ", violations));
	}
}
