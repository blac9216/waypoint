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

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1062 (epic #726 sections 6/7): the Admin-configurable evidence retention
/// singleton (migration 0078) -- default value, actor/timestamp tracking on update,
/// and a grant-drift guard proving <c>retention_policy</c> is genuinely unreachable
/// under BOTH real least-privilege runner roles, the same "42501 that only appears
/// under the real role" class <c>RunRetentionHoldTests</c>' own drift guard exists
/// to catch (see migration 0078's header for the withheld-grants rationale).
/// </summary>
[Collection("Postgres")]
public sealed class RetentionPolicyTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	// Issue #1109: audit_log is append-only (a DB trigger rejects DELETE), and it is
	// shared appliance-wide by the whole "Postgres" xunit collection (one fixture,
	// many test classes/methods interleaved). Rather than truncate it, every actor
	// name this class writes is suffixed with a run-unique id, and
	// ReadRetentionAuditRowsAsync filters by actor -- so each test only ever sees the
	// rows it wrote itself, regardless of what earlier tests left behind.
	private readonly string _runId = Guid.NewGuid().ToString("N");

	private RetentionPolicyRepository _repository = null!;
	private RetentionPolicyService _service = null!;
	private string _complianceRunnerConnectionString = string.Empty;
	private string _downloadRunnerConnectionString = string.Empty;

	public RetentionPolicyTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();

		// retention_policy is a singleton with no FK to runs, so ResetJobEngineDataAsync's
		// TRUNCATE ... CASCADE (scoped from `runs`) never touches it -- reset it back to
		// the seeded default explicitly so a prior test's PUT does not leak into this one.
		await using (NpgsqlConnection reset = new(_fixture.ConnectionString))
		{
			await reset.OpenAsync();
			await using NpgsqlCommand resetCommand = new(
				"UPDATE retention_policy SET evidence_retention_days = 180, updated_by = NULL WHERE id = 1", reset);
			await resetCommand.ExecuteNonQueryAsync();
		}

		_repository = new RetentionPolicyRepository(_fixture.ConnectionString);
		_service = new RetentionPolicyService(_repository);

		NpgsqlConnectionStringBuilder runnerBuilder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_compliance_runner",
			Password = "waypoint_test",
		};
		_complianceRunnerConnectionString = runnerBuilder.ConnectionString;

		NpgsqlConnectionStringBuilder downloadBuilder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = downloadBuilder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task GetAsync_SeededSingleton_DefaultsTo180DaysWithNullUpdatedBy()
	{
		RetentionPolicy? policy = await _repository.GetAsync(CancellationToken.None);

		Assert.NotNull(policy);
		Assert.Equal(180, policy!.EvidenceRetentionDays);
		Assert.Null(policy.UpdatedBy);
	}

	[Fact]
	public async Task SetRetentionAsync_PositiveDays_UpdatesValueActorAndTimestamp()
	{
		RetentionPolicy? before = await _repository.GetAsync(CancellationToken.None);

		SetRetentionPolicyResult result = await _service.SetRetentionAsync(90, "admin-alice", CancellationToken.None);

		Assert.Equal(SetRetentionPolicyOutcome.Updated, result.Outcome);
		Assert.Equal(90, result.Policy!.EvidenceRetentionDays);
		Assert.Equal("admin-alice", result.Policy.UpdatedBy);
		Assert.True(result.Policy.UpdatedAt >= before!.UpdatedAt);

		RetentionPolicy? reread = await _repository.GetAsync(CancellationToken.None);
		Assert.Equal(90, reread!.EvidenceRetentionDays);
		Assert.Equal("admin-alice", reread.UpdatedBy);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-5)]
	public async Task SetRetentionAsync_NonPositiveDays_RejectedWithoutTouchingTheRow(int invalidDays)
	{
		string actor = $"admin-alice-{_runId}";
		SetRetentionPolicyResult result = await _service.SetRetentionAsync(invalidDays, actor, CancellationToken.None);

		Assert.Equal(SetRetentionPolicyOutcome.InvalidRetentionDays, result.Outcome);
		Assert.Null(result.Policy);

		RetentionPolicy? unchanged = await _repository.GetAsync(CancellationToken.None);
		Assert.Equal(180, unchanged!.EvidenceRetentionDays);
		Assert.Null(unchanged.UpdatedBy);

		Assert.Empty(await ReadRetentionAuditRowsAsync(actor));
	}

	/// <summary>
	/// Issue #1109: a positive value below the floor is exactly the typo class this
	/// issue targets (a dropped trailing zero) -- rejected before the repository (and
	/// therefore before any audit_log write), same as the non-positive case above.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(18)]
	[InlineData(29)]
	public async Task SetRetentionAsync_BelowMinimumDays_RejectedWithoutTouchingTheRowOrAuditLog(int belowFloor)
	{
		string actor = $"admin-alice-{_runId}";
		SetRetentionPolicyResult result = await _service.SetRetentionAsync(belowFloor, actor, CancellationToken.None);

		Assert.Equal(SetRetentionPolicyOutcome.BelowMinimum, result.Outcome);
		Assert.Null(result.Policy);

		RetentionPolicy? unchanged = await _repository.GetAsync(CancellationToken.None);
		Assert.Equal(180, unchanged!.EvidenceRetentionDays);
		Assert.Null(unchanged.UpdatedBy);

		Assert.Empty(await ReadRetentionAuditRowsAsync(actor));
	}

	/// <summary>The floor itself (30) is accepted -- BelowMinimum only rejects values strictly under it.</summary>
	[Fact]
	public async Task SetRetentionAsync_ExactlyMinimumDays_Accepted()
	{
		SetRetentionPolicyResult result = await _service.SetRetentionAsync(30, $"admin-alice-{_runId}", CancellationToken.None);

		Assert.Equal(SetRetentionPolicyOutcome.Updated, result.Outcome);
		Assert.Equal(30, result.Policy!.EvidenceRetentionDays);
	}

	/// <summary>
	/// Issue #1109 AC1/AC2: every accepted PUT writes one audit_log row, atomically
	/// with the retention_policy UPDATE, carrying the actor and both the previous and
	/// new day counts.
	/// </summary>
	[Fact]
	public async Task SetRetentionAsync_Change_WritesAuditRowWithActorAndBothValues()
	{
		string actor = $"admin-alice-{_runId}";
		SetRetentionPolicyResult result = await _service.SetRetentionAsync(90, actor, CancellationToken.None);
		Assert.Equal(SetRetentionPolicyOutcome.Updated, result.Outcome);

		List<(string EventType, string Actor, JsonElement Detail)> rows = await ReadRetentionAuditRowsAsync(actor);
		(string EventType, string Actor, JsonElement Detail) row = Assert.Single(rows);

		Assert.Equal("retention_policy.updated", row.EventType);
		Assert.Equal(actor, row.Actor);
		Assert.Equal(180, row.Detail.GetProperty("previous_evidence_retention_days").GetInt32());
		Assert.Equal(90, row.Detail.GetProperty("new_evidence_retention_days").GetInt32());
		Assert.True(row.Detail.GetProperty("changed").GetBoolean());
	}

	/// <summary>
	/// Issue #1109 AC3: a PUT that resubmits the current value is not silently
	/// dropped and not silently misreported as a real change -- it still writes an
	/// audit_log row, honestly marked `changed: false`.
	/// </summary>
	[Fact]
	public async Task SetRetentionAsync_NoOpSameValue_WritesAuditRowMarkedUnchanged()
	{
		string actor = $"admin-alice-{_runId}";
		SetRetentionPolicyResult first = await _service.SetRetentionAsync(90, actor, CancellationToken.None);
		Assert.Equal(SetRetentionPolicyOutcome.Updated, first.Outcome);

		SetRetentionPolicyResult second = await _service.SetRetentionAsync(90, actor, CancellationToken.None);
		Assert.Equal(SetRetentionPolicyOutcome.Updated, second.Outcome);

		List<(string EventType, string Actor, JsonElement Detail)> rows = await ReadRetentionAuditRowsAsync(actor);
		Assert.Equal(2, rows.Count);

		(string EventType, string Actor, JsonElement Detail) noOpRow = rows[1];
		Assert.Equal(actor, noOpRow.Actor);
		Assert.Equal(90, noOpRow.Detail.GetProperty("previous_evidence_retention_days").GetInt32());
		Assert.Equal(90, noOpRow.Detail.GetProperty("new_evidence_retention_days").GetInt32());
		Assert.False(noOpRow.Detail.GetProperty("changed").GetBoolean());
	}

	private async Task<List<(string EventType, string Actor, JsonElement Detail)>> ReadRetentionAuditRowsAsync(string actor)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT event_type, actor, detail FROM audit_log WHERE event_type = 'retention_policy.updated' AND actor = $1 ORDER BY occurred_at", connection);
		command.Parameters.AddWithValue(actor);

		List<(string, string, JsonElement)> rows = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			rows.Add((reader.GetString(0), reader.GetString(1), JsonDocument.Parse(reader.GetString(2)).RootElement));
		}

		return rows;
	}

	/// <summary>
	/// Migration 0078 deliberately withholds every grant on <c>retention_policy</c>
	/// from both runner roles, the same posture as <c>run_retention_holds</c>
	/// (migration 0075) -- deciding and driving the retention sweep is exclusively an
	/// API-side responsibility.
	/// </summary>
	[Fact]
	public Task ComplianceRunnerRole_CannotReadOrWriteRetentionPolicy() =>
		AssertRoleCannotReachRetentionPolicyAsync(_complianceRunnerConnectionString);

	/// <inheritdoc cref="ComplianceRunnerRole_CannotReadOrWriteRetentionPolicy"/>
	[Fact]
	public Task DownloadRunnerRole_CannotReadOrWriteRetentionPolicy() =>
		AssertRoleCannotReachRetentionPolicyAsync(_downloadRunnerConnectionString);

	private static async Task AssertRoleCannotReachRetentionPolicyAsync(string runnerConnectionString)
	{
		await using NpgsqlConnection connection = new(runnerConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand select = new("SELECT evidence_retention_days FROM retention_policy WHERE id = 1", connection))
		{
			PostgresException selectException = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, selectException.SqlState);
		}

		await using (NpgsqlCommand update = new("UPDATE retention_policy SET evidence_retention_days = 30 WHERE id = 1", connection))
		{
			PostgresException updateException = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateException.SqlState);
		}

		await using (NpgsqlCommand insert = new(
			"INSERT INTO retention_policy (id, evidence_retention_days) VALUES (2, 30)", connection))
		{
			PostgresException insertException = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertException.SqlState);
		}
	}
}
