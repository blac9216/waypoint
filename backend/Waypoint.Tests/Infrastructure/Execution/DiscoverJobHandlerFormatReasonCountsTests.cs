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
using Waypoint.Infrastructure.Discovery;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #1273: <see cref="DiscoverJobHandler"/>'s private <c>FormatReasonCounts</c>
/// helper previously enumerated an <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// directly -- <c>Dictionary&lt;TKey,TValue&gt;</c> enumeration order is explicitly
/// unspecified by .NET's own contract, so the completion note's reason breakdown could
/// reorder without any code change even though today's caller happens to build the
/// dictionary via an upstream <c>OrderBy</c>. This is a pure unit test (no Postgres, no
/// PowerShell) against the helper itself via reflection -- the sibling
/// <c>Waypoint.Infrastructure.Runs.ScanPlannerService.FormatReasonCounts</c> helper is
/// already covered the same way by its own tests and sorts explicitly at render time;
/// this pins <see cref="DiscoverJobHandler"/>'s twin to the identical
/// <see cref="StringComparer.Ordinal"/> key order regardless of the input dictionary's
/// own enumeration/insertion order.
/// </summary>
public sealed class DiscoverJobHandlerFormatReasonCountsTests
{
	[Fact]
	public void SortsReasonsByKey_Ordinal_RegardlessOfInputDictionaryOrder()
	{
		// Deliberately inserted out of ordinal order ("out_of_declared_scope" sorts
		// before "no_exact_version_fact" alphabetically -- "n" < "o" -- so an
		// insertion-order-preserving Dictionary enumerated as-inserted would render the
		// wrong order without the fix).
		Dictionary<string, int> reasonCounts = new()
		{
			["out_of_declared_scope"] = 2,
			["no_exact_version_fact"] = 1,
			["ambiguous"] = 3,
		};

		string formatted = InvokeFormatReasonCounts(reasonCounts);

		Assert.Equal("3 ambiguous, 1 no_exact_version_fact, 2 out_of_declared_scope", formatted);
	}

	[Fact]
	public void SortsReasonsByKey_Ordinal_EvenWhenAlreadyBuiltInThatOrder()
	{
		// The happy path today's real caller (unlinkedByReason) exercises -- confirms
		// the fix is not merely coincidentally correct for the sorted-input case too.
		Dictionary<string, int> reasonCounts = new()
		{
			["ambiguous"] = 3,
			["no_exact_version_fact"] = 1,
			["out_of_declared_scope"] = 2,
		};

		string formatted = InvokeFormatReasonCounts(reasonCounts);

		Assert.Equal("3 ambiguous, 1 no_exact_version_fact, 2 out_of_declared_scope", formatted);
	}

	private static string InvokeFormatReasonCounts(IReadOnlyDictionary<string, int> reasonCounts)
	{
		MethodInfo method = typeof(DiscoverJobHandler).GetMethod(
			"FormatReasonCounts", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IReadOnlyDictionary<string, int>)])
			?? throw new MissingMethodException(nameof(DiscoverJobHandler), "FormatReasonCounts");

		return (string)method.Invoke(null, [reasonCounts])!;
	}
}
