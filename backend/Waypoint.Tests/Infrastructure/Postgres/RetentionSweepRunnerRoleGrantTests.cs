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
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #1436, migration 0127's runner grants, following the
/// <see cref="ManagedToolInstallRunnerRoleGrantTests"/>/<see cref="RunnerRoleGrantDriftTests"/>
/// convention (this repo's #556 convention: grant drift has shipped real 42501s
/// before -- prove both the grant that exists AND the operation that must still be
/// denied). <c>waypoint_download_runner</c> gets exactly <c>SELECT, UPDATE</c> on
/// <c>download_retained_content_state</c> and exactly <c>SELECT</c> on
/// <c>download_retention_policies</c> (0127's own header comment: the sweep
/// transitions existing rows and reads policy, it never inserts or deletes either
/// table) -- both halves proven here, plus the pre-existing least-privilege boundary
/// that <c>waypoint_compliance_runner</c> gets nothing on either table at all.
/// </summary>
[Collection("Postgres")]
public sealed class RetentionSweepRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;
	private string _ownerConnectionString = string.Empty;

	public RetentionSweepRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		_ownerConnectionString = _fixture.ConnectionString;

		// Same fixed test-role password convention as WorkerRegistryRunnerRoleGrantTests/
		// ManagedToolInstallRunnerRoleGrantTests: PostgresFixture.CreateRunnerRolesAsync
		// provisions both roles with "waypoint_test".
		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = builder.ConnectionString;

		builder.Username = "waypoint_compliance_runner";
		_complianceRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task<Guid> InsertDepotArtifactAndTrackedStateAsync(string relativePath)
	{
		await using NpgsqlConnection connection = new(_ownerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insertArtifact = new("INSERT INTO depot_artifacts (relative_path) VALUES ($1) RETURNING id", connection);
		insertArtifact.Parameters.AddWithValue($"{relativePath}-{Guid.NewGuid():N}");
		Guid artifactId = (Guid)(await insertArtifact.ExecuteScalarAsync())!;

		RetainedContentStateRepository states = new(_ownerConnectionString);
		Guid stateId = await states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		return stateId;
	}

	/// <summary>
	/// The full surface <c>RetentionSweepService</c> exercises against
	/// <c>download_retained_content_state</c> as the real download-runner role:
	/// <c>SELECT</c> (read the current row, including the <c>FOR UPDATE</c> lock read)
	/// and <c>UPDATE</c> (transition its state) must both succeed without 42501.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CanSelectAndUpdateRetainedContentState()
	{
		Guid stateId = await InsertDepotArtifactAndTrackedStateAsync("runner-grant-select-update");
		RetainedContentStateRepository repository = new(_downloadRunnerConnectionString);

		RetainedContentState? state = await repository.GetAsync(stateId, CancellationToken.None);
		Assert.NotNull(state);

		await repository.TransitionAsync(stateId, RetainedContentStates.Grace, CancellationToken.None);

		RetainedContentState? afterTransition = await repository.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Grace, afterTransition!.State);
	}

	/// <summary>
	/// The negative half of the #556 convention: 0127 deliberately does NOT grant
	/// INSERT on this table (the sweep never creates a row -- see
	/// <c>RetentionSweepService</c>'s doc comment), so a direct INSERT as the runner
	/// role must still fail with 42501.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotInsertIntoRetainedContentState()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand insert = new(
			"INSERT INTO download_retained_content_state (depot_artifact_id, state) VALUES (gen_random_uuid(), 'tracked')", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
		Assert.Equal("42501", denied.SqlState);
	}

	/// <summary>
	/// <c>waypoint_download_runner</c> gets exactly <c>SELECT</c> on
	/// <c>download_retention_policies</c> -- the sweep resolves a policy's
	/// <c>grace_period_days</c> to decide whether a grace-state row is due; it never
	/// writes a policy.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CanSelectRetentionPolicies()
	{
		RetentionPolicyRepository repository = new(_downloadRunnerConnectionString);

		RetentionPolicy? policy = await repository.GetByScopeKeyAsync(RetentionPolicyScopes.Default, CancellationToken.None);

		Assert.NotNull(policy);
	}

	/// <summary>The negative half for the policies table: no UPDATE grant.</summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotUpdateRetentionPolicies()
	{
		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand update = new(
			"UPDATE download_retention_policies SET grace_period_days = 99 WHERE scope_key = 'default'", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
		Assert.Equal("42501", denied.SqlState);
	}

	/// <summary>
	/// Least-privilege boundary: the download-retention domain is a download-domain
	/// concern (ADR-0013 §2) -- the compliance-runner role gets no grant at all on
	/// either table, and that must stay true (42501, insufficient_privilege).
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirelyOnBothTables()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand selectState = new("SELECT count(*) FROM download_retained_content_state", connection);
		PostgresException deniedState = await Assert.ThrowsAsync<PostgresException>(() => selectState.ExecuteScalarAsync());
		Assert.Equal("42501", deniedState.SqlState);

		await using NpgsqlCommand selectPolicies = new("SELECT count(*) FROM download_retention_policies", connection);
		PostgresException deniedPolicies = await Assert.ThrowsAsync<PostgresException>(() => selectPolicies.ExecuteScalarAsync());
		Assert.Equal("42501", deniedPolicies.SqlState);
	}
}
