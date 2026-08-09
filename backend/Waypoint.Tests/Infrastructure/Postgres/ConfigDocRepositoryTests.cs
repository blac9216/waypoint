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

	/// <summary>
	/// Issue #270: slot-creation and @v1 must commit as one transaction, so a failure in
	/// the @v1 insert must roll back the config_docs row too -- never leaving an orphan
	/// (a doc row with current_version = 0 and no config_versions rows). A real process
	/// crash between the two steps is not reproducible from a test, so this forces the
	/// failure a different way: pre-seed a v1 row under a *different* doc id at the same
	/// slot is not possible (version numbers are per-doc, not global), so instead this
	/// pre-seeds an inconsistent row directly -- a config_docs row already sitting at the
	/// target slot with current_version left at 0 but a stray v1 already present. SaveAsync
	/// targets a *different*, brand-new id at that same (kind, profile, layer): the slot
	/// lookup finds the pre-seeded row (no INSERT into config_docs happens for the new id),
	/// so this exercises SaveVersionAsync's own transaction boundary rather than
	/// FindOrCreateDocAsync's -- see the companion API-level orphan/GET test and the
	/// concurrent-create test below for the rest of the #270 AC.
	/// </summary>
	[Fact]
	public async Task SaveVersionAsync_UniqueViolationOnInsert_RollsBackWithoutLeavingAStrayVersion()
	{
		Guid docId = Guid.NewGuid();
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync().ConfigureAwait(false);
			await using NpgsqlCommand insertDoc = new(
				"INSERT INTO config_docs (id, kind, profile, layer_type, layer_ref, current_version) VALUES ($1, $2, $3, 'global', NULL, 0)", connection);
			insertDoc.Parameters.AddWithValue(docId);
			insertDoc.Parameters.AddWithValue(ConfigDocKinds.Input);
			insertDoc.Parameters.AddWithValue("orphan-force-profile");
			await insertDoc.ExecuteNonQueryAsync().ConfigureAwait(false);

			await using NpgsqlCommand insertStrayVersion = new(
				"INSERT INTO config_versions (doc_id, version, author, body_yaml) VALUES ($1, 1, 'seed', 'seed: true')", connection);
			insertStrayVersion.Parameters.AddWithValue(docId);
			await insertStrayVersion.ExecuteNonQueryAsync().ConfigureAwait(false);
		}

		// current_version is still 0 (never advanced), so SaveVersionAsync computes
		// nextVersion = 1 and its INSERT collides with the stray v1 seeded above --
		// forcing a real Postgres unique-violation inside SaveVersionAsync's transaction.
		await Assert.ThrowsAsync<PostgresException>(
			() => _configDocs.SaveVersionAsync(docId, "alice", "x: 1", CancellationToken.None));

		// The doc row's current_version must be unchanged (still 0) -- the UPDATE that
		// would have repointed it never committed because the INSERT before it failed and
		// the whole transaction rolled back.
		ConfigDoc? doc = await _configDocs.GetAsync(docId, CancellationToken.None);
		Assert.Equal(0, doc!.CurrentVersion);

		// No second version row was left behind -- only the one seeded directly.
		IReadOnlyList<ConfigDocVersion> versions = await _configDocs.ListVersionsAsync(docId, CancellationToken.None);
		Assert.Single(versions);
		Assert.Equal("seed", versions[0].Author);
	}

	/// <summary>
	/// Issue #270 AC: "GET-by-id on a doc with no versions returns a clean response, not
	/// 500" is verified at the HTTP layer in ConfigDocsApiTests; this is the repository-level
	/// companion proving the specific orphan shape (current_version = 0, zero
	/// config_versions rows) degrades cleanly rather than throwing when read directly.
	/// </summary>
	[Fact]
	public async Task GetLatestVersionAsync_OrphanDocWithNoVersions_ReturnsNullNotThrow()
	{
		Guid docId = Guid.NewGuid();
		await using (NpgsqlConnection connection = new(_fixture.ConnectionString))
		{
			await connection.OpenAsync().ConfigureAwait(false);
			await using NpgsqlCommand insertOrphan = new(
				"INSERT INTO config_docs (id, kind, profile, layer_type, layer_ref, current_version) VALUES ($1, $2, $3, 'global', NULL, 0)", connection);
			insertOrphan.Parameters.AddWithValue(docId);
			insertOrphan.Parameters.AddWithValue(ConfigDocKinds.Attestation);
			insertOrphan.Parameters.AddWithValue("orphan-read-profile");
			await insertOrphan.ExecuteNonQueryAsync().ConfigureAwait(false);
		}

		ConfigDoc? doc = await _configDocs.GetAsync(docId, CancellationToken.None);
		Assert.NotNull(doc);

		ConfigDocVersion? latest = await _configDocs.GetLatestVersionAsync(docId, CancellationToken.None);
		Assert.Null(latest);
	}

	/// <summary>
	/// Issue #270 AC: concurrent first-saves at the same brand-new (kind, profile, layer)
	/// slot must still dedupe to exactly one config_docs row -- the FindOrCreateDocAsync
	/// unique-violation-and-reread race handling (now running inside SaveAsync's shared
	/// transaction via a SAVEPOINT, see ConfigDocRepository) must still work.
	/// </summary>
	[Fact]
	public async Task SaveAsync_ConcurrentFirstSavesAtSameNewSlot_DedupeToOneDocRow()
	{
		string profile = $"concurrent-slot-{Guid.NewGuid():N}";

		Task<(ConfigDocSaveOutcome, ConfigDoc?, ConfigDocVersion?)> first = _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, profile, ConfigDocLayers.Global, null, "alice", "a: 1", CancellationToken.None);
		Task<(ConfigDocSaveOutcome, ConfigDoc?, ConfigDocVersion?)> second = _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, profile, ConfigDocLayers.Global, null, "bob", "b: 1", CancellationToken.None);

		(ConfigDocSaveOutcome, ConfigDoc?, ConfigDocVersion?)[] results = await Task.WhenAll(first, second);

		Assert.All(results, r => Assert.Equal(ConfigDocSaveOutcome.Ok, r.Item1));
		Assert.Equal(results[0].Item2!.Id, results[1].Item2!.Id);

		(IReadOnlyList<ConfigDoc> items, long total) = await _configDocs.ListAsync(
			ConfigDocKinds.Input, profile, ConfigDocLayers.Global, null, new PageRequest(), CancellationToken.None);
		Assert.Equal(1, total);
		Assert.Single(items);

		// Exactly one of the two saves landed as v1 and the other as v2 -- both against the
		// single deduped doc row, never two v1 rows under two doc rows.
		IReadOnlyList<ConfigDocVersion> versions = await _configDocs.ListVersionsAsync(items[0].Id, CancellationToken.None);
		Assert.Equal(2, versions.Count);
		Assert.Contains(versions, v => v.Version == 1);
		Assert.Contains(versions, v => v.Version == 2);
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
