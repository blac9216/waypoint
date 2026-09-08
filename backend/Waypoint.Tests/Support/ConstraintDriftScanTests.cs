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

using Xunit;

namespace Waypoint.Tests.Support;

/// <summary>
/// Issue #1814: proves <see cref="ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql"/>
/// -- the helper every converted <c>*ConstraintDriftTests</c> guard now shares -- resolves
/// only CHECKs declared on the table it was asked about, and is still visible to a later
/// <c>ALTER TABLE ... ADD CONSTRAINT</c> re-declaration (the ALTER-visibility hazard
/// <c>VmToolsConstraintDriftTests</c>'s round-1 fix hit). Centralised here rather than
/// duplicated per call site: every guard's own doc comment points back to this file for
/// the table-scoping proof, so the eight former copies of the same regex are not joined
/// by eight copies of the same decoy fixture too.
/// </summary>
public sealed class ConstraintDriftScanTests
{
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresIdenticallyNamedCheckOnAnotherTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('active', 'retired'))
			);

			CREATE TABLE other_thing (
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('bogus'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Equal(["active", "retired"], values);
	}

	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresDifferentlyNamedCheckOnTheSameTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('active', 'retired')),
			    kind TEXT NOT NULL CONSTRAINT widget_kind_check CHECK (kind IN ('bogus'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Equal(["active", "retired"], values);
	}

	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_PicksUpALaterAlterTableAddConstraintOnTheSameTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('active', 'retired', 'archived'))
			);
			""",
			"""
			ALTER TABLE widget DROP CONSTRAINT IF EXISTS widget_status_check;
			ALTER TABLE widget ADD CONSTRAINT widget_status_check CHECK (status IN ('active'));
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Equal(["active"], values);
	}

	/// <summary>
	/// The ALTER-visibility counterpart to the first test above: an <c>ALTER TABLE</c>
	/// re-declaration naming a DIFFERENT table than the one the caller asked about must
	/// not be picked up either, even though it shares the same constraint name (this
	/// repo's naming convention would never produce this, but the helper does not rely
	/// on the convention holding to stay table-scoped).
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresAnAlterTableOnAnotherTableEvenWithTheSameConstraintName()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('active', 'retired'))
			);
			""",
			"""
			ALTER TABLE other_thing ADD CONSTRAINT widget_status_check CHECK (status IN ('bogus'));
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Equal(["active", "retired"], values);
	}

	/// <summary>
	/// PR #1832 round-2 finding F10, the defect this file's fixture shape is drawn
	/// directly from: the body walk used to count every raw <c>(</c> and <c>)</c>,
	/// including ones inside <c>--</c> comment prose. A comment in the FIRST table's
	/// body carrying a net extra <c>(</c>, paired with a comment in a LATER table's
	/// body carrying the matching <c>)</c>, made the walk run past the first table's
	/// own closing paren and straight through the second -- and the scan then returned
	/// the SECOND table's CHECK value list for a caller asking about the first, with
	/// nothing asserting. That is the same silent cross-table read #1814 exists to
	/// eliminate. Reproduced against this exact fixture before the fix:
	/// <c>Expected: ["active", "retired"] / Actual: ["bogus"]</c>.
	///
	/// <para>The shape is not invented for the test: <c>0063_component_results.sql</c>
	/// L61/L63 carries exactly this prose-paren pattern inside the
	/// <c>component_results</c> body, the table
	/// <c>ComponentResultStatusConstraintDriftTests</c> guards through this helper --
	/// it merely happens to cancel within that one body today.</para>
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IsNotWalkedOutOfItsOwnBodyByCommentParens()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    -- Unique per (site, name -- an unbalanced open paren in prose.
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('active', 'retired'))
			);

			CREATE TABLE other_thing (
			    -- See the note above) for why this mirrors widget.
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('bogus'))
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Equal(["active", "retired"], values);
	}

	/// <summary>
	/// The literal-and-comment counterpart to the walk test above: a paren inside a
	/// single-quoted DEFAULT (including one written with the <c>''</c> escape), inside
	/// a <c>/* */</c> block comment and inside a quoted identifier must not be counted
	/// either, and a CHECK constraint that exists only as commented-out prose must not
	/// be read as a declaration.
	/// </summary>
	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_IgnoresParensAndDeclarationsInLiteralsAndBlockComments()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    /* Block comment with an unbalanced ( paren. */
			    label TEXT NOT NULL DEFAULT 'left ( paren and an ''escaped'' quote',
			    "weird ) column" TEXT NULL,
			    -- CONSTRAINT widget_status_check CHECK (status IN ('commented_out'))
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('active', 'retired'))
			);

			CREATE TABLE other_thing (
			    /* Matching ) paren, in prose only. */
			    status TEXT NOT NULL CONSTRAINT widget_status_check CHECK (status IN ('bogus'))
			);
			""",
			"""
			-- ALTER TABLE widget ADD CONSTRAINT widget_status_check CHECK (status IN ('also_commented_out'));
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Equal(["active", "retired"], values);
	}

	[Fact]
	public void ParseLatestTableScopedCheckAcrossSql_ReturnsNullWhenTheConstraintIsNeverDeclaredOnTheTable()
	{
		string[] migrations =
		[
			"""
			CREATE TABLE IF NOT EXISTS widget (
			    id UUID PRIMARY KEY
			);
			""",
		];

		List<string>? values = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrations, "widget", "widget_status_check", "status");

		Assert.Null(values);
	}
}
