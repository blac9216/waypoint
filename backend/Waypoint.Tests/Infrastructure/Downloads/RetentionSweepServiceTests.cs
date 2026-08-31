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
/// <see cref="RetentionSweepService"/> (issue #1436, epic #1182) against real
/// Postgres: the grace-entry + alert pass, the timed auto-prune pass (a controllable
/// <see cref="TimeProvider"/> stands in for real elapsed time), the two hard safety
/// contracts this issue's Risk note calls out (never prune a pinned row, never prune
/// on an unverified/partial listing), the immediate-purge path's physical file
/// deletion + per-file logging, and the structural "orphans are never touched"
/// contract (this service never reads <c>unknown_catalog_files</c> at all).
/// </summary>
[Collection("Postgres")]
public sealed class RetentionSweepServiceTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _depotRoot;
	private RetainedContentStateRepository _states = null!;
	private RetentionPolicyRepository _policies = null!;

	public RetentionSweepServiceTests(PostgresFixture fixture)
	{
		_fixture = fixture;
		_depotRoot = Directory.CreateTempSubdirectory("waypoint-retention-sweep-test-").FullName;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await ResetAsync();
		_states = new RetainedContentStateRepository(_fixture.ConnectionString);
		_policies = new RetentionPolicyRepository(_fixture.ConnectionString);
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

	private RetentionSweepService CreateService(FakeTimeProvider clock, RecordingEventPublisher? events = null) => new(
		_states,
		_policies,
		new DepotArtifactRepository(_fixture.ConnectionString),
		events ?? new RecordingEventPublisher(),
		Options.Create(new CatalogOptions { DepotPath = _depotRoot }),
		NullLogger<RetentionSweepService>.Instance,
		clock);

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
	public async Task RunSweepAsync_TrackedCandidate_EntersGraceAndRaisesAlert()
	{
		Guid artifactId = await InsertDepotArtifactAsync("candidate-enters-grace");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		RecordingEventPublisher events = new();
		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow), events);

		RetentionSweepReport report = await service.RunSweepAsync(
			new RetentionSweepRequest([artifactId], ListingVerified: true), CancellationToken.None);

		Assert.False(report.Skipped);
		Assert.Equal(1, report.EnteredGrace);
		Assert.Equal(0, report.AutoPruned);
		Assert.Empty(report.Errors);

		RetainedContentState? state = await _states.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Grace, state!.State);
		Assert.NotNull(state.GraceStartedAt);
		Assert.Contains(events.EmittedPayloads, payload => payload.Contains("grace_entered") && payload.Contains(artifactId.ToString()));
	}

	[Fact]
	public async Task RunSweepAsync_UntrackedCandidate_IsSkippedNotInserted()
	{
		Guid artifactId = await InsertDepotArtifactAsync("never-tracked");
		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

		RetentionSweepReport report = await service.RunSweepAsync(
			new RetentionSweepRequest([artifactId], ListingVerified: true), CancellationToken.None);

		Assert.Equal(0, report.EnteredGrace);
		Assert.Equal(1, report.UntrackedCandidatesSkipped);
		Assert.Empty(report.Errors);

		RetainedContentState? state = await _states.GetByDepotArtifactIdAsync(artifactId, CancellationToken.None);
		Assert.Null(state); // never inserted -- EnsureTrackedAsync (an INSERT) is outside the runner's granted scope
	}

	[Fact]
	public async Task RunSweepAsync_ListingUnverified_TakesNoActionAtAll()
	{
		Guid artifactId = await InsertDepotArtifactAsync("unverified-listing-candidate");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

		RetentionSweepReport report = await service.RunSweepAsync(
			new RetentionSweepRequest([artifactId], ListingVerified: false), CancellationToken.None);

		Assert.True(report.Skipped);
		Assert.NotNull(report.SkippedReason);
		Assert.Equal(0, report.EnteredGrace);
		Assert.Equal(0, report.AutoPruned);

		RetainedContentState? state = await _states.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Tracked, state!.State); // untouched
	}

	[Fact]
	public async Task RunSweepAsync_PinnedContent_IsNeverAutoPruned()
	{
		Guid artifactId = await InsertDepotArtifactAsync("pinned-candidate");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		await _states.TransitionAsync(stateId, RetainedContentStates.Grace, CancellationToken.None);
		await _states.PinAsync(stateId, "operator-1", "keep for audit", CancellationToken.None);

		// A clock far past any plausible grace window -- if pinned rows were pruned by
		// state-blind elapsed-time logic alone, this would catch it.
		FakeTimeProvider clock = new(DateTimeOffset.UtcNow.AddYears(1));
		RetentionSweepService service = CreateService(clock);

		RetentionSweepReport report = await service.RunSweepAsync(
			new RetentionSweepRequest([], ListingVerified: true), CancellationToken.None);

		Assert.Equal(0, report.AutoPruned);
		RetainedContentState? state = await _states.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Pinned, state!.State);
	}

	[Fact]
	public async Task RunSweepAsync_AutoPrunesOnlyAfterGraceWindowElapses_WithControllableClock()
	{
		await _policies.UpsertAsync(RetentionPolicyScopes.Default, gracePeriodDays: 2, graceMaxRefreshes: 0, ManualDownloadDialOptions.Review, CancellationToken.None);

		DateTimeOffset start = DateTimeOffset.UtcNow;

		Guid dueArtifactId = await InsertDepotArtifactAsync("grace-window-due");
		Guid dueStateId = await _states.EnsureTrackedAsync(dueArtifactId, null, CancellationToken.None);
		await _states.TransitionAsync(dueStateId, RetainedContentStates.Grace, CancellationToken.None);

		Guid notDueArtifactId = await InsertDepotArtifactAsync("grace-window-not-due");
		Guid notDueStateId = await _states.EnsureTrackedAsync(notDueArtifactId, null, CancellationToken.None);
		await _states.TransitionAsync(notDueStateId, RetainedContentStates.Grace, CancellationToken.None);

		WriteDepotFile("grace-window-due");
		WriteDepotFile("grace-window-not-due");

		// One day in: neither is due yet (2-day window).
		RetentionSweepService atOneDay = CreateService(new FakeTimeProvider(start.AddDays(1)));
		RetentionSweepReport reportAtOneDay = await atOneDay.RunSweepAsync(new RetentionSweepRequest([], ListingVerified: true), CancellationToken.None);
		Assert.Equal(0, reportAtOneDay.AutoPruned);

		// Three days in: the due row crosses the 2-day window, the other has not aged
		// (its own grace_started_at is effectively the same moment, so it is due too
		// under a single shared policy -- distinguish by re-checking both independently
		// once one has been purged).
		RetentionSweepService atThreeDays = CreateService(new FakeTimeProvider(start.AddDays(3)));
		RetentionSweepReport reportAtThreeDays = await atThreeDays.RunSweepAsync(new RetentionSweepRequest([], ListingVerified: true), CancellationToken.None);
		Assert.Equal(2, reportAtThreeDays.AutoPruned);

		RetainedContentState? dueState = await _states.GetAsync(dueStateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Purged, dueState!.State);
		Assert.False(File.Exists(Path.Combine(_depotRoot, "grace-window-due")));
	}

	[Fact]
	public async Task RunSweepAsync_NeverTouchesUnknownCatalogFiles()
	{
		UnknownCatalogFileRepository unknownFiles = new(_fixture.ConnectionString);
		await unknownFiles.RecordSeenAsync("orphan-file-not-in-any-subscription", 1234, CancellationToken.None);
		IReadOnlyList<UnknownCatalogFile> before = await unknownFiles.ListAsync(CancellationToken.None);

		Guid artifactId = await InsertDepotArtifactAsync("in-scope-candidate");
		await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);

		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow.AddYears(1)));
		await service.RunSweepAsync(new RetentionSweepRequest([artifactId], ListingVerified: true), CancellationToken.None);

		IReadOnlyList<UnknownCatalogFile> after = await unknownFiles.ListAsync(CancellationToken.None);
		Assert.Equal(before.Count, after.Count);
		Assert.Equal(before[0].LastSeenAt, after[0].LastSeenAt); // not even touched (no re-touch), let alone removed
	}

	[Fact]
	public async Task PurgeImmediatelyAsync_TrackedContent_SkipsTheGraceWaitAndDeletesTheFile()
	{
		Guid artifactId = await InsertDepotArtifactAsync("immediate-purge-target");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		WriteDepotFile("immediate-purge-target");

		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

		RetentionPurgeOutcome outcome = await service.PurgeImmediatelyAsync(stateId, "operator-1", "policy violation", CancellationToken.None);

		Assert.True(outcome.Purged);
		Assert.Null(outcome.Error);
		Assert.False(File.Exists(Path.Combine(_depotRoot, "immediate-purge-target")));

		RetainedContentState? state = await _states.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Purged, state!.State);
		Assert.NotNull(state.PurgedAt);
	}

	[Fact]
	public async Task PurgeImmediatelyAsync_AlreadyAbsentFile_StillTransitionsToPurged()
	{
		Guid artifactId = await InsertDepotArtifactAsync("immediate-purge-no-file-on-disk");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		// Deliberately never write the file -- a catalog row can exist with no bytes
		// present (e.g. already removed out of band); the purge must not fail on that.

		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

		RetentionPurgeOutcome outcome = await service.PurgeImmediatelyAsync(stateId, "operator-1", null, CancellationToken.None);

		Assert.True(outcome.Purged);
		RetainedContentState? state = await _states.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Purged, state!.State);
	}

	[Fact]
	public async Task PurgeImmediatelyAsync_PinnedContent_IsRefused()
	{
		Guid artifactId = await InsertDepotArtifactAsync("pinned-immediate-purge-target");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		await _states.PinAsync(stateId, "operator-1", null, CancellationToken.None);

		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

		RetentionPurgeOutcome outcome = await service.PurgeImmediatelyAsync(stateId, "operator-2", "trying anyway", CancellationToken.None);

		Assert.False(outcome.Purged);
		Assert.NotNull(outcome.Error);
		RetainedContentState? state = await _states.GetAsync(stateId, CancellationToken.None);
		Assert.Equal(RetainedContentStates.Pinned, state!.State);
	}

	[Fact]
	public async Task PurgeImmediatelyAsync_AlreadyPurged_IsANoOpNotAnError()
	{
		Guid artifactId = await InsertDepotArtifactAsync("already-purged-target");
		Guid stateId = await _states.EnsureTrackedAsync(artifactId, null, CancellationToken.None);
		await _states.TransitionAsync(stateId, RetainedContentStates.Grace, CancellationToken.None);
		await _states.TransitionAsync(stateId, RetainedContentStates.PendingPurge, CancellationToken.None);
		await _states.TransitionAsync(stateId, RetainedContentStates.Purged, CancellationToken.None);

		RetentionSweepService service = CreateService(new FakeTimeProvider(DateTimeOffset.UtcNow));

		RetentionPurgeOutcome outcome = await service.PurgeImmediatelyAsync(stateId, "operator-1", null, CancellationToken.None);

		Assert.False(outcome.Purged);
		Assert.NotNull(outcome.Error);
	}

	private void WriteDepotFile(string relativePath) =>
		File.WriteAllText(Path.Combine(_depotRoot, relativePath), "fixture bytes");
}
