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

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Migration 0100 (issue #1488) against real Postgres: <c>unknown_catalog_files</c>
/// is insert-or-touch-last-seen only (design decision Q11 -- alert instead of drop).
/// <see cref="Repository_HasNoDeleteOrRemoveMethod"/> is the acceptance criterion's
/// own enforcement ("no delete path ... enforced by a repository test") -- it fails
/// the build the moment anyone adds one, rather than relying on code review to catch
/// a future regression. Fixtures are entirely invented (CLAUDE.md sanitization
/// rules) -- no real depot data, hostnames, or tokens.
/// </summary>
[Collection("Postgres")]
public sealed class UnknownCatalogFileRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private UnknownCatalogFileRepository _repository = null!;

	public UnknownCatalogFileRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetUnknownFilesAsync();
		_repository = new UnknownCatalogFileRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public void Repository_HasNoDeleteOrRemoveMethod()
	{
		MethodInfo[] methods = typeof(IUnknownCatalogFileRepository).GetMethods();

		Assert.DoesNotContain(methods, method =>
			method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
			|| method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
			|| method.Name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task RecordSeenAsync_SamePathTwice_TouchesLastSeenAtAndKeepsOneRow()
	{
		string relativePath = $"unknown/{Guid.NewGuid():N}.iso";

		Guid firstId = await _repository.RecordSeenAsync(relativePath, 1024, CancellationToken.None);
		IReadOnlyList<UnknownCatalogFile> afterFirst = await _repository.ListAsync(CancellationToken.None);
		UnknownCatalogFile firstSeen = Assert.Single(afterFirst, item => item.RelativePath == relativePath);

		// A real clock tick between inserts so last_seen_at can provably advance --
		// Postgres timestamptz resolution is far finer than this, but the assertion
		// below only needs last_seen_at >= first_seen_at, not strict inequality.
		await Task.Delay(TimeSpan.FromMilliseconds(5));

		Guid secondId = await _repository.RecordSeenAsync(relativePath, 2048, CancellationToken.None);
		Assert.Equal(firstId, secondId);

		IReadOnlyList<UnknownCatalogFile> afterSecond = await _repository.ListAsync(CancellationToken.None);
		UnknownCatalogFile[] matching = [.. afterSecond.Where(item => item.RelativePath == relativePath)];

		Assert.Single(matching);
		Assert.Equal(2048, matching[0].SizeBytes);
		Assert.Equal(firstSeen.FirstSeenAt, matching[0].FirstSeenAt);
		Assert.True(matching[0].LastSeenAt >= firstSeen.LastSeenAt);
	}

	[Fact]
	public async Task RecordSeenAsync_TwoDifferentPaths_YieldsTwoRows()
	{
		string tag = Guid.NewGuid().ToString("N");
		await _repository.RecordSeenAsync($"unknown/{tag}-a.iso", 111, CancellationToken.None);
		await _repository.RecordSeenAsync($"unknown/{tag}-b.iso", 222, CancellationToken.None);

		IReadOnlyList<UnknownCatalogFile> items = await _repository.ListAsync(CancellationToken.None);
		Assert.Equal(2, items.Count(item => item.RelativePath.Contains(tag, StringComparison.Ordinal)));
	}

	private async Task ResetUnknownFilesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE unknown_catalog_files RESTART IDENTITY", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}

/// <summary>
/// Issue #1593 review, finding 4, following the issue-#556 runner-role-grant-drift
/// convention (<see cref="RunnerRoleGrantDriftTests"/>, <see cref="WorkerRegistryRunnerRoleGrantTests"/>,
/// <see cref="ManagedToolInstallRunnerRoleGrantTests"/>): migration 0100 grants
/// <c>waypoint_download_runner</c> SELECT/INSERT/UPDATE (never DELETE) on
/// <c>unknown_catalog_files</c>, but until this class every test against the table ran
/// through <see cref="PostgresFixture.ConnectionString"/> -- the full-privilege test
/// owner -- so a real grant gap would never have shown up in the suite, the exact
/// class of defect that shipped live 42501s twice before (see
/// <see cref="RunnerRoleGrantDriftTests"/>'s doc comment). This connects as the real
/// runner role and proves both halves of the migration's contract: the granted
/// operations actually work, and the withheld DELETE is enforced at the DB layer, not
/// merely by <see cref="UnknownCatalogFileRepositoryTests.Repository_HasNoDeleteOrRemoveMethod"/>'s
/// interface-level convention.
/// </summary>
[Collection("Postgres")]
public sealed class UnknownCatalogFileRunnerRoleGrantTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private string _downloadRunnerConnectionString = string.Empty;

	public UnknownCatalogFileRunnerRoleGrantTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetUnknownFilesAsync();

		// Same fixed test-role password convention as WorkerRegistryRunnerRoleGrantTests:
		// PostgresFixture.CreateRunnerRolesAsync provisions the role with "waypoint_test".
		NpgsqlConnectionStringBuilder builder = new(_fixture.ConnectionString)
		{
			Username = "waypoint_download_runner",
			Password = "waypoint_test",
		};
		_downloadRunnerConnectionString = builder.ConnectionString;
	}

	public Task DisposeAsync() => Task.CompletedTask;

	/// <summary>
	/// The full surface <see cref="UnknownCatalogFileRepository.RecordSeenAsync"/>
	/// exercises (INSERT ... ON CONFLICT DO UPDATE ... RETURNING, which needs
	/// SELECT+INSERT+UPDATE) as the real download-runner role: insert-then-touch must
	/// both succeed without 42501, mirroring
	/// <see cref="UnknownCatalogFileRepositoryTests.RecordSeenAsync_SamePathTwice_TouchesLastSeenAtAndKeepsOneRow"/>
	/// but through the actual granted role instead of the fixture owner.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_RecordSeenAsync_InsertAndTouchSucceedWithoutPermissionDenied()
	{
		UnknownCatalogFileRepository runnerRepository = new(_downloadRunnerConnectionString);
		string relativePath = $"unknown/{Guid.NewGuid():N}.iso";

		Guid firstId = await runnerRepository.RecordSeenAsync(relativePath, 1024, CancellationToken.None);
		Guid secondId = await runnerRepository.RecordSeenAsync(relativePath, 2048, CancellationToken.None);

		Assert.Equal(firstId, secondId);

		IReadOnlyList<UnknownCatalogFile> items = await runnerRepository.ListAsync(CancellationToken.None);
		UnknownCatalogFile matching = Assert.Single(items.Where(item => item.RelativePath == relativePath));
		Assert.Equal(2048, matching.SizeBytes);
	}

	/// <summary>
	/// Least-privilege boundary check, and the migration's real enforcement mechanism
	/// (not just <see cref="UnknownCatalogFileRepositoryTests.Repository_HasNoDeleteOrRemoveMethod"/>'s
	/// interface-only guard): migration 0100 deliberately withholds DELETE from
	/// <c>waypoint_download_runner</c> on <c>unknown_catalog_files</c>, so an ad hoc
	/// DELETE as that role must fail 42501 even though the role has SELECT/INSERT/
	/// UPDATE on the same table.
	/// </summary>
	[Fact]
	public async Task DownloadRunnerRole_CannotDeleteUnknownCatalogFileRows()
	{
		UnknownCatalogFileRepository ownerRepository = new(_fixture.ConnectionString);
		string relativePath = $"unknown/{Guid.NewGuid():N}.iso";
		await ownerRepository.RecordSeenAsync(relativePath, 512, CancellationToken.None);

		await using NpgsqlConnection connection = new(_downloadRunnerConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand delete = new("DELETE FROM unknown_catalog_files WHERE relative_path = $1", connection);
		delete.Parameters.AddWithValue(relativePath);

		PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
		Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
	}

	private async Task ResetUnknownFilesAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE unknown_catalog_files RESTART IDENTITY", connection);
		await truncate.ExecuteNonQueryAsync();
	}
}
