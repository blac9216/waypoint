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

namespace Waypoint.Tests.Api;

/// <summary>
/// Shared walk mechanism behind the no-secret response-shape guarantee
/// (security.md control 3, enforced at the serialization layer -- epic #8 slice 3).
/// Originally written for <see cref="CredentialResponseShapeTests"/> (issue #189) and
/// extracted here so the repo-wide sweep (issue #370) reuses the exact same
/// walk/heuristic instead of re-implementing it.
/// </summary>
internal static class ResponseShapeWalk
{
	/// <summary>
	/// Name fragments that mark a data-bearing property as carrying secret material.
	///
	/// "token" is included even though it also matches legitimate non-secret uses
	/// elsewhere in the codebase (session/bearer tokens issued *to* an authenticated
	/// caller, not connection-credential material read back *from* storage) -- see the
	/// narrow, justified exclusion for <c>AuthContracts.LoginResponse.Token</c> in
	/// <see cref="AllControllersResponseShapeTests"/>. The fragment stays in the
	/// heuristic because a `token` field on a *credential* or *config-doc* response
	/// would be exactly the leak this test exists to catch.
	/// </summary>
	private static readonly string[] ForbiddenNameFragments =
		["secret", "password", "token", "ciphertext", "plaintext", "privatekey", "keymaterial"];

	/// <summary>
	/// Returns every <c>[ProducesResponseType]</c>-declared response type across every
	/// public, non-void action method on <paramref name="controllerType"/>.
	/// </summary>
	public static IEnumerable<Type> DeclaredResponseTypes(Type controllerType)
	{
		foreach (MethodInfo action in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
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

	/// <summary>
	/// Recursively collects <paramref name="type"/> and every <c>Waypoint*</c> type
	/// reachable from its public instance properties (unwrapping arrays) into
	/// <paramref name="reachable"/>.
	/// </summary>
	public static void Collect(Type type, HashSet<Type> reachable)
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

	/// <summary>
	/// Returns every secret-suggesting, data-bearing property found across
	/// <paramref name="reachable"/>, formatted as "<c>Type.Property</c>", excluding
	/// any property whose fully-qualified "<c>Type.Property</c>" name appears in
	/// <paramref name="allowlist"/>.
	/// </summary>
	public static List<string> FindSecretSuggestingProperties(IEnumerable<Type> reachable, IReadOnlySet<string>? allowlist = null)
	{
		List<string> violations = [];
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

				string qualifiedName = $"{type.Name}.{property.Name}";
				if (allowlist?.Contains(qualifiedName) == true)
				{
					continue;
				}

				string name = property.Name.ToLowerInvariant();
				if (ForbiddenNameFragments.Any(fragment => name.Contains(fragment, StringComparison.Ordinal)))
				{
					violations.Add(qualifiedName);
				}
			}
		}

		return violations;
	}
}
