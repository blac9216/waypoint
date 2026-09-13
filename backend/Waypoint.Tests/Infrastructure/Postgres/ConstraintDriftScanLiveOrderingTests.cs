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

using Npgsql;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1853: proves the ordering hazard the class doc comment on
/// <see cref="ConstraintDriftScan"/> describes, and that
/// <see cref="ConstraintDriftScan.ParseLatestTableScopedCheckLiveAsync"/> is immune to it
/// while <see cref="ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql"/> is not.
///
/// <para>A from-scratch <see cref="Waypoint.Infrastructure.Data.NpgsqlSchemaMigrator"/>
/// run cannot itself reproduce a real deployment's out-of-order application (it always
/// applies every embedded migration in one pass, in ordinal order), so this test
/// simulates the hazard directly: it applies two <c>DROP CONSTRAINT</c>/<c>ADD
/// CONSTRAINT</c> statements to a real database in the REVERSE of their ordinal-name
/// order -- exactly what happens on a real database when a low-numbered "reserved slot"
/// migration merges and deploys only after a higher-numbered one has already shipped
/// (#1815's Motivation). After that reversed apply, the database actually enforces the
/// LOW-numbered statement's value list (it ran last), but the text scan -- which only
/// ever sees ordinal name order -- reports the HIGH-numbered statement's value list
/// instead. The live read gets it right because it asks Postgres, not the filenames.</para>
/// </summary>
[Collection("Postgres")]
public sealed class ConstraintDriftScanLiveOrderingTests : IAsyncLifetime
{
	private const string Table = "drift_ordering_probe";
	private const string Constraint = "drift_ordering_probe_status_check";

	private readonly PostgresFixture _fixture;

	public ConstraintDriftScanLiveOrderingTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	// xUnit constructs a fresh instance of this class -- and therefore re-runs
	// InitializeAsync -- for EVERY [Fact], but the "Postgres" collection shares one
	// container/database across every class in it (PostgresCollectionDefinition), so
	// this setup must be idempotent against its own prior run rather than assuming a
	// pristine database.
	public async Task InitializeAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand createTable = new(
			$"CREATE TABLE IF NOT EXISTS {Table} (status TEXT NOT NULL CONSTRAINT {Constraint} CHECK (status IN ('placeholder')))",
			connection);
		await createTable.ExecuteNonQueryAsync();

		// Simulate a real deployment's out-of-order application: "0140_widen.sql" (the
		// higher-numbered, ordinally-later migration) is applied FIRST, then
		// "0111_reserved_slot.sql" (the lower-numbered migration, reserved earlier but
		// merged and deployed later) is applied SECOND -- reversed from ordinal order,
		// exactly the scenario #1815's Motivation describes.
		await using NpgsqlCommand applyHigherNumberedFirst = new(
			$"""
			ALTER TABLE {Table} DROP CONSTRAINT IF EXISTS {Constraint};
			ALTER TABLE {Table} ADD CONSTRAINT {Constraint} CHECK (status IN ('active', 'retired'));
			""", connection);
		await applyHigherNumberedFirst.ExecuteNonQueryAsync();

		await using NpgsqlCommand applyLowerNumberedSecond = new(
			$"""
			ALTER TABLE {Table} DROP CONSTRAINT IF EXISTS {Constraint};
			ALTER TABLE {Table} ADD CONSTRAINT {Constraint} CHECK (status IN ('active'));
			""", connection);
		await applyLowerNumberedSecond.ExecuteNonQueryAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// The text scan resolves "latest" by ordinal migration-name order alone: given the
	/// two migration texts in ordinal order (<c>0111_reserved_slot.sql</c> before
	/// <c>0140_widen.sql</c>), it reports 0140's ["active", "retired"] as the latest
	/// declaration -- which is wrong for this database, because 0111 actually ran AFTER
	/// 0140 here and left only ["active"] enforced. This is the failure mode #1853/#1815
	/// exist to eliminate: it demonstrates the text scan's blind spot, it does not
	/// validate correct behavior.
	/// </summary>
	[Fact]
	public void TextScan_MisreadsTheOrdinallyHigherNumberedMigrationAsLatest_WhenApplicationOrderWasReversed()
	{
		string[] migrationsInOrdinalNameOrder =
		[
			$"ALTER TABLE {Table} ADD CONSTRAINT {Constraint} CHECK (status IN ('active'));", // "0111_reserved_slot.sql"
			$"ALTER TABLE {Table} ADD CONSTRAINT {Constraint} CHECK (status IN ('active', 'retired'));", // "0140_widen.sql"
		];

		List<string>? textScanResult = ConstraintDriftScan.ParseLatestTableScopedCheckAcrossSql(
			migrationsInOrdinalNameOrder, Table, Constraint, "status");

		// The database actually enforces ["active"] (asserted below); the text scan
		// reports the ordinally-later ["active", "retired"] instead -- a real mismatch.
		Assert.Equal(["active", "retired"], textScanResult);
	}

	/// <summary>
	/// The live read asks Postgres directly, so it is unaffected by which migration
	/// sorts ordinally later -- it reports whatever the database, having actually run
	/// the reversed sequence above, truly enforces: ["active"].
	/// </summary>
	[Fact]
	public async Task LiveRead_ReportsWhatTheDatabaseActuallyEnforces_RegardlessOfOrdinalNameOrder()
	{
		List<string> liveResult = await ConstraintDriftScan.ParseLatestTableScopedCheckLiveAsync(
			_fixture.ConnectionString, Table, Constraint);

		Assert.Equal(["active"], liveResult);
	}
}
