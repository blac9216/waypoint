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
