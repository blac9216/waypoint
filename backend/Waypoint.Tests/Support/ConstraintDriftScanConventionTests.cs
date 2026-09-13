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
using System.Text.RegularExpressions;
using Xunit;

namespace Waypoint.Tests.Support;

/// <summary>
/// Issue #1877: nothing previously stopped a new <c>*ConstraintDriftTests</c> class from
/// carrying its own private CHECK IN-list regex instead of the shared, table-scoped
/// <see cref="ConstraintDriftScan"/> (issue #1814) -- #1844 landed exactly that, on
/// <c>main</c>, while PR #1832's consolidation was still under review (see the sibling
/// fix, #1876). This reflects over the test assembly for every
/// <c>*ConstraintDriftTests</c> type, reads that type's own source file, and fails if it
/// parses a CHECK <c>IN (...)</c> value list with a private regex rather than delegating
/// to <see cref="ConstraintDriftScan"/> -- unless the type is named on
/// <see cref="ExemptTypeNamesWithReasons"/>, which is asserted complete against the
/// classes actually found, so the allowlist is the record rather than a silent gap.
/// Documented in <c>docs/process/testing.md</c> under "Constraint-drift guard
/// convention".
/// </summary>
public sealed class ConstraintDriftScanConventionTests
{
	/// <summary>
	/// A private regex that resolves a CHECK's <c>IN (...)</c> value list writes its
	/// pattern with a regex-syntax "IN\s*(" fragment (the literal characters as they
	/// appear in C# source, whether interpolated or verbatim) -- this is what every
	/// defect instance in this repo's history (the pre-#1814 eight sites, and #1844's
	/// <c>PhotonImageIndexConstantsConstraintDriftTests</c> before #1876) looked like.
	/// A synthetic SQL string literal handed TO <see cref="ConstraintDriftScan"/> as a
	/// test fixture (e.g. <c>"CHECK (status IN ('active'))"</c>) does not match this,
	/// because it has no regex metacharacters -- only a hand-rolled parser's pattern
	/// string does.
	/// </summary>
	private static readonly Regex PrivateCheckInListRegexMarker = new(
		@"IN\\s\*\\?\(", RegexOptions.None, TimeSpan.FromSeconds(5));

	/// <summary>
	/// Every <c>*ConstraintDriftTests</c> class that legitimately does not delegate to
	/// <see cref="ConstraintDriftScan"/>, with the reason it is exempt. Kept in sync
	/// with `docs/process/testing.md`'s "Constraint-drift guard convention" section.
	/// </summary>
	private static readonly Dictionary<string, string> ExemptTypeNamesWithReasons = new(StringComparer.Ordinal)
	{
		["ContentLibraryFoldersConstraintDriftTests"] = "asserts a UNIQUE constraint/index exists, not a CHECK IN-list value set",
		["DepotRelativePathsConstraintDriftTests"] = "compares a PowerShell $Script: module-scoped variable, no SQL CHECK involved",
		["ConsumerViewConstraintDriftTests"] = "resolves a CHECK constraint's/index's NAME for an error-mapping guard, not its IN-list value set",
		["ContentLibraryItemsConstraintDriftTests"] = "asserts one named migration file's literal DDL text (Assert.Matches), not a 'latest across all migrations' resolved value list -- no ordering hazard applies",
	};

	[Fact]
	public void EveryConstraintDriftTestsType_EitherDelegatesToConstraintDriftScan_OrIsOnTheStatedExemptionList()
	{
		string testsSourceRoot = FindTestsSourceRoot();

		Type[] driftTestTypes = [.. typeof(ConstraintDriftScanConventionTests).Assembly.GetTypes()
			.Where(type => type.Name.EndsWith("ConstraintDriftTests", StringComparison.Ordinal))];

		Assert.NotEmpty(driftTestTypes);

		List<string> violations = [];
		HashSet<string> seenTypeNames = new(StringComparer.Ordinal);

		foreach (Type type in driftTestTypes)
		{
			seenTypeNames.Add(type.Name);

			if (ExemptTypeNamesWithReasons.ContainsKey(type.Name))
			{
				continue;
			}

			string[] matches = Directory.GetFiles(testsSourceRoot, $"{type.Name}.cs", SearchOption.AllDirectories);
			Assert.True(matches.Length == 1, $"Expected exactly one source file named {type.Name}.cs, found {matches.Length}.");

			string source = File.ReadAllText(matches[0]);

			if (PrivateCheckInListRegexMarker.IsMatch(source))
			{
				violations.Add($"{type.Name} parses a CHECK IN-list with its own regex instead of ConstraintDriftScan.");
			}
			else if (!source.Contains("ConstraintDriftScan.", StringComparison.Ordinal))
			{
				violations.Add(
					$"{type.Name} does not call ConstraintDriftScan and is not on the stated exemption list -- " +
					"add it to ExemptTypeNamesWithReasons with a reason, or route it through ConstraintDriftScan.");
			}
		}

		Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));

		// The allowlist is the record: every name on it must actually exist, so a
		// stale entry (a renamed or deleted class) cannot silently widen the gap this
		// guard exists to close.
		foreach (string exemptName in ExemptTypeNamesWithReasons.Keys)
		{
			Assert.Contains(exemptName, seenTypeNames);
		}
	}

	/// <summary>
	/// Mutation-proof for #1877 AC1: a synthetic <c>*ConstraintDriftTests</c> type
	/// carrying a private CHECK IN-list regex, backed by a real source file on disk
	/// under the test tree, must fail the convention check above. Exercised directly
	/// against the regex marker and the delegation check rather than by injecting a
	/// real assembly type (xunit discovers types once per process), which proves the
	/// same two failure paths the fact above exercises.
	/// </summary>
	[Fact]
	public void PrivateCheckInListRegexMarker_MatchesTheShapeOfThePriorRealDefect()
	{
		const string decoyRegexSource =
			"""Regex pattern = new($@"CONSTRAINT\s+{Regex.Escape(constraintName)}\s+CHECK\s*\(\s*{Regex.Escape(columnName)}\s+IN\s*\(([^)]*)\)");""";

		Assert.Matches(PrivateCheckInListRegexMarker, decoyRegexSource);

		const string delegatingSource =
			"""ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(Table, "photon_image_index_channel_check", "channel")""";

		Assert.DoesNotMatch(PrivateCheckInListRegexMarker, delegatingSource);
	}

	/// <summary>
	/// <c>AppContext.BaseDirectory</c> is the test run's
	/// <c>Waypoint.Tests/bin/&lt;Config&gt;/&lt;TFM&gt;/</c> output directory; four
	/// levels up is <c>backend/</c>, matching the same relative-path convention
	/// <see cref="Waypoint.Tests.Core.Catalog.DepotRelativePathsConstraintDriftTests"/>
	/// already uses to reach a sibling project's source tree.
	/// </summary>
	private static string FindTestsSourceRoot()
	{
		string backendDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
		string projectDir = Path.GetFullPath(Path.Combine(backendDir, "Waypoint.Tests"));

		if (!File.Exists(Path.Combine(projectDir, "Waypoint.Tests.csproj")))
		{
			throw new DirectoryNotFoundException("Could not locate the Waypoint.Tests source directory from " + AppContext.BaseDirectory);
		}

		return projectDir;
	}
}
