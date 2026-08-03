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

using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Proves issue #107: <c>jobs_running_requires_lease_check</c> makes
/// <c>state = 'running'</c> with a NULL <c>lease_expires_at</c> unrepresentable, closing
/// the stranded-job hole (a row in that shape is reachable by neither the claim query,
/// which filters <c>state = 'queued'</c>, nor the recovery sweep, whose
/// <c>lease_expires_at &lt; now()</c> predicate is NULL, not true, against a NULL
/// lease).
///
/// <see cref="Insert_RunningWithNullLease_ViolatesTheConstraint"/> and
/// <see cref="Update_ToRunningWithNullLease_ViolatesTheConstraint"/> are exactly the two
/// write shapes the issue's repro used against schema v1 (0001 only) to demonstrate the
/// bug -- both succeeded there. Run against only 0001 (temporarily moving
/// 0002_jobs_running_requires_lease.sql out of the embedded resource, see the PR body's
/// "Empirical evidence" section for the exact before/after run), both of these tests
/// fail (the insert/update the CHECK is supposed to reject instead succeeds, so
/// <c>Assert.ThrowsAsync&lt;PostgresException&gt;</c> never observes an exception) --
/// that is the "for each new test, show the failure count against the pre-fix code"
/// proof for this constraint.
/// </summary>
[Collection("Postgres")]
public sealed class JobsRunningRequiresLeaseTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	public JobsRunningRequiresLeaseTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task Insert_RunningWithNullLease_ViolatesTheConstraint()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (job_type, priority, state, lease_expires_at) VALUES ('download', 1, 'running', NULL)", connection);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
		Assert.Equal("jobs_running_requires_lease_check", exception.ConstraintName);
	}

	/// <summary>
	/// The exact repro from the issue body: a job already sitting in the table gets
	/// promoted straight to <c>running</c> without ever going through a claim (e.g. a
	/// hand-rolled query, or a future code path that forgets the atomic claim
	/// discipline). This is the shape #107 says "the schema does not *make*
	/// unreachable" today -- after this migration, it does.
	/// </summary>
	[Fact]
	public async Task Update_ToRunningWithNullLease_ViolatesTheConstraint()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		Guid jobId;
		await using (NpgsqlCommand insert = new(
			"INSERT INTO jobs (job_type, priority, state) VALUES ('download', 1, 'queued') RETURNING id", connection))
		{
			jobId = (Guid)(await insert.ExecuteScalarAsync())!;
		}

		await using NpgsqlCommand update = new(
			"UPDATE jobs SET state = 'running', claimed_by = 'worker-b' WHERE id = $1", connection);
		update.Parameters.AddWithValue(jobId);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal("jobs_running_requires_lease_check", exception.ConstraintName);

		// The row must be genuinely unaffected -- Postgres rolls the whole failed
		// statement back, not just the offending column.
		await using NpgsqlCommand verify = new("SELECT state FROM jobs WHERE id = $1", connection);
		verify.Parameters.AddWithValue(jobId);
		Assert.Equal("queued", (string)(await verify.ExecuteScalarAsync())!);
	}

	[Fact]
	public async Task Insert_RunningWithLease_Succeeds()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"""
			INSERT INTO jobs (job_type, priority, state, claimed_by, lease_expires_at)
			VALUES ('download', 1, 'running', 'worker-a', now() + interval '5 minutes')
			RETURNING id
			""", connection);

		object? id = await command.ExecuteScalarAsync();
		Assert.NotNull(id);
	}

	/// <summary>Every other state is unconstrained by this CHECK -- only 'running' strands.</summary>
	[Theory]
	[InlineData("queued")]
	[InlineData("failed")]
	[InlineData("auth-failed")]
	[InlineData("blocked")]
	[InlineData("cancelled")]
	[InlineData("done")]
	[InlineData("uploaded")]
	[InlineData("attesting")]
	[InlineData("converting")]
	public async Task NonRunningStates_PermitANullLease(string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand command = new(
			"INSERT INTO jobs (job_type, priority, state, lease_expires_at) VALUES ('download', 1, $1, NULL) RETURNING id", connection);
		command.Parameters.AddWithValue(state);

		object? id = await command.ExecuteScalarAsync();
		Assert.NotNull(id);
	}
}
