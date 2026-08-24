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

using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.Runs;
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Scans;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #594 (epic #577): <see cref="PurgeJobHandler"/> unit coverage against a
/// temp-directory artifact root and <see cref="FakeRunPurgeRepository"/> -- no Postgres
/// dependency, mirroring <c>ManagedToolInstallJobHandlerTests</c>'s "fake the
/// repository interface, use a real temp filesystem" pattern. Covers: successful
/// deletion of an existing file set, tolerance of already-missing files, path
/// confinement (defense in depth against the issue's Risk note), and the
/// partial-failure/retry path via an injected undeletable file.
/// </summary>
public sealed class PurgeJobHandlerTests : IDisposable
{
	private readonly string _artifactRoot = Directory.CreateTempSubdirectory("waypoint-purge-artifacts-").FullName;

	public void Dispose()
	{
		try
		{
			// Best effort: a test that locked a file down to prevent its own deletion
			// must restore write permission before recursive delete can succeed.
			foreach (string file in Directory.EnumerateFiles(_artifactRoot))
			{
				File.SetAttributes(file, FileAttributes.Normal);
			}

			Directory.Delete(_artifactRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best-effort cleanup only -- CI temp-dir sweep handles anything left over.
		}
	}

	private sealed class FakeRunPurgeRepository : IRunPurgeRepository
	{
		public Guid? RunIdForArtifactJob { get; set; }
		public bool? LastSucceeded { get; private set; }
		public int? LastArtifactsDeleted { get; private set; }
		public string? LastError { get; private set; }
		public int ReportCount { get; private set; }

		public Task<RunPurgeStatus?> GetStatusAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<RunPurgeStatus?>(null);

		public Task<RunPurgeTombstone?> GetTombstoneAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult<RunPurgeTombstone?>(null);

		public Task<Guid?> FindRunIdByArtifactJobIdAsync(Guid artifactJobId, CancellationToken cancellationToken) => Task.FromResult(RunIdForArtifactJob);

		public Task<RunPurgeStatus> CreateAsync(Guid runId, string requestedBy, string priorState, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by PurgeJobHandler.");

		public Task MarkDbPhaseDoneAsync(Guid runId, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by PurgeJobHandler.");

		public Task MarkArtifactJobEnqueuedAsync(Guid runId, Guid jobId, int artifactsTotal, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by PurgeJobHandler.");

		public Task ReportArtifactOutcomeAsync(Guid runId, bool succeeded, int artifactsDeleted, string? lastError, CancellationToken cancellationToken)
		{
			ReportCount++;
			LastSucceeded = succeeded;
			LastArtifactsDeleted = artifactsDeleted;
			LastError = lastError;
			return Task.CompletedTask;
		}

		public Task<RunPurgeTombstone> CompleteAsync(Guid runId, string runType, string actor, string priorState, int artifactsDeleted, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by PurgeJobHandler.");
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private PurgeJobHandler CreateHandler(FakeRunPurgeRepository purges)
	{
		ScanOptions scanOptions = new() { ArtifactStorePath = _artifactRoot };
		return new PurgeJobHandler(purges, Options.Create(scanOptions), NullLogger<PurgeJobHandler>.Instance);
	}

	private static JobExecutionContext ContextFor(Guid purgeJobId, string payload)
	{
		ClaimedJob job = new(
			Id: purgeJobId, RunId: null, JobType: "purge", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 6, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	[Fact]
	public async Task ExecuteAsync_DeletesAllThreeArtifactFilesPerJob_ReportsSuccess()
	{
		Guid scanJobId = Guid.NewGuid();
		File.WriteAllText(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId), "{}");
		File.WriteAllText(ScanArtifactPaths.AttestedHdf(_artifactRoot, scanJobId), "{}");
		File.WriteAllText(ScanArtifactPaths.Ckl(_artifactRoot, scanJobId), "<CHECKLIST/>");

		Guid purgeJobId = Guid.NewGuid();
		Guid targetRunId = Guid.NewGuid();
		FakeRunPurgeRepository purges = new() { RunIdForArtifactJob = targetRunId };
		PurgeJobHandler handler = CreateHandler(purges);

		string payload = JsonSerializer.Serialize(new { job_ids = new[] { scanJobId.ToString() } });
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(purgeJobId, payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.False(File.Exists(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId)));
		Assert.False(File.Exists(ScanArtifactPaths.AttestedHdf(_artifactRoot, scanJobId)));
		Assert.False(File.Exists(ScanArtifactPaths.Ckl(_artifactRoot, scanJobId)));
		Assert.Equal(1, purges.ReportCount);
		Assert.True(purges.LastSucceeded);
		Assert.Equal(3, purges.LastArtifactsDeleted);
	}

	[Fact]
	public async Task ExecuteAsync_JobNeverReachedAttestOrConvert_MissingFilesAreNotFailures()
	{
		// Only the raw HDF exists -- attested/CKL were never produced (job failed
		// before those stages). Both absent files must be tolerated, not reported as
		// errors (see the handler's doc comment: "not every job produced every
		// artifact kind").
		Guid scanJobId = Guid.NewGuid();
		File.WriteAllText(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId), "{}");

		Guid targetRunId = Guid.NewGuid();
		FakeRunPurgeRepository purges = new() { RunIdForArtifactJob = targetRunId };
		PurgeJobHandler handler = CreateHandler(purges);

		string payload = JsonSerializer.Serialize(new { job_ids = new[] { scanJobId.ToString() } });
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.True(purges.LastSucceeded);
		Assert.Equal(3, purges.LastArtifactsDeleted); // deletion is "present and removed OR already absent" -- all three count as handled.
	}

	[Fact]
	public async Task ExecuteAsync_ReInvokedAfterAllFilesAlreadyGone_SucceedsIdempotently()
	{
		// Simulates a retry after a prior successful run of this same handler: no
		// files exist at all. Must still succeed (idempotent retry), never fail
		// because "nothing was there to delete".
		Guid scanJobId = Guid.NewGuid();
		Guid targetRunId = Guid.NewGuid();
		FakeRunPurgeRepository purges = new() { RunIdForArtifactJob = targetRunId };
		PurgeJobHandler handler = CreateHandler(purges);

		string payload = JsonSerializer.Serialize(new { job_ids = new[] { scanJobId.ToString() } });
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.True(purges.LastSucceeded);
	}

	[Fact]
	public async Task ExecuteAsync_UndeletableFile_ReportsFailureAndIsRetryable()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			// File permission semantics differ enough on Windows CI that this
			// scenario is skipped there -- the read-only-attribute trick below is
			// POSIX-file-permission-shaped and this repo's CI targets Linux
			// containers (deploy/*, docs/testing.md).
			return;
		}

		Guid scanJobId = Guid.NewGuid();
		string rawHdfPath = ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId);
		File.WriteAllText(rawHdfPath, "{}");

		// Lock the file's parent directory to read-only-execute so File.Delete on the
		// file inside it throws UnauthorizedAccessException -- the standard way to
		// force a real permission-denied deletion failure in a test without needing
		// root/non-root separation.
		string lockedDirectory = Path.Combine(_artifactRoot, "locked");
		Directory.CreateDirectory(lockedDirectory);
		string lockedFilePath = Path.Combine(lockedDirectory, $"{scanJobId:N}.json");
		File.WriteAllText(lockedFilePath, "{}");
		File.SetUnixFileMode(lockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

		try
		{
			ScanOptions scanOptions = new() { ArtifactStorePath = lockedDirectory };
			Guid targetRunId = Guid.NewGuid();
			FakeRunPurgeRepository purges = new() { RunIdForArtifactJob = targetRunId };
			PurgeJobHandler handler = new(purges, Options.Create(scanOptions), NullLogger<PurgeJobHandler>.Instance);

			string payload = JsonSerializer.Serialize(new { job_ids = new[] { scanJobId.ToString() } });
			JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), payload), CancellationToken.None);

			Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
			Assert.False(purges.LastSucceeded);
			Assert.NotNull(purges.LastError);

			// Retry: restore permissions (simulating an operator/ops fix, or simply
			// that the transient condition clears) and re-invoke -- must now succeed,
			// proving the failure was retryable and not a permanent poison state.
			File.SetUnixFileMode(lockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			JobExecutionOutcome retryOutcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), payload), CancellationToken.None);
			Assert.Equal(JobOutcomeKind.Succeeded, retryOutcome.Kind);
			Assert.False(File.Exists(lockedFilePath));
		}
		finally
		{
			File.SetUnixFileMode(lockedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	[Fact]
	public async Task ExecuteAsync_MalformedPayload_FailsWithoutTouchingFilesystem()
	{
		FakeRunPurgeRepository purges = new();
		PurgeJobHandler handler = CreateHandler(purges);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), "not json"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Equal(0, purges.ReportCount);
	}

	[Fact]
	public async Task ExecuteAsync_EmptyJobIds_FailsWithoutTouchingFilesystem()
	{
		FakeRunPurgeRepository purges = new();
		PurgeJobHandler handler = CreateHandler(purges);

		string payload = JsonSerializer.Serialize(new { job_ids = Array.Empty<string>() });
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Equal(0, purges.ReportCount);
	}

	[Fact]
	public async Task ExecuteAsync_NoRunPurgesRowReferencesThisJob_FailsClosed()
	{
		Guid scanJobId = Guid.NewGuid();
		File.WriteAllText(ScanArtifactPaths.RawHdf(_artifactRoot, scanJobId), "{}");

		// RunIdForArtifactJob left null -- simulates the "should not happen" case
		// documented on PurgeJobHandler.ExecuteAsync.
		FakeRunPurgeRepository purges = new();
		PurgeJobHandler handler = CreateHandler(purges);

		string payload = JsonSerializer.Serialize(new { job_ids = new[] { scanJobId.ToString() } });
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(Guid.NewGuid(), payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Equal(0, purges.ReportCount);
	}
}
