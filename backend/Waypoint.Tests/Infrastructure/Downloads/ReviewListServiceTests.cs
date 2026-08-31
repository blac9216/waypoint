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
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// <see cref="ReviewListService"/> (issue #1440, epic #1182) against real Postgres:
/// orphan + out-of-scope enumeration, the insert-or-touch-last-seen + alert-once
/// contract for out-of-scope reports, and the structural "nothing here ever
/// deletes" guarantee this issue's Risk note calls for. The last [Fact] runs
/// <see cref="ReviewListService"/> and <see cref="RetentionSweepService"/> together
/// (issue #1440 AC: "covered by an integration test running both services
/// together") to prove the sweep's auto-prune pass never touches what the review
/// list surfaces.
/// </summary>
[Collection("Postgres")]
public sealed class ReviewListServiceTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _depotRoot;
	private UnknownCatalogFileRepository _unknownCatalogFiles = null!;
	private DepotArtifactRepository _artifacts = null!;
	private ReviewListService _reviewList = null!;
	private RecordingEventPublisher _events = null!;

	public ReviewListServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
		_depotRoot = Directory.CreateTempSubdirectory("waypoint-review-list-test-").FullName;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();

		_events = new RecordingEventPublisher();
		_unknownCatalogFiles = new UnknownCatalogFileRepository(_fixture.ConnectionString, _events);
		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_reviewList = new ReviewListService(_fixture.ConnectionString, _unknownCatalogFiles, _artifacts, _events);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		try
		{
			Directory.Delete(_depotRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup; a stray temp dir does not fail the test run.
		}
	}

	private async Task ResetAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using (NpgsqlCommand delete = new("DELETE FROM download_out_of_scope_content", connection))
		{
			await delete.ExecuteNonQueryAsync();
		}
		await using (NpgsqlCommand delete = new("DELETE FROM download_retained_content_state", connection))
		{
			await delete.ExecuteNonQueryAsync();
		}
		await using (NpgsqlCommand delete = new("DELETE FROM unknown_catalog_files", connection))
		{
			await delete.ExecuteNonQueryAsync();
		}
		await using (NpgsqlCommand delete = new("DELETE FROM depot_artifacts", connection))
		{
			await delete.ExecuteNonQueryAsync();
		}
	}

	private async Task<Guid> InsertDepotArtifactAsync(string relativePath)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("INSERT INTO depot_artifacts (relative_path) VALUES ($1) RETURNING id", connection);
		command.Parameters.AddWithValue(relativePath);
		return (Guid)(await command.ExecuteScalarAsync())!;
	}

	private sealed class RecordingEventPublisher : IJobEventPublisher
	{
		public List<string> EmittedPayloads { get; } = [];

		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{
			if (eventType == JobEventTypes.SystemNotice)
			{
				EmittedPayloads.Add(payloadJson);
			}
			return Task.CompletedTask;
		}
	}

	[Fact]
	public async Task ListAsync_OrphanRecorded_AppearsOnReviewListWithNoDepotArtifactId()
	{
		await _unknownCatalogFiles.RecordSeenAsync("uploads/mystery-9f2c.iso", 4096, CancellationToken.None);

		IReadOnlyList<ReviewListEntry> entries = await _reviewList.ListAsync(CancellationToken.None);

		ReviewListEntry entry = Assert.Single(entries);
		Assert.Equal(ReviewListEntryKind.Orphan, entry.Kind);
		Assert.Null(entry.DepotArtifactId);
		Assert.Equal("uploads/mystery-9f2c.iso", entry.RelativePath);
		Assert.Equal(4096, entry.SizeBytes);
	}

	[Fact]
	public async Task ReportOutOfScopeAsync_ThenListAsync_AppearsWithDepotArtifactIdAndReason()
	{
		Guid artifactId = await InsertDepotArtifactAsync("photon/photon-5.0-retired.iso");

		await _reviewList.ReportOutOfScopeAsync(artifactId, "no subscription references this product/version", CancellationToken.None);

		IReadOnlyList<ReviewListEntry> entries = await _reviewList.ListAsync(CancellationToken.None);

		ReviewListEntry entry = Assert.Single(entries);
		Assert.Equal(ReviewListEntryKind.OutOfScope, entry.Kind);
		Assert.Equal(artifactId, entry.DepotArtifactId);
		Assert.Equal("photon/photon-5.0-retired.iso", entry.RelativePath);
		Assert.Equal("no subscription references this product/version", entry.Reason);
	}

	[Fact]
	public async Task ListAsync_BothOrphansAndOutOfScope_AllAppearOnTheUnionList()
	{
		Guid artifactId = await InsertDepotArtifactAsync("photon/photon-5.0-retired.iso");
		await _reviewList.ReportOutOfScopeAsync(artifactId, "retired lane", CancellationToken.None);
		await _unknownCatalogFiles.RecordSeenAsync("uploads/mystery.iso", null, CancellationToken.None);

		IReadOnlyList<ReviewListEntry> entries = await _reviewList.ListAsync(CancellationToken.None);

		Assert.Equal(2, entries.Count);
		Assert.Contains(entries, e => e.Kind == ReviewListEntryKind.Orphan);
		Assert.Contains(entries, e => e.Kind == ReviewListEntryKind.OutOfScope);
	}

	[Fact]
	public async Task ReportOutOfScopeAsync_NewEntry_RaisesAlert()
	{
		Guid artifactId = await InsertDepotArtifactAsync("photon/photon-5.0-retired.iso");

		await _reviewList.ReportOutOfScopeAsync(artifactId, "retired lane", CancellationToken.None);

		string payload = Assert.Single(_events.EmittedPayloads);
		Assert.Contains("download.retention.out_of_scope_reported", payload);
		Assert.Contains(artifactId.ToString(), payload);
	}

	[Fact]
	public async Task ReportOutOfScopeAsync_RepeatReportOfSameArtifact_TouchesButDoesNotReAlert()
	{
		Guid artifactId = await InsertDepotArtifactAsync("photon/photon-5.0-retired.iso");

		await _reviewList.ReportOutOfScopeAsync(artifactId, "retired lane", CancellationToken.None);
		await _reviewList.ReportOutOfScopeAsync(artifactId, "retired lane, re-confirmed", CancellationToken.None);

		Assert.Single(_events.EmittedPayloads); // still only the first report's alert

		IReadOnlyList<ReviewListEntry> entries = await _reviewList.ListAsync(CancellationToken.None);
		ReviewListEntry entry = Assert.Single(entries);
		Assert.Equal("retired lane, re-confirmed", entry.Reason); // reason refreshed, no duplicate row
	}

	/// <summary>
	/// Issue #1440's Risk note: "consider a structural test... rather than only
	/// behavioral tests" that no code path in the review-list mechanism performs a
	/// deletion. Same interface-level convention
	/// <see cref="Waypoint.Tests.Infrastructure.Postgres.UnknownCatalogFileRepositoryTests.Repository_HasNoDeleteOrRemoveMethod"/>
	/// already establishes for the orphan half of this domain: neither
	/// <see cref="IReviewListService"/> itself, nor either repository interface it
	/// depends on (<see cref="IUnknownCatalogFileRepository"/>,
	/// <see cref="IDepotArtifactRepository"/>), exposes a method whose name suggests
	/// removal -- fails the build the moment anyone adds one, rather than relying on
	/// code review to catch a future regression.
	/// </summary>
	[Theory]
	[InlineData(typeof(IReviewListService))]
	[InlineData(typeof(IUnknownCatalogFileRepository))]
	[InlineData(typeof(IDepotArtifactRepository))]
	public void Interface_HasNoDeleteOrRemoveOrPurgeMethod(Type interfaceType)
	{
		MethodInfo[] methods = interfaceType.GetMethods();

		Assert.DoesNotContain(methods, method =>
			method.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase)
			|| method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase)
			|| method.Name.Contains("Purge", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Issue #1440 AC: "Orphans and out-of-scope content appear on the review list
	/// and are never removed by the sweep job -- covered by an integration test
	/// running both services together." Runs a real
	/// <see cref="RetentionSweepService"/> pass alongside <see cref="ReviewListService"/>
	/// against the same database: an orphan has no <c>depot_artifacts</c> row at
	/// all, so it structurally cannot be named as a sweep candidate; an out-of-scope
	/// artifact that was never given a <c>download_retained_content_state</c> row
	/// (this slice never calls <see cref="IRetainedContentStateRepository.EnsureTrackedAsync"/>
	/// for out-of-scope reports -- see <see cref="IReviewListService"/>'s own doc
	/// comment) is skipped by the sweep as an untracked candidate, per
	/// <see cref="RetentionSweepService"/>'s own contract. Both still appear on the
	/// review list afterward, unchanged.
	/// </summary>
	[Fact]
	public async Task RunSweepAsync_NeverTouchesOrphanOrOutOfScopeContent()
	{
		await _unknownCatalogFiles.RecordSeenAsync("uploads/mystery.iso", null, CancellationToken.None);
		Guid outOfScopeArtifactId = await InsertDepotArtifactAsync("photon/photon-5.0-retired.iso");
		await _reviewList.ReportOutOfScopeAsync(outOfScopeArtifactId, "retired lane", CancellationToken.None);

		RetentionPolicyRepository policies = new(_fixture.ConnectionString);
		RetainedContentStateRepository states = new(_fixture.ConnectionString);
		RetentionSweepService sweep = new(
			states,
			policies,
			_artifacts,
			new RecordingEventPublisher(),
			Options.Create(new Waypoint.Core.Catalog.CatalogOptions { DepotPath = _depotRoot }),
			NullLogger<RetentionSweepService>.Instance);

		// Ask the sweep to treat the out-of-scope artifact as a grace candidate too
		// -- it must be skipped as untracked (no download_retained_content_state
		// row was ever created for it) rather than pruned.
		RetentionSweepReport report = await sweep.RunSweepAsync(
			new RetentionSweepRequest([outOfScopeArtifactId], ListingVerified: true),
			CancellationToken.None);

		Assert.Equal(0, report.EnteredGrace);
		Assert.Equal(0, report.AutoPruned);
		Assert.Equal(1, report.UntrackedCandidatesSkipped);

		IReadOnlyList<ReviewListEntry> entriesAfterSweep = await _reviewList.ListAsync(CancellationToken.None);
		Assert.Equal(2, entriesAfterSweep.Count);
		Assert.Contains(entriesAfterSweep, e => e.Kind == ReviewListEntryKind.Orphan && e.RelativePath == "uploads/mystery.iso");
		Assert.Contains(entriesAfterSweep, e => e.Kind == ReviewListEntryKind.OutOfScope && e.DepotArtifactId == outOfScopeArtifactId);

		// The depot file itself was never written to _depotRoot, so a real prune
		// attempt would have logged a "depot artifact not found" error -- confirming
		// the sweep took the untracked-skip branch, not a purge branch, for this id.
		Assert.Empty(report.Errors);
	}
}
