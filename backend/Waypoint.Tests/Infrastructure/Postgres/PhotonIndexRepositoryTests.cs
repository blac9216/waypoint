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
using Waypoint.Core.Downloads.Photon;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads.Photon;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Migration 0108's <c>photon_repo_index</c> against a real, disposable Postgres
/// container (issue #1509) -- the acceptance criterion this covers ("re-discovery of
/// an unchanged upstream yields no duplicate rows") only means something proven
/// against the real <c>ON CONFLICT ... DO UPDATE</c> engine behavior.
/// </summary>
[Collection("Postgres")]
public sealed class PhotonIndexRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;

	public PhotonIndexRepositoryTests(PostgresFixture fixture)
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
	public async Task UpsertRepoIndexEntryAsync_ReDiscoveryOfUnchangedRepo_YieldsNoDuplicateRow()
	{
		PhotonIndexRepository repository = new(_fixture.ConnectionString);
		PhotonRepoIndexEntry entry = new(
			"5.0", PhotonRepoVariants.Updates, PhotonArches.X86_64,
			"https://photon.example.internal/photon/5.0/photon_updates_5.0_x86_64",
			HasRepodata: true, RepomdRevision: "1700000000", PackageCount: 2125);

		await repository.UpsertRepoIndexEntryAsync(entry, CancellationToken.None);
		await repository.UpsertRepoIndexEntryAsync(entry, CancellationToken.None);
		await repository.UpsertRepoIndexEntryAsync(entry, CancellationToken.None);

		IReadOnlyList<PhotonRepoIndexEntry> entries = await repository.ListRepoIndexEntriesAsync(CancellationToken.None);
		PhotonRepoIndexEntry stored = Assert.Single(entries);
		Assert.Equal("5.0", stored.Version);
		Assert.Equal(PhotonRepoVariants.Updates, stored.Variant);
		Assert.Equal(PhotonArches.X86_64, stored.Arch);
		Assert.Equal(2125, stored.PackageCount);
	}

	[Fact]
	public async Task UpsertRepoIndexEntryAsync_ReDiscoveryWithNewRevision_UpdatesInPlace_PreservingDiscoveredAt()
	{
		PhotonIndexRepository repository = new(_fixture.ConnectionString);
		PhotonRepoIndexEntry first = new(
			"5.0", PhotonRepoVariants.Release, PhotonArches.Aarch64,
			"https://photon.example.internal/photon/5.0/photon_release_5.0_aarch64",
			HasRepodata: true, RepomdRevision: "1700000000", PackageCount: 100);
		await repository.UpsertRepoIndexEntryAsync(first, CancellationToken.None);

		IReadOnlyList<PhotonRepoIndexEntry> afterFirst = await repository.ListRepoIndexEntriesAsync(CancellationToken.None);
		DateTimeOffset originalDiscoveredAt = Assert.Single(afterFirst).DiscoveredAt!.Value;

		PhotonRepoIndexEntry updated = first with { RepomdRevision = "1700000042", PackageCount = 101 };
		await repository.UpsertRepoIndexEntryAsync(updated, CancellationToken.None);

		IReadOnlyList<PhotonRepoIndexEntry> afterSecond = await repository.ListRepoIndexEntriesAsync(CancellationToken.None);
		PhotonRepoIndexEntry stored = Assert.Single(afterSecond);
		Assert.Equal("1700000042", stored.RepomdRevision);
		Assert.Equal(101, stored.PackageCount);
		Assert.Equal(originalDiscoveredAt, stored.DiscoveredAt);
	}

	[Fact]
	public async Task UpsertRepoIndexEntryAsync_NoRepodataRepo_StoresNullRevisionAndPackageCount()
	{
		PhotonIndexRepository repository = new(_fixture.ConnectionString);
		PhotonRepoIndexEntry entry = new(
			"5.0", PhotonRepoVariants.Snapshots, PhotonArches.X86_64,
			"https://photon.example.internal/photon/5.0/photon_snapshots_5.0_x86_64",
			HasRepodata: false, RepomdRevision: null, PackageCount: null);

		await repository.UpsertRepoIndexEntryAsync(entry, CancellationToken.None);

		PhotonRepoIndexEntry stored = Assert.Single(await repository.ListRepoIndexEntriesAsync(CancellationToken.None));
		Assert.False(stored.HasRepodata);
		Assert.Null(stored.RepomdRevision);
		Assert.Null(stored.PackageCount);
	}

	[Fact]
	public async Task UpsertRepoIndexEntryAsync_DifferentArchSameVersionAndVariant_IsASeparateRow()
	{
		PhotonIndexRepository repository = new(_fixture.ConnectionString);
		await repository.UpsertRepoIndexEntryAsync(
			new PhotonRepoIndexEntry("5.0", PhotonRepoVariants.Extras, PhotonArches.X86_64, "https://photon.example.internal/photon/5.0/photon_extras_5.0_x86_64", true, "r1", 14),
			CancellationToken.None);
		await repository.UpsertRepoIndexEntryAsync(
			new PhotonRepoIndexEntry("5.0", PhotonRepoVariants.Extras, PhotonArches.Aarch64, "https://photon.example.internal/photon/5.0/photon_extras_5.0_aarch64", true, "r1", 14),
			CancellationToken.None);

		IReadOnlyList<PhotonRepoIndexEntry> entries = await repository.ListRepoIndexEntriesAsync(CancellationToken.None);
		Assert.Equal(2, entries.Count);
		Assert.Contains(entries, e => e.Arch == PhotonArches.X86_64);
		Assert.Contains(entries, e => e.Arch == PhotonArches.Aarch64);
	}
}
