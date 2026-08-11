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

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Pagination;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.PowerShell;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #10 (M1 vertical slice) full-loop acceptance, through the REAL loop: a
/// <c>download</c> job is fanned out, the dispatcher claims it, the real
/// <see cref="DownloadJobHandler"/> invokes the stub <c>Invoke-WaypointDownload</c>
/// module in-process, verifies sha256 against <c>depot_artifacts</c>, and lands the
/// file on the (temp-directory) artifact store -- with the three acceptance criteria
/// from issue #10 proven end to end: (1) queue -&gt; progress events -&gt; verified +
/// file present, (2) a corrupted fixture -&gt; failed/quarantined/retriable, one
/// failure never blocking a sibling download (Continue policy), and (3) a clean retry
/// after quarantine succeeds.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool.
public sealed class DownloadJobHandlerEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDownloadStubModule", "WaypointDownloadStubModule.psm1");
	private static readonly string FixtureSourcePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDownloadFixtures", "sample-artifact.bin");

	private readonly PostgresFixture _fixture;
	private readonly string _storeDirectory = Directory.CreateTempSubdirectory("wp-download-store").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private WaypointRunspacePool _pool = null!;
	private DepotArtifactRepository _artifacts = null!;
	private DownloadRepository _downloads = null!;
	private DownloadJobHandler _handler = null!;

	public DownloadJobHandlerEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetCatalogDataAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		PowerShellOptions powerShellOptions = new() { MaxRunspaces = 2 };
		powerShellOptions.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);
		_pool = new WaypointRunspacePool(wrappedPsOptions, NullLogger<WaypointRunspacePool>.Instance);
		PowerShellExecutor executor = new(_pool, _logBuffer, wrappedPsOptions, NullLogger<PowerShellExecutor>.Instance);

		_artifacts = new DepotArtifactRepository(_fixture.ConnectionString);
		_downloads = new DownloadRepository(_fixture.ConnectionString);

		DownloadOptions downloadOptions = new() { ArtifactStorePath = _storeDirectory };
		_handler = new DownloadJobHandler(executor, _downloads, _artifacts, Options.Create(downloadOptions));
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
		_pool.Dispose();
	}

	public void Dispose()
	{
		Directory.Delete(_storeDirectory, recursive: true);
	}

	private JobDispatcherHostedService CreateDispatcher()
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 };
		return new JobDispatcherHostedService(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	/// <summary>
	/// Acceptance criterion 1: queue -&gt; downloading/verifying/verified
	/// <c>download.progress</c> events on the stream -&gt; the file lands on the
	/// artifact store and the downloads row reaches <c>verified</c>.
	/// </summary>
	[Fact]
	public async Task QueueOneDownload_ReachesVerified_EmitsProgress_AndFileIsPresent()
	{
		string tag = Guid.NewGuid().ToString("N");
		Guid artifactId = await SeedArtifactAsync(tag, correctSha256: true);
		(Guid downloadId, Guid jobId, Guid runId) = await CreateDownloadJobAsync(artifactId, FixtureSourcePath);

		await RunDispatcherUntilTerminalAsync(jobId);

		Assert.Equal("done", await GetJobFieldAsync(jobId, "state"));

		Download? download = await _downloads.GetAsync(downloadId, CancellationToken.None);
		Assert.NotNull(download);
		Assert.Equal(DownloadStates.Verified, download!.State);
		Assert.True(download.BytesDownloaded > 0);

		string expectedPath = Path.Combine(_storeDirectory, tag);
		Assert.True(File.Exists(expectedPath), $"Expected downloaded file at '{expectedPath}'.");

		Assert.True(await EventTypeExistsAsync(JobEventTypes.DownloadProgress, jobId));

		DepotArtifact? artifact = await GetArtifactAsync(artifactId);
		Assert.Equal("present", artifact!.Status);

		_ = runId;
	}

	/// <summary>
	/// Acceptance criterion 2: a corrupted fixture download fails verification,
	/// quarantines the file, and the failure never blocks a sibling artifact queued in
	/// the same run. Both jobs live in ONE run (ADR-0008 fan-out), so this proves the
	/// in-run Continue policy -- the core job-engine guarantee that one job failing does
	/// not halt its siblings -- not run-isolation. The run is asserted to remain
	/// non-aborted after one of its jobs fails.
	/// </summary>
	[Fact]
	public async Task CorruptedDownload_FailsAndQuarantines_WithoutBlockingSiblingDownload()
	{
		string corruptTag = Guid.NewGuid().ToString("N") + "-corrupt";
		string healthyTag = Guid.NewGuid().ToString("N") + "-healthy";
		Guid corruptArtifactId = await SeedArtifactAsync(corruptTag, correctSha256: true);
		Guid healthyArtifactId = await SeedArtifactAsync(healthyTag, correctSha256: true);

		// One run, two download jobs (ADR-0008): the corrupt one and its healthy sibling.
		(Guid runId, IReadOnlyList<(Guid DownloadId, Guid JobId)> jobs) = await CreateDownloadRunAsync(
			(corruptArtifactId, FixtureSourcePath + "#corrupt"),
			(healthyArtifactId, FixtureSourcePath));
		(Guid corruptDownloadId, Guid corruptJobId) = jobs[0];
		(Guid healthyDownloadId, Guid healthyJobId) = jobs[1];

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(corruptJobId);
			await PollUntilTerminalAsync(healthyJobId);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.Equal("failed", await GetJobFieldAsync(corruptJobId, "state"));
		Download? corruptDownload = await _downloads.GetAsync(corruptDownloadId, CancellationToken.None);
		Assert.Equal(DownloadStates.Failed, corruptDownload!.State);
		Assert.Contains("checksum mismatch", corruptDownload.FailureReason ?? string.Empty, StringComparison.Ordinal);
		Assert.Contains("quarantine", corruptDownload.FailureReason ?? string.Empty, StringComparison.Ordinal);

		string quarantinedPath = Path.Combine(_storeDirectory, "quarantine", corruptTag);
		Assert.True(File.Exists(quarantinedPath), $"Expected quarantined file at '{quarantinedPath}'.");
		string liveTargetPath = Path.Combine(_storeDirectory, corruptTag);
		Assert.False(File.Exists(liveTargetPath), "Corrupted file must not remain at the live destination path.");

		DepotArtifact? corruptArtifact = await GetArtifactAsync(corruptArtifactId);
		Assert.Equal("failed", corruptArtifact!.Status);

		// The sibling download is unaffected -- proves the Continue policy applies WITHIN
		// one run: one job's failure never halts a sibling job in the same run.
		Assert.Equal("done", await GetJobFieldAsync(healthyJobId, "state"));
		Download? healthyDownload = await _downloads.GetAsync(healthyDownloadId, CancellationToken.None);
		Assert.Equal(DownloadStates.Verified, healthyDownload!.State);

		// And the run was never aborted by the failing job.
		Assert.NotEqual("aborted", await GetRunStateAsync(runId));
	}

	/// <summary>
	/// Acceptance criterion 2 (continued): after a checksum-mismatch quarantine, a
	/// fresh retry download of the SAME artifact against a good source succeeds --
	/// the quarantine step must not leave a stale partial/quarantined file that blocks
	/// a subsequent clean attempt from writing the live destination path again.
	/// </summary>
	[Fact]
	public async Task AfterQuarantine_RetryWithGoodSource_Succeeds()
	{
		string tag = Guid.NewGuid().ToString("N") + "-retry";
		Guid artifactId = await SeedArtifactAsync(tag, correctSha256: true);

		(_, Guid firstJobId, _) = await CreateDownloadJobAsync(artifactId, FixtureSourcePath + "#corrupt");
		await RunDispatcherUntilTerminalAsync(firstJobId);
		Assert.Equal("failed", await GetJobFieldAsync(firstJobId, "state"));

		(Guid retryDownloadId, Guid retryJobId, _) = await CreateDownloadJobAsync(artifactId, FixtureSourcePath);
		await RunDispatcherUntilTerminalAsync(retryJobId);

		Assert.Equal("done", await GetJobFieldAsync(retryJobId, "state"));
		Download? retryDownload = await _downloads.GetAsync(retryDownloadId, CancellationToken.None);
		Assert.Equal(DownloadStates.Verified, retryDownload!.State);

		string livePath = Path.Combine(_storeDirectory, tag);
		Assert.True(File.Exists(livePath));
	}

	private async Task<Guid> SeedArtifactAsync(string externalId, bool correctSha256)
	{
		string sha256 = correctSha256
			? Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(File.OpenRead(FixtureSourcePath), CancellationToken.None))
			: "0000000000000000000000000000000000000000000000000000000000000000";

		return await _artifacts.UpsertAsync(new DepotArtifactUpsert(externalId, sha256, "indexed", "{}"), CancellationToken.None);
	}

	private async Task<(Guid DownloadId, Guid JobId, Guid RunId)> CreateDownloadJobAsync(Guid artifactId, string sourceUrl)
	{
		Guid downloadId = await _downloads.CreateAsync(artifactId, jobId: null, "tester", CancellationToken.None);

		string payload = JsonSerializer.Serialize(new { download_id = downloadId, depot_artifact_id = artifactId, source_url = sourceUrl });

		Guid runId = await _repository.CreateRunAsync("download", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("download", 6, TargetId: artifactId, Payload: payload)], "tester", CancellationToken.None);

		await _downloads.SetJobAsync(downloadId, jobIds[0], runId, CancellationToken.None);
		return (downloadId, jobIds[0], runId);
	}

	/// <summary>
	/// Fans several download artifacts into ONE run of N jobs (ADR-0008), mirroring what
	/// <c>DownloadsController.QueueDownloads</c> does for a multi-artifact request. Returns
	/// the shared run id and the per-artifact (downloadId, jobId) pairs in request order.
	/// </summary>
	private async Task<(Guid RunId, IReadOnlyList<(Guid DownloadId, Guid JobId)> Jobs)> CreateDownloadRunAsync(
		params (Guid ArtifactId, string SourceUrl)[] artifacts)
	{
		Guid runId = await _repository.CreateRunAsync("download", "{}", credentialId: null, "tester", CancellationToken.None);

		List<Guid> downloadIds = [];
		List<JobSpec> specs = [];
		foreach ((Guid artifactId, string sourceUrl) in artifacts)
		{
			Guid downloadId = await _downloads.CreateAsync(artifactId, jobId: null, "tester", CancellationToken.None);
			downloadIds.Add(downloadId);
			string payload = JsonSerializer.Serialize(new { download_id = downloadId, depot_artifact_id = artifactId, source_url = sourceUrl });
			specs.Add(new JobSpec("download", 6, TargetId: artifactId, Payload: payload));
		}

		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, specs, "tester", CancellationToken.None);

		List<(Guid DownloadId, Guid JobId)> pairs = [];
		for (int i = 0; i < downloadIds.Count; i++)
		{
			await _downloads.SetJobAsync(downloadIds[i], jobIds[i], runId, CancellationToken.None);
			pairs.Add((downloadIds[i], jobIds[i]));
		}

		return (runId, pairs);
	}

	private async Task<string> GetRunStateAsync(Guid runId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT state FROM runs WHERE id = $1", connection);
		query.Parameters.AddWithValue(runId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task RunDispatcherUntilTerminalAsync(Guid jobId)
	{
		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobId);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	private async Task<DepotArtifact?> GetArtifactAsync(Guid artifactId)
	{
		(IReadOnlyList<DepotArtifact> items, long _) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new PageRequest { Limit = 200 }, CancellationToken.None);
		return items.FirstOrDefault(item => item.Id == artifactId);
	}

	private async Task<bool> EventTypeExistsAsync(string eventType, Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new(
			"SELECT count(*) FROM job_events WHERE event_type = $1 AND job_id = $2", connection);
		query.Parameters.AddWithValue(eventType);
		query.Parameters.AddWithValue(jobId);
		return (long)(await query.ExecuteScalarAsync())! > 0;
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}

	private async Task ResetCatalogDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE downloads, depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}
}
