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
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #39, migration 0037's runner grants, following the
/// <see cref="RunnerRoleGrantDriftTests"/> convention (grant drift has shipped real
/// 42501s twice -- see that class's doc comment): every operation the download-runner's
/// <c>ManagedToolInstallJobHandler</c> actually performs must run as the REAL
/// <c>waypoint_download_runner</c> role, not the full-privilege test owner, and the
/// least-privilege boundary (compliance-runner has no business in this table at all)
/// must stay denied so drift in either direction breaks a test.
/// </summary>
[Collection("Postgres")]
public sealed class ManagedToolInstallRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;
	private string _complianceRunnerConnectionString = string.Empty;

	public ManagedToolInstallRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();

		// Same fixed test-role password convention as WorkerRegistryRunnerRoleGrantTests:
		// PostgresFixture.CreateRunnerRolesAsync provisions both roles with "waypoint_test".
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

	/// <summary>
	/// The full read/write surface <c>ManagedToolInstallJobHandler</c> exercises, as the
	/// real download-runner role: RecordAsync (INSERT ... RETURNING, which also needs
	/// SELECT), ListAsync, and GetCurrentAsync must all succeed without 42501.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CanRecordAndReadInstallHistory()
	{
		ManagedToolInstallRepository repository = new(_downloadRunnerConnectionString);

		Guid id = await repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.Upload, $"staged-{Guid.NewGuid():N}", "9.9-test", "sha-test",
				ManagedToolInstallOutcomes.Installed, null, "grant-drift-tester", null),
			CancellationToken.None);

		IReadOnlyList<ManagedToolInstall> history = await repository.ListAsync(200, CancellationToken.None);
		Assert.Contains(history, item => item.Id == id);

		ManagedToolInstall? current = await repository.GetCurrentAsync(CancellationToken.None);
		Assert.NotNull(current);
	}

	/// <summary>
	/// Least-privilege boundary: managed-tool state is a download-domain concern
	/// (ADR-0013 §2) -- the compliance-runner role gets no grant at all on this table,
	/// and that must stay true (42501, insufficient_privilege).
	/// </summary>
	[Fact]
	public async Task ComplianceRunnerRole_IsDeniedEntirely()
	{
		await using NpgsqlConnection connection = new(_complianceRunnerConnectionString);
		await connection.OpenAsync();

		await using NpgsqlCommand select = new("SELECT count(*) FROM managed_tool_installs", connection);
		PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => select.ExecuteScalarAsync());
		Assert.Equal("42501", denied.SqlState);
	}
}
