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
using Waypoint.Core.Capacity;
using Waypoint.Infrastructure.Capacity;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Capacity;

/// <summary>
/// Issue #1529 (epic #1180, split from #1042): the disk-admission reserve singleton
/// (migration 20260913060100) -- default value, round-trip update, and a grant-drift
/// guard proving <c>disk_admission_policy</c> is genuinely unreachable under BOTH real
/// least-privilege runner roles, the same class of proof
/// <c>Waypoint.Tests.Infrastructure.Postgres.RetentionPolicyTests</c> already
/// establishes for its sibling singleton.
/// </summary>
[Collection("Postgres")]
public sealed class DiskAdmissionPolicyRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private DiskAdmissionPolicyRepository _repository = null!;
	private string _complianceRunnerConnectionString = string.Empty;
	private string _downloadRunnerConnectionString = string.Empty;

	public DiskAdmissionPolicyRepositoryTests(PostgresFixture fixture) => _fixture = fixture;

	public async Task InitializeAsync()
	{
		await new NpgsqlSchemaMigrator(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance).ApplyAsync();

		// Singleton with no FK to runs, so ResetJobEngineDataAsync's TRUNCATE ...
		// CASCADE (scoped from `runs`) never touches it -- reset it back to the
		// seeded default explicitly so a prior test's SetAsync does not leak into
		// this one, same convention RetentionPolicyTests uses for its own singleton.
		await using (NpgsqlConnection reset = new(_fixture.ConnectionString))
		{
			await reset.OpenAsync();
			await using NpgsqlCommand resetCommand = new(
				"UPDATE disk_admission_policy SET reserve_bytes = 10737418240, updated_by = NULL WHERE id = 1", reset);
			await resetCommand.ExecuteNonQueryAsync();
		}

		_repository = new DiskAdmissionPolicyRepository(_fixture.ConnectionString);

		NpgsqlConnectionStringBuilder complianceBuilder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_compliance_runner",
			Password = "waypoint_test",
		};
		_complianceRunnerConnectionString = complianceBuilder.ConnectionString;

		NpgsqlConnectionStringBuilder downloadBuilder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = downloadBuilder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task GetAsync_SeededSingleton_DefaultsTo10GiBWithNullUpdatedBy()
	{
		DiskAdmissionPolicy? policy = await _repository.GetAsync(CancellationToken.None);

		Assert.NotNull(policy);
		Assert.Equal(10737418240L, policy!.ReserveBytes);
		Assert.Null(policy.UpdatedBy);
	}

	[Fact]
	public async Task SetAsync_NonNegativeReserve_UpdatesValueActorAndAdvancesTimestamp()
	{
		DiskAdmissionPolicy? before = await _repository.GetAsync(CancellationToken.None);

		DiskAdmissionPolicy updated = await _repository.SetAsync(5_368_709_120L, "admin-alice", CancellationToken.None);

		Assert.Equal(5_368_709_120L, updated.ReserveBytes);
		Assert.Equal("admin-alice", updated.UpdatedBy);
		Assert.True(updated.UpdatedAt >= before!.UpdatedAt);

		DiskAdmissionPolicy? reread = await _repository.GetAsync(CancellationToken.None);
		Assert.Equal(5_368_709_120L, reread!.ReserveBytes);
		Assert.Equal("admin-alice", reread.UpdatedBy);
		Assert.Equal(updated.UpdatedAt, reread.UpdatedAt);
	}

	[Fact]
	public async Task SetAsync_ZeroReserve_Accepted()
	{
		DiskAdmissionPolicy updated = await _repository.SetAsync(0, "admin-alice", CancellationToken.None);
		Assert.Equal(0, updated.ReserveBytes);
	}

	[Fact]
	public async Task SetAsync_NegativeReserve_ThrowsArgumentOutOfRangeException() =>
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => _repository.SetAsync(-1, "admin-alice", CancellationToken.None));

	/// <summary>
	/// Migration 20260913060100 deliberately withholds every grant on
	/// <c>disk_admission_policy</c> from both runner roles, the same posture migration
	/// 0078 documented for <c>retention_policy</c> -- reading and writing this setting
	/// is exclusively an API-side responsibility.
	/// </summary>
	[Fact]
	public Task ComplianceRunnerRole_CannotReadOrWriteDiskAdmissionPolicy() =>
		AssertRoleCannotReachDiskAdmissionPolicyAsync(_complianceRunnerConnectionString);

	/// <inheritdoc cref="ComplianceRunnerRole_CannotReadOrWriteDiskAdmissionPolicy"/>
	[Fact]
	public Task DownloadRunnerRole_CannotReadOrWriteDiskAdmissionPolicy() =>
		AssertRoleCannotReachDiskAdmissionPolicyAsync(_downloadRunnerConnectionString);

	private static async Task AssertRoleCannotReachDiskAdmissionPolicyAsync(string runnerConnectionString)
	{
		await using NpgsqlConnection connection = new(runnerConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand select = new("SELECT reserve_bytes FROM disk_admission_policy WHERE id = 1", connection))
		{
			PostgresException selectException = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, selectException.SqlState);
		}

		await using (NpgsqlCommand update = new("UPDATE disk_admission_policy SET reserve_bytes = 0 WHERE id = 1", connection))
		{
			PostgresException updateException = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, updateException.SqlState);
		}

		await using (NpgsqlCommand insert = new(
			"INSERT INTO disk_admission_policy (id, reserve_bytes) VALUES (2, 0)", connection))
		{
			PostgresException insertException = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
			Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertException.SqlState);
		}
	}
}
