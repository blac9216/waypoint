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
/// Issue #39, migration 0037: the append-only <c>managed_tool_installs</c> ledger
/// against real Postgres -- history ordering, the derived "current install" view, and
/// that rejected/failed attempts round-trip alongside installed ones (the acceptance
/// criterion is explicit that rejected attempts must be recorded, not dropped).
/// </summary>
[Collection("Postgres")]
public sealed class ManagedToolInstallRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ManagedToolInstallRepository _repository = null!;

	public ManagedToolInstallRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_repository = new ManagedToolInstallRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	private async Task ResetAsync()
	{
		// TRUNCATE, not DELETE: migration 0037's append-only trigger blocks row-level
		// DELETE for every role, and TRUNCATE does not fire row-level triggers -- the
		// owner role's legitimate escape hatch for resetting test state.
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("TRUNCATE managed_tool_installs", connection);
		await command.ExecuteNonQueryAsync();
	}

	[Fact]
	public async Task RecordAsync_ThenListAsync_ReturnsNewestFirst()
	{
		Guid first = await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.LocalRepository, "vcf-download-tool-1.0", "1.0", "sha-1",
				ManagedToolInstallOutcomes.Installed, null, "tester", null),
			CancellationToken.None);
		await Task.Delay(10); // ensure a distinct created_at ordering on fast hardware
		Guid second = await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.Upload, "staged-xyz", "1.1", "sha-2",
				ManagedToolInstallOutcomes.Installed, null, "tester", null),
			CancellationToken.None);

		IReadOnlyList<ManagedToolInstall> items = await _repository.ListAsync(10, CancellationToken.None);

		Assert.Equal(2, items.Count);
		Assert.Equal(second, items[0].Id);
		Assert.Equal(first, items[1].Id);
	}

	[Fact]
	public async Task RecordAsync_RejectedAttempt_RoundTripsTheRejectionReason()
	{
		Guid id = await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.LocalRepository, "vcf-download-tool-bad", null, "sha-bad",
				ManagedToolInstallOutcomes.Rejected, "Signature does not match the Broadcom release public key.", "tester", null),
			CancellationToken.None);

		ManagedToolInstall recorded = (await _repository.ListAsync(10, CancellationToken.None)).Single(item => item.Id == id);

		Assert.Equal(ManagedToolInstallOutcomes.Rejected, recorded.Outcome);
		Assert.Equal("Signature does not match the Broadcom release public key.", recorded.RejectedReason);
	}

	[Fact]
	public async Task GetCurrentAsync_NoInstalledRow_ReturnsNull()
	{
		await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.LocalRepository, "bad", null, null,
				ManagedToolInstallOutcomes.Rejected, "bad signature", "tester", null),
			CancellationToken.None);

		Assert.Null(await _repository.GetCurrentAsync(CancellationToken.None));
	}

	[Fact]
	public async Task GetCurrentAsync_ReturnsTheNewestInstalledRow_IgnoringLaterRejectedAttempts()
	{
		await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.LocalRepository, "good-1.0", "1.0", "sha-1",
				ManagedToolInstallOutcomes.Installed, null, "tester", null),
			CancellationToken.None);
		await Task.Delay(10);
		await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.Upload, "later-bad", null, null,
				ManagedToolInstallOutcomes.Rejected, "bad signature", "tester", null),
			CancellationToken.None);

		ManagedToolInstall? current = await _repository.GetCurrentAsync(CancellationToken.None);

		Assert.NotNull(current);
		Assert.Equal("good-1.0", current!.SourcePath);
		Assert.Equal(ManagedToolInstallOutcomes.Installed, current.Outcome);
	}

	[Fact]
	public async Task Table_IsAppendOnly_UpdateAndDeleteAreBothBlocked()
	{
		Guid id = await _repository.RecordAsync(
			new ManagedToolInstallAttempt(ManagedToolInstallSources.LocalRepository, "vcf-download-tool-1.0", "1.0", "sha-1",
				ManagedToolInstallOutcomes.Installed, null, "tester", null),
			CancellationToken.None);

		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand update = new("UPDATE managed_tool_installs SET outcome = 'rejected' WHERE id = $1", connection))
		{
			update.Parameters.AddWithValue(id);
			PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
			Assert.Contains("append-only", exception.MessageText, StringComparison.OrdinalIgnoreCase);
		}

		await using (NpgsqlCommand delete = new("DELETE FROM managed_tool_installs WHERE id = $1", connection))
		{
			delete.Parameters.AddWithValue(id);
			PostgresException exception = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
			Assert.Contains("append-only", exception.MessageText, StringComparison.OrdinalIgnoreCase);
		}
	}
}
