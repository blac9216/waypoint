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
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Pagination;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #265 against real Postgres: <see cref="ConfigDocRepository"/> storage-layer
/// behavior -- append-only versioning (a save never mutates an existing version row),
/// the three-layer identity uniqueness (including the global-layer partial-index edge
/// case), and version-history ordering. HTTP round trips live in
/// <c>ConfigDocsApiTests</c>.
/// </summary>
[Collection("Postgres")]
public sealed class ConfigDocRepositoryTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private ConfigDocRepository _configDocs = null!;

	public ConfigDocRepositoryTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetDataAsync();
		_configDocs = new ConfigDocRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	[Fact]
	public async Task SaveAsync_TwiceAtSameSlot_CreatesV1ThenV2_NeverMutatingV1()
	{
		(ConfigDocSaveOutcome outcome1, ConfigDoc? doc1, ConfigDocVersion? v1) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Attestation, "profile-a", ConfigDocLayers.Global, null, "alice", "waived: true", CancellationToken.None);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome1);
		Assert.Equal(1, v1!.Version);

		(ConfigDocSaveOutcome outcome2, ConfigDoc? doc2, ConfigDocVersion? v2) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Attestation, "profile-a", ConfigDocLayers.Global, null, "bob", "waived: false", CancellationToken.None);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome2);
		Assert.Equal(2, v2!.Version);
		Assert.Equal(doc1!.Id, doc2!.Id);

		// v1's body and author must be unchanged -- the second save must not have
		// mutated the first version row, only appended a new one.
		ConfigDocVersion? reread1 = await _configDocs.GetVersionAsync(doc1.Id, 1, CancellationToken.None);
		Assert.Equal("alice", reread1!.Author);
		Assert.Equal("waived: true", reread1.BodyYaml);

		IReadOnlyList<ConfigDocVersion> history = await _configDocs.ListVersionsAsync(doc1.Id, CancellationToken.None);
		Assert.Equal(2, history.Count);
		Assert.Equal(2, history[0].Version); // newest first
		Assert.Equal(1, history[1].Version);

		ConfigDocVersion? latest = await _configDocs.GetLatestVersionAsync(doc1.Id, CancellationToken.None);
		Assert.Equal("bob", latest!.Author);

		ConfigDoc? current = await _configDocs.GetAsync(doc1.Id, CancellationToken.None);
		Assert.Equal(2, current!.CurrentVersion);
	}

	[Fact]
	public async Task SaveAsync_DifferentLayersOfSameKindAndProfile_AreDistinctSlots()
	{
		(_, ConfigDoc? globalDoc, _) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, "profile-b", ConfigDocLayers.Global, null, "alice", "syslog: 192.0.2.1", CancellationToken.None);

		Guid siteId = Guid.NewGuid();
		(ConfigDocSaveOutcome siteOutcome, ConfigDoc? siteDoc, _) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, "profile-b", ConfigDocLayers.Site, siteId, "alice", "syslog: 198.51.100.1", CancellationToken.None);

		Assert.Equal(ConfigDocSaveOutcome.Ok, siteOutcome);
		Assert.NotEqual(globalDoc!.Id, siteDoc!.Id);
	}

	[Fact]
	public async Task SaveAsync_SecondGlobalSlotForSameKindAndProfile_ReusesTheSameDocRow()
	{
		// Regression guard for the global layer's partial-unique-index edge case
		// (layer_ref is NULL for every global row, so plain UNIQUE alone would not
		// catch a duplicate) -- two saves at the same (kind, profile, global) slot must
		// land on the same doc id, not create a second row.
		(_, ConfigDoc? first, _) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.RemediationInput, "profile-c", ConfigDocLayers.Global, null, "alice", "allow_reboot: false", CancellationToken.None);
		(_, ConfigDoc? second, _) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.RemediationInput, "profile-c", ConfigDocLayers.Global, null, "bob", "allow_reboot: true", CancellationToken.None);

		Assert.Equal(first!.Id, second!.Id);

		(IReadOnlyList<ConfigDoc> items, long total) = await _configDocs.ListAsync(
			ConfigDocKinds.RemediationInput, "profile-c", ConfigDocLayers.Global, null, new PageRequest(), CancellationToken.None);
		Assert.Single(items);
		Assert.Equal(1, total);
	}

	[Fact]
	public async Task SaveAsync_InvalidKind_ReturnsInvalidKindWithoutWriting()
	{
		(ConfigDocSaveOutcome outcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			Guid.NewGuid(), "not-a-real-kind", "profile-d", ConfigDocLayers.Global, null, "alice", "x: 1", CancellationToken.None);

		Assert.Equal(ConfigDocSaveOutcome.InvalidKind, outcome);
		Assert.Null(doc);
		Assert.Null(version);
	}

	[Fact]
	public async Task ListAsync_FiltersByKindProfileAndLayer()
	{
		Guid targetId = Guid.NewGuid();
		await _configDocs.SaveAsync(Guid.NewGuid(), ConfigDocKinds.Input, "filter-profile", ConfigDocLayers.Global, null, "alice", "a: 1", CancellationToken.None);
		await _configDocs.SaveAsync(Guid.NewGuid(), ConfigDocKinds.Attestation, "filter-profile", ConfigDocLayers.Global, null, "alice", "b: 1", CancellationToken.None);
		await _configDocs.SaveAsync(Guid.NewGuid(), ConfigDocKinds.Input, "filter-profile", ConfigDocLayers.Target, targetId, "alice", "c: 1", CancellationToken.None);

		(IReadOnlyList<ConfigDoc> byKind, _) = await _configDocs.ListAsync(
			ConfigDocKinds.Input, null, null, null, new PageRequest(), CancellationToken.None);
		Assert.All(byKind, doc => Assert.Equal(ConfigDocKinds.Input, doc.Kind));
		Assert.Contains(byKind, doc => doc.Profile == "filter-profile");

		(IReadOnlyList<ConfigDoc> byLayer, long layerTotal) = await _configDocs.ListAsync(
			null, "filter-profile", ConfigDocLayers.Target, targetId, new PageRequest(), CancellationToken.None);
		Assert.Equal(1, layerTotal);
		Assert.Equal(ConfigDocLayers.Target, byLayer.Single().LayerType);
	}

	[Fact]
	public async Task SaveVersionAsync_UnknownDocId_ReturnsNull()
	{
		ConfigDocVersion? result = await _configDocs.SaveVersionAsync(Guid.NewGuid(), "alice", "x: 1", CancellationToken.None);
		Assert.Null(result);
	}

	[Fact]
	public async Task GetVersionAsync_ByteStableRoundTrip_ForValidYaml()
	{
		const string yaml = "controls:\n  - id: V-1\n    status: not_applicable\n    justification: \"Not present in this build.\"\n";
		(_, ConfigDoc? doc, _) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Attestation, "roundtrip-profile", ConfigDocLayers.Global, null, "alice", yaml, CancellationToken.None);

		ConfigDocVersion? version = await _configDocs.GetVersionAsync(doc!.Id, 1, CancellationToken.None);
		Assert.Equal(yaml, version!.BodyYaml);
	}

	private async Task ResetDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new(
			"TRUNCATE TABLE config_versions, config_docs, targets, sites, downloads, credential_secrets, credentials RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
