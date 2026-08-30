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

using System.Text.RegularExpressions;
using Waypoint.Core.Components;
using Xunit;

namespace Waypoint.Tests.Core.Components;

/// <summary>
/// Round-2 finding T1 on PR #1232: <see cref="CatalogLinkageReasons"/> is a CLOSED
/// vocabulary published in <c>docs/api-contract.md</c> (both the
/// <c>/targets/{id}/discover</c> and <c>/components/{id}</c> rows), exactly like
/// <see cref="Waypoint.Core.Scans.ScanPlanSkipReasons.All"/> and
/// <see cref="ScopeOmissionReasons.All"/>. These are its drift guards: the member list
/// is pinned here, every emitting branch is asserted to be inside
/// <see cref="CatalogLinkageReasons.All"/> where it is produced
/// (<c>DiscoverJobHandlerCatalogLinkageTests</c>), and the doc must publish exactly the
/// same set -- so a fifth reason cannot reach <c>discover.progress</c> without the
/// contract being updated in the same change.
/// </summary>
public sealed class CatalogLinkageReasonsTests
{
	[Fact]
	public void All_IsTheClosedSetOfEveryDeclaredReason()
	{
		Assert.Equal(
			new[]
			{
				CatalogLinkageReasons.NoExactVersionFact,
				CatalogLinkageReasons.OutOfDeclaredScope,
				CatalogLinkageReasons.Ambiguous,
				CatalogLinkageReasons.LookupFailed,
			},
			CatalogLinkageReasons.All);
	}

	/// <summary>
	/// Doc-vs-code drift guard, following the <c>ParityMatrixCompletenessTests</c>
	/// precedent of reading the repository's own documentation from the test:
	/// <c>docs/api-contract.md</c>'s <c>/targets/{id}/discover</c> row publishes this
	/// vocabulary as an explicit pipe-delimited CLOSED set, and it must be exactly
	/// <see cref="CatalogLinkageReasons.All"/> -- same members, same order.
	///
	/// Issue #1272: both directions are guarded, which containment alone did not do.
	/// Each published list is EXTRACTED from its row and compared with
	/// <see cref="Assert.Equal{T}(System.Collections.Generic.IEnumerable{T}, System.Collections.Generic.IEnumerable{T})"/>,
	/// so adding a fifth reason in code without updating the contract fails here, AND so
	/// does adding a fifth value to either doc list that no code declares. The
	/// <c>/components/{id}</c> row enumerates the same set in prose and is extracted and
	/// compared the same way.
	/// </summary>
	[Fact]
	public void ApiContract_PublishesExactlyTheseReasons()
	{
		string doc = File.ReadAllText(FindApiContractDoc());

		// The discover row's literal closed-set enumeration, e.g.
		// the closed `no_exact_version_fact`\|`out_of_declared_scope`\|`ambiguous`\|`lookup_failed` set
		// (the backslash escapes the pipe inside a Markdown table cell).
		Match discoverRow = Regex.Match(doc, @"closed ((?:`[a-z_]+`\\\|)*`[a-z_]+`) set", RegexOptions.None, TimeSpan.FromSeconds(5));
		Assert.True(discoverRow.Success, "docs/api-contract.md no longer publishes a 'closed <list> set' catalog-linkage reason enumeration.");
		Assert.Equal(
			CatalogLinkageReasons.All,
			discoverRow.Groups[1].Value.Split("\\|").Select(token => token.Trim('`')).ToArray());

		// The /components/{id} row's prose enumeration, e.g.
		// "... for every unlinked outcome -- `a`, `b`, `c`, `d` -- not only ...".
		Match componentRow = Regex.Match(doc, @"unlinked outcome — (.+?) —", RegexOptions.None, TimeSpan.FromSeconds(5));
		Assert.True(componentRow.Success, "docs/api-contract.md no longer publishes the catalog-linkage reason prose enumeration on the /components/{id} row.");
		Assert.Equal(
			CatalogLinkageReasons.All,
			Regex.Matches(componentRow.Groups[1].Value, "`([a-z_]+)`", RegexOptions.None, TimeSpan.FromSeconds(5))
				.Select(match => match.Groups[1].Value)
				.ToArray());
	}

	private static string FindApiContractDoc()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, "docs", "api-contract.md");
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException("Could not locate docs/api-contract.md by walking up from AppContext.BaseDirectory");
	}
}
