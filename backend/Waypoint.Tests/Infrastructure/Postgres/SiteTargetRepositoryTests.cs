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
using Waypoint.Core.Pagination;
using Waypoint.Core.Sites;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Sites;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #19 (epic #13) against real Postgres: <see cref="SiteRepository"/> and
/// <see cref="TargetRepository"/> storage-layer behavior below the HTTP surface --
/// name uniqueness, cascade-vs-blocked delete, the multiple-targets-per-kind rule, and
/// FK integrity against a missing site/credential. End-to-end HTTP round trips (the
/// two-vCenter+NSX+SRG scenario, role gates, the secret-in-connection 400) live in
/// <c>SitesTargetsApiTests</c>.
/// </summary>
[Collection("Postgres")]
public sealed class SiteTargetRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;

	public SiteTargetRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task CreateAsync_DuplicateName_ReturnsNull()
	{
		string name = $"dup-site-{Guid.NewGuid():N}";
		Guid? first = await _sites.CreateAsync(name, null, null, CancellationToken.None);
		Guid? second = await _sites.CreateAsync(name, null, null, CancellationToken.None);

		Assert.NotNull(first);
		Assert.Null(second);
	}

	[Fact]
	public async Task Targets_AllowMultiplePerKindUnderOneSite()
	{
		Guid siteId = (Guid)(await _sites.CreateAsync($"multi-kind-{Guid.NewGuid():N}", null, null, CancellationToken.None))!;

		(TargetWriteOutcome outcomeA, Guid? idA) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, "vcsa-01", """{"host":"vcsa-01.example.internal"}""", null, CancellationToken.None);
		(TargetWriteOutcome outcomeB, Guid? idB) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, "vcsa-02", """{"host":"vcsa-02.example.internal"}""", null, CancellationToken.None);

		Assert.Equal(TargetWriteOutcome.Ok, outcomeA);
		Assert.Equal(TargetWriteOutcome.Ok, outcomeB);
		Assert.NotEqual(idA, idB);

		(IReadOnlyList<Target> items, long total) = await _targets.ListAsync(siteId, new PageRequest(), CancellationToken.None);
		Assert.Equal(2, items.Count);
		Assert.Equal(2, total);
		Assert.All(items, target => Assert.Equal(TargetKinds.VSphere, target.Kind));
	}

	[Fact]
	public async Task CreateAsync_SameNameTwiceUnderOneSite_IsRejected()
	{
		Guid siteId = (Guid)(await _sites.CreateAsync($"dup-target-name-{Guid.NewGuid():N}", null, null, CancellationToken.None))!;

		(TargetWriteOutcome first, _) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, "same-name", "{}", null, CancellationToken.None);
		(TargetWriteOutcome second, _) = await _targets.CreateAsync(
			siteId, TargetKinds.NsxApi, "same-name", "{}", null, CancellationToken.None);

		Assert.Equal(TargetWriteOutcome.Ok, first);
		Assert.Equal(TargetWriteOutcome.NameTaken, second);
	}

	[Fact]
	public async Task CreateAsync_UnknownSite_ReturnsSiteNotFound()
	{
		(TargetWriteOutcome outcome, Guid? id) = await _targets.CreateAsync(
			Guid.NewGuid(), TargetKinds.Ssh, "orphan", "{}", null, CancellationToken.None);

		Assert.Equal(TargetWriteOutcome.SiteNotFound, outcome);
		Assert.Null(id);
	}

	[Fact]
	public async Task CreateAsync_UnknownCredential_ReturnsCredentialNotFound()
	{
		Guid siteId = (Guid)(await _sites.CreateAsync($"bad-cred-site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!;

		(TargetWriteOutcome outcome, Guid? id) = await _targets.CreateAsync(
			siteId, TargetKinds.Ssh, "bad-cred-target", "{}", Guid.NewGuid(), CancellationToken.None);

		Assert.Equal(TargetWriteOutcome.CredentialNotFound, outcome);
		Assert.Null(id);
	}

	[Fact]
	public async Task DeleteAsync_SiteWithTargets_IsBlockedUnlessForced()
	{
		Guid siteId = (Guid)(await _sites.CreateAsync($"blocked-delete-{Guid.NewGuid():N}", null, null, CancellationToken.None))!;
		await _targets.CreateAsync(siteId, TargetKinds.Ssh, "blocker", "{}", null, CancellationToken.None);

		SiteDeleteOutcome blocked = await _sites.DeleteAsync(siteId, force: false, CancellationToken.None);
		Assert.Equal(SiteDeleteOutcome.InUse, blocked);

		SiteDeleteOutcome forced = await _sites.DeleteAsync(siteId, force: true, CancellationToken.None);
		Assert.Equal(SiteDeleteOutcome.Deleted, forced);

		(IReadOnlyList<Target> remaining, _) = await _targets.ListAsync(siteId, new PageRequest(), CancellationToken.None);
		Assert.Empty(remaining);
	}

	[Fact]
	public async Task UpdateAsync_ClearCredential_NullsTheReference()
	{
		Guid credentialId = await SeedCredentialAsync();
		Guid siteId = (Guid)(await _sites.CreateAsync($"clear-cred-site-{Guid.NewGuid():N}", null, null, CancellationToken.None))!;
		(TargetWriteOutcome created, Guid? targetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, "cred-target", "{}", credentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, created);

		TargetWriteOutcome updated = await _targets.UpdateAsync(
			targetId!.Value, null, null, null, null, clearCredential: true, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, updated);

		Target? target = await _targets.GetAsync(targetId.Value, CancellationToken.None);
		Assert.Null(target!.CredentialId);
	}

	private async Task<Guid> SeedCredentialAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand insert = new(
			"INSERT INTO credentials (name, credential_type, owner) VALUES ($1, 'service', 'shared') RETURNING id", connection);
		insert.Parameters.AddWithValue($"repo-test-cred-{Guid.NewGuid():N}");
		return (Guid)(await insert.ExecuteScalarAsync())!;
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE targets, sites, downloads, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
