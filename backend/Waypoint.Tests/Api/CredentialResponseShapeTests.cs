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

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
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
/// error paths, not just success ones. Widening this to every controller's error
/// paths is a separate, broader sweep -- see issue #189's deferred follow-up.
/// </summary>
public sealed class CredentialResponseShapeTests
{
	private static readonly string[] ForbiddenNameFragments =
		["secret", "password", "token", "ciphertext", "plaintext", "privatekey", "keymaterial"];

	[Fact]
	public void NoCredentialResponseType_CarriesASecretBearingProperty()
	{
		HashSet<Type> reachable = [];
		foreach (Type responseType in DeclaredResponseTypes())
		{
			Collect(responseType, reachable);
		}

		// The error envelope every 4xx/5xx returns -- not declared via
		// [ProducesResponseType] on the controller, so it must be added explicitly.
		Collect(typeof(ErrorResponse), reachable);

		Assert.Contains(typeof(CredentialResponse), reachable);
		Assert.Contains(typeof(ErrorResponse), reachable);
		Assert.Contains(typeof(ErrorDetail), reachable);

		foreach (Type type in reachable)
		{
			foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				// Data-bearing types only: a bool like HasSecret states a fact about
				// the blob without carrying a byte of it.
				bool dataBearing = property.PropertyType == typeof(string)
					|| property.PropertyType == typeof(byte[])
					|| property.PropertyType == typeof(object);
				if (!dataBearing)
				{
					continue;
				}

				string name = property.Name.ToLowerInvariant();
				Assert.False(
					ForbiddenNameFragments.Any(fragment => name.Contains(fragment, StringComparison.Ordinal)),
					$"{type.Name}.{property.Name} is a data-bearing property with a secret-suggesting name -- " +
					"credential responses must not carry secret material (security.md control 3).");
			}
		}
	}

	private static IEnumerable<Type> DeclaredResponseTypes()
	{
		foreach (MethodInfo action in typeof(CredentialsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
		{
			foreach (ProducesResponseTypeAttribute produces in action.GetCustomAttributes<ProducesResponseTypeAttribute>())
			{
				if (produces.Type != typeof(void))
				{
					yield return produces.Type;
				}
			}
		}
	}

	private static void Collect(Type type, HashSet<Type> reachable)
	{
		if (type.IsArray)
		{
			Collect(type.GetElementType()!, reachable);
			return;
		}

		if (type.Namespace?.StartsWith("Waypoint", StringComparison.Ordinal) != true || !reachable.Add(type))
		{
			return;
		}

		foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			Collect(property.PropertyType, reachable);
		}
	}
}
