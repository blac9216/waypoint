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
using Waypoint.Core.Runs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #784 (epic #726): service-level place/remove validation
/// (<see cref="RunRetentionHoldService"/>), the audited transition trail this backs
/// (both directions land in the EXISTING <c>audit_log</c> table -- no new audit
/// table), and a grant-drift guard proving <c>run_retention_holds</c> is genuinely
/// unreachable under the REAL least-privilege <c>waypoint_compliance_runner</c> role
/// (deliberately withheld per migration 0075's header) -- the exact "42501 that only
/// appears under the real role" failure class this repository's own conventions call
/// out (<c>RunnerRoleGrantDriftTests</c>' doc comment). The purge-exclusion path
/// itself (the complete evidence graph surviving a held purge attempt) is covered in
/// <c>RunPurgeComplianceEvidenceTests</c> alongside its sibling purge tests, against
/// the same real schema.
/// </summary>
[Collection("Postgres")]
public sealed class RunRetentionHoldTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private JobQueueRepository _jobs = null!;
	private RunRetentionHoldRepository _holds = null!;
	private RunPurgeRepository _purges = null!;
	private RunRetentionHoldService _service = null!;
	private string _complianceRunnerConnectionString = string.Empty;
	private string _downloadRunnerConnectionString = string.Empty;

	public RunRetentionHoldTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE run_retention_holds, audit_log, jobs, runs RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();

		_jobs = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_holds = new RunRetentionHoldRepository(_fixture.ConnectionString);
		_purges = new RunPurgeRepository(_fixture.ConnectionString);
		_service = new RunRetentionHoldService(_jobs, _holds, _purges);

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
	public async Task PlaceHoldAsync_RunNotFound_ReturnsRunNotFound()
	{
		PlaceRetentionHoldResult result = await _service.PlaceHoldAsync(Guid.NewGuid(), "invented-reason", "admin-tester", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.RunNotFound, result.Outcome);
	}

	[Fact]
	public async Task PlaceHoldAsync_NonComplianceRunType_ReturnsUnsupportedRunType()
	{
		Guid runId = await SeedRunAsync("download", "completed");
		PlaceRetentionHoldResult result = await _service.PlaceHoldAsync(runId, "invented-reason", "admin-tester", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.UnsupportedRunType, result.Outcome);
	}

	[Fact]
	public async Task PlaceHoldAsync_NonTerminalRun_ReturnsRunNotTerminal()
	{
		Guid runId = await SeedRunAsync("scan", "running");
		PlaceRetentionHoldResult result = await _service.PlaceHoldAsync(runId, "invented-reason", "admin-tester", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.RunNotTerminal, result.Outcome);
	}

	[Fact]
	public async Task PlaceThenRemoveHoldAsync_AuditsBothDirectionsWithActorTimeReasonDirection()
	{
		Guid runId = await SeedRunAsync("scan", "completed");

		PlaceRetentionHoldResult placed = await _service.PlaceHoldAsync(runId, "invented-legal-hold-reason", "admin-alice", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.Placed, placed.Outcome);
		Assert.NotNull(placed.Hold);
		Assert.Equal("invented-legal-hold-reason", placed.Hold!.Reason);
		Assert.Equal("admin-alice", placed.Hold.PlacedBy);

		RunRetentionHold? current = await _service.GetHoldAsync(runId, CancellationToken.None);
		Assert.NotNull(current);

		// AC2: placing again while already held is a no-op that reports the EXISTING
		// hold, not a second audited transition.
		PlaceRetentionHoldResult placedAgain = await _service.PlaceHoldAsync(runId, "invented-second-reason-ignored", "admin-bob", CancellationToken.None);
		Assert.Equal(PlaceRetentionHoldOutcome.AlreadyHeld, placedAgain.Outcome);
		Assert.Equal("admin-alice", placedAgain.Hold!.PlacedBy);

		RemoveRetentionHoldResult removed = await _service.RemoveHoldAsync(runId, "invented-unhold-reason", "admin-alice", CancellationToken.None);
		Assert.Equal(RemoveRetentionHoldOutcome.Removed, removed.Outcome);
		Assert.Null(await _service.GetHoldAsync(runId, CancellationToken.None));

		// AC4: normal retention eligibility resumes -- proven here as "no hold row
		// remains"; RunPurgeComplianceEvidenceTests proves the purge itself then
		// succeeds.
		RemoveRetentionHoldResult removedAgain = await _service.RemoveHoldAsync(runId, "invented-second-unhold-reason", "admin-alice", CancellationToken.None);
		Assert.Equal(RemoveRetentionHoldOutcome.NotHeld, removedAgain.Outcome);

		// AC5: every transition -- actor, time, reason, direction -- is audited. Two
		// rows only: the one successful place, the one successful remove (neither the
		// idempotent re-place nor the idempotent re-remove writes a second row).
		List<(string EventType, string Actor, string Detail)> events = await ReadAuditEventsAsync(runId);
		Assert.Equal(2, events.Count);
		Assert.Equal(("retention_hold_placed", "admin-alice", "invented-legal-hold-reason"), (events[0].EventType, events[0].Actor, ExtractReason(events[0].Detail)));
		Assert.Equal(("retention_hold_removed", "admin-alice", "invented-unhold-reason"), (events[1].EventType, events[1].Actor, ExtractReason(events[1].Detail)));
	}

	/// <summary>
	/// Migration 0075 deliberately withholds every grant on <c>run_retention_holds</c>
	/// from BOTH runner roles -- neither reading nor writing this table is ever a
	/// runner-side operation. Proven against the REAL roles, not the migration owner: a
	/// bare SELECT/INSERT/DELETE as either role must fail <c>42501</c>, the exact class
	/// of drift this repository's own <c>RunnerRoleGrantDriftTests</c> convention exists
	/// to catch before it ships. Both roles are covered because the migration header
	/// claims both -- <c>RunnerRoleGrantDriftTests</c> sets that same precedent by
	/// covering <c>waypoint_download_runner</c> for its own tables.
	///
	/// This denial is also the reason the hold needs a third enforcement point: the
	/// compliance-runner literally cannot re-check a hold when it claims an
	/// artifact-deletion job, so <see cref="RunRetentionHoldService.PlaceHoldAsync"/>
	/// cancels that job API-side instead (pinned by
	/// <c>RunPurgeServiceTests.PlaceHoldAsync_WithArtifactJobStillQueued_CancelsItSoTheRunnerNeverClaimsIt</c>).
	/// </summary>
	[Fact]
	public Task ComplianceRunnerRole_CannotReadOrWriteRunRetentionHolds() =>
		AssertRoleCannotReachRunRetentionHoldsAsync(_complianceRunnerConnectionString);

	/// <inheritdoc cref="ComplianceRunnerRole_CannotReadOrWriteRunRetentionHolds"/>
	[Fact]
	public Task DownloadRunnerRole_CannotReadOrWriteRunRetentionHolds() =>
		AssertRoleCannotReachRunRetentionHoldsAsync(_downloadRunnerConnectionString);

	private async Task AssertRoleCannotReachRunRetentionHoldsAsync(string runnerConnectionString)
	{
		Guid runId = await SeedRunAsync("scan", "completed");

		await using NpgsqlConnection connection = new(runnerConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand select = new("SELECT run_id FROM run_retention_holds WHERE run_id = $1", connection))
		{
			select.Parameters.AddWithValue(runId);
			PostgresException selectException = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, selectException.SqlState);
		}

		await using (NpgsqlCommand insert = new(
			"INSERT INTO run_retention_holds (run_id, reason, placed_by) VALUES ($1, 'invented-reason', 'runner')", connection))
		{
			insert.Parameters.AddWithValue(runId);
			PostgresException insertException = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertException.SqlState);
		}

		await using (NpgsqlCommand delete = new("DELETE FROM run_retention_holds WHERE run_id = $1", connection))
		{
			delete.Parameters.AddWithValue(runId);
			PostgresException deleteException = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteException.SqlState);
		}
	}

	private async Task<Guid> SeedRunAsync(string runType, string state)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"INSERT INTO runs (run_type, scope, state) VALUES ($1, '{}', $2) RETURNING id", connection);
		command.Parameters.AddWithValue(runType);
		command.Parameters.AddWithValue(state);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private async Task<List<(string EventType, string Actor, string Detail)>> ReadAuditEventsAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"SELECT event_type, actor, detail::text FROM audit_log WHERE run_id = $1 ORDER BY occurred_at, id", connection);
		command.Parameters.AddWithValue(runId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
		List<(string, string, string)> events = [];
		while (await reader.ReadAsync())
		{
			events.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
		}

		return events;
	}

	private static string ExtractReason(string detailJson) =>
		System.Text.Json.JsonDocument.Parse(detailJson).RootElement.GetProperty("reason").GetString()!;
}
