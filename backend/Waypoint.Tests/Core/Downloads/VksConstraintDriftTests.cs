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

using Waypoint.Core.Downloads;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Core.Downloads;

/// <summary>
/// Drift guard for <see cref="VksItemSources.All"/>/<see cref="VksReleaseLines.All"/>/
/// <see cref="VksNamingEras.All"/>/<see cref="VksParseStatuses.All"/> against migration
/// 0111's four named CHECK constraints on <c>vks_library_items</c>
/// (<c>vks_library_items_source_check</c>, <c>_release_line_check</c>,
/// <c>_naming_era_check</c>, <c>_parse_status_check</c>), following this repo's
/// convention of parsing the migration SQL itself rather than a live database.
/// Resolution is scoped by BOTH the owning table and the constraint name (#1795 AC1)
/// via the shared <see cref="ConstraintDriftScan"/> helper (issue #1814 -- every
/// <c>*ConstraintDriftTests</c> guard shares that one table-scoped, ALTER-visible
/// helper rather than each carrying its own private copy of the same scan): a CHECK
/// is only read when it is declared inside <c>vks_library_items</c>' own
/// <c>CREATE TABLE</c> body or added to that table by a later
/// <c>ALTER TABLE ... ADD CONSTRAINT</c>, so neither a differently-named nor an
/// identically-named <c>source IN (...)</c> CHECK on some other table can be picked up
/// in its place. Column name alone would not do: four other migrations already declare
/// a <c>source IN (...)</c> CHECK on unrelated tables -- 0036 line 61 unnamed, and
/// 0037 line 32 (<c>managed_tool_installs_source_check</c>), 0052 line 51
/// (<c>benchmark_revisions_source_check</c>) and 0054 line 128
/// (<c>component_observations_source_check</c>) named after their own tables -- and a
/// column-only scan would silently repoint itself at whichever of those sorts last.
/// </summary>
public sealed class VksConstraintDriftTests
{
	private const string Table = "vks_library_items";

	[Fact]
	public void VksItemSourcesAll_IsInLockstepWithSourceCheckConstraintValueSet()
	{
		Assert.Equal(
			VksItemSources.All,
			ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(Table, "vks_library_items_source_check", "source"));
	}

	[Fact]
	public void VksReleaseLinesAll_IsInLockstepWithReleaseLineCheckConstraintValueSet()
	{
		Assert.Equal(
			VksReleaseLines.All,
			ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(Table, "vks_library_items_release_line_check", "release_line"));
	}

	[Fact]
	public void VksNamingErasAll_IsInLockstepWithNamingEraCheckConstraintValueSet()
	{
		Assert.Equal(
			VksNamingEras.All,
			ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(Table, "vks_library_items_naming_era_check", "naming_era"));
	}

	[Fact]
	public void VksParseStatusesAll_IsInLockstepWithParseStatusCheckConstraintValueSet()
	{
		Assert.Equal(
			VksParseStatuses.All,
			ConstraintDriftScan.ParseLatestTableScopedCheckAcrossMigrations(Table, "vks_library_items_parse_status_check", "parse_status"));
	}

	/// <summary>
	/// #1795 AC2: a <c>source IN (...)</c> CHECK on another table, in a HIGHER-numbered
	/// migration than 0111, must never change what this guard reads -- here in its
	/// hardest form, the decoy carrying the REAL constraint's own name
	/// (<c>vks_library_items_source_check</c>) on <c>reviewer_probe_table</c>. That is
	/// the round-2 reviewer's own probe, which turned a name-only guard red with
	/// <c>Actual: ["bogus"]</c>; table scoping is what makes the real
	/// <c>['depot','public']</c> still resolve. The real declaration comes first in
	/// this fixture's text (PR #1832 review round 1, F3) so the test cannot pass
	/// vacuously on a scan that keeps the LAST match regardless of table.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresAnIdenticallyNamedCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public'))
			);

			CREATE TABLE reviewer_probe_table (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('bogus'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(migrations, Table, "vks_library_items_source_check", "source");

		Assert.Equal(["depot", "public"], values);
	}

	/// <summary>
	/// #1795 AC2, the everyday form: a DIFFERENTLY-named <c>source IN (...)</c> CHECK on
	/// another table in a higher-numbered migration -- the shape 0037/0052/0054 actually
	/// ship -- must likewise leave the real value set untouched.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresADifferentlyNamedSourceCheckOnADifferentTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public'))
			);

			CREATE TABLE managed_tool_installs (
			    source TEXT NOT NULL,
			    CONSTRAINT managed_tool_installs_source_check CHECK (source IN ('local-repository', 'depot', 'upload'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(migrations, Table, "vks_library_items_source_check", "source");

		Assert.Equal(["depot", "public"], values);
	}

	/// <summary>
	/// A widened real <c>vks_library_items_source_check</c> constraint value set must
	/// be picked up (i.e. no longer equal <see cref="VksItemSources.All"/>) -- the
	/// mutation-test half of #1795's acceptance criteria, proving this guard actually
	/// fails when the vocabulary genuinely diverges rather than passing vacuously.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_DetectsAGenuinelyWidenedRealConstraint()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public', 'widened'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(migrations, Table, "vks_library_items_source_check", "source");

		Assert.NotEqual(VksItemSources.All, values);
	}

	/// <summary>
	/// Table scoping must not cost ALTER visibility (the hole
	/// <c>VmToolsConstraintDriftTests</c> was corrected for on PR #1765): a later
	/// migration re-declaring the constraint via this repo's
	/// <c>DROP CONSTRAINT</c>/<c>ADD CONSTRAINT</c> idiom (migration 0129's own shape)
	/// sits outside any <c>CREATE TABLE</c> parens and MUST still be read as the latest
	/// declaration -- the one a fully-migrated database actually enforces.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_PicksUpALaterAlterTableDropAddConstraint()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS vks_library_items (
			    source TEXT NOT NULL CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot', 'public'))
			);
			""",
			"""
			ALTER TABLE vks_library_items DROP CONSTRAINT IF EXISTS vks_library_items_source_check;
			ALTER TABLE vks_library_items ADD CONSTRAINT vks_library_items_source_check CHECK (source IN ('depot'));
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(migrations, Table, "vks_library_items_source_check", "source");

		Assert.Equal(["depot"], values);
	}
}
