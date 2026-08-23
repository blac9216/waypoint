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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.SystemState;
using Waypoint.DownloadRunner;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Resources;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #443 against real Postgres: the download-runner's readiness reporter writes its
/// database-persisted heartbeat twin only when the database is actually reachable
/// (<see cref="ReadinessSnapshot.DatabaseReachable"/> gates the shared engine's
/// <c>canHeartbeat</c> predicate -- see issue #484, which restored this gate after the
/// #461/#473 readiness consolidation briefly dropped it). The pure-unit
/// <c>ReadinessReportingHostedServiceTests</c> can't reach the reachable branch (no
/// configured database), so this exercises it end to end: a real connection string makes
/// the probe succeed, and the upserted <c>worker_registry</c> row is read back with the
/// runner's allowlist and readiness. <see cref="StartThenImmediateStop_WithUnreachableDatabase_SkipsHeartbeatWithoutError"/>
/// covers the opposite branch -- an invalid connection string, so the probe fails and the
/// heartbeat is skipped rather than attempted-and-caught.
/// </summary>
[Collection("Postgres")]
public sealed class DownloadRunnerHeartbeatTests : IAsyncLifetime
{
	private readonly PostgresFixture _fixture;
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-download-runner-heartbeat-").FullName;

	public DownloadRunnerHeartbeatTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync().ConfigureAwait(false);
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE worker_registry RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync().ConfigureAwait(false);
	}

	public Task DisposeAsync()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}

		return Task.CompletedTask;
	}

	[Fact]
	public async Task StartThenImmediateStop_WithReachableDatabase_UpsertsWorkerRegistryRow()
	{
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:Waypoint"] = _fixture.ConnectionString,
			})
			.Build();

		WorkerRegistryRepository registry = new(_fixture.ConnectionString);

		ReadinessReportingHostedService service = new(
			configuration,
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions
			{
				ReadinessFilePath = readinessFile,
				ReadinessInterval = TimeSpan.FromMinutes(5),
				WorkerId = "download-runner-under-test",
			}),
			CreateResourceAdmission(),
			registry,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		WorkerHeartbeat row = Assert.Single(await registry.ListAsync(CancellationToken.None));
		Assert.Equal("download-runner-under-test", row.WorkerId);
		// The runner reports its full allowlist; all required paths exist, so it is ready.
		Assert.True(row.Ready);
		Assert.Equal(
			new HashSet<string>(StringComparer.Ordinal) { "catalog-index", "download" },
			new HashSet<string>(row.JobTypes, StringComparer.Ordinal));
	}

	[Fact]
	public async Task StartThenImmediateStop_WithUnreachableDatabase_SkipsHeartbeatWithoutError()
	{
		// Issue #484: a connection string is configured (so IWorkerRegistryWriter is
		// non-null) but points at nothing reachable -- BuildSnapshotAsync's own database
		// probe fails, so DatabaseReachable is false. The shared engine's canHeartbeat
		// predicate must skip HeartbeatAsync entirely in that case (not attempt it and
		// catch the failure): the registry is never called, and worker_registry stays
		// empty for this worker rather than gaining a Ready:false row for a worker whose
		// self-reported job types and capacity the control plane never actually saw.
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		// A syntactically valid Npgsql connection string pointed at a port nothing
		// listens on -- OpenAsync fails fast rather than hanging for the test timeout.
		const string unreachableConnectionString = "Host=127.0.0.1;Port=1;Database=waypoint;Username=waypoint;Password=waypoint;Timeout=1";

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:Waypoint"] = unreachableConnectionString,
			})
			.Build();

		CountingWorkerRegistry registry = new();

		ReadinessReportingHostedService service = new(
			configuration,
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions
			{
				ReadinessFilePath = readinessFile,
				ReadinessInterval = TimeSpan.FromMinutes(5),
				WorkerId = "download-runner-db-unreachable",
			}),
			CreateResourceAdmission(),
			registry,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.Equal(0, registry.Count);
		Assert.Empty(await new WorkerRegistryRepository(_fixture.ConnectionString).ListAsync(CancellationToken.None));
		// The file snapshot still lands even though the heartbeat was skipped.
		Assert.True(File.Exists(readinessFile));
	}

	[Fact]
	public async Task StartThenImmediateStop_WhenHeartbeatWriterThrows_DoesNotCrashReporter()
	{
		// databaseReachable is true (real connection string), so the heartbeat branch is
		// entered, but the writer throws -- the reporter must log-and-continue (best-effort),
		// still land the file snapshot, and never propagate the failure out of the host.
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:Waypoint"] = _fixture.ConnectionString,
			})
			.Build();

		ThrowingWorkerRegistry registry = new();

		ReadinessReportingHostedService service = new(
			configuration,
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions
			{
				ReadinessFilePath = readinessFile,
				ReadinessInterval = TimeSpan.FromMinutes(5),
				WorkerId = "download-runner-throwing",
			}),
			CreateResourceAdmission(),
			registry,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.True(registry.WasCalled);
		// The file snapshot still lands even though the heartbeat threw.
		Assert.True(File.Exists(readinessFile));
	}

	[Fact]
	public async Task RunningLongEnoughForARefreshTick_HeartbeatsAgain()
	{
		// Drives ExecuteAsync's periodic loop (not just the StartAsync first snapshot): a
		// short ReadinessInterval lets the timer tick before StopAsync, so the periodic
		// snapshot-and-heartbeat path runs and upserts the row again (last_seen advances).
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:Waypoint"] = _fixture.ConnectionString,
			})
			.Build();

		CountingWorkerRegistry registry = new();

		ReadinessReportingHostedService service = new(
			configuration,
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions
			{
				ReadinessFilePath = readinessFile,
				ReadinessInterval = TimeSpan.FromMilliseconds(30),
				WorkerId = "download-runner-ticking",
			}),
			CreateResourceAdmission(),
			registry,
			NullLogger<ReadinessReportingHostedService>.Instance);

		await service.StartAsync(CancellationToken.None);
		await Task.Delay(TimeSpan.FromMilliseconds(200));
		await service.StopAsync(CancellationToken.None);

		Assert.True(registry.Count >= 2, $"expected at least 2 heartbeats, got {registry.Count}");
	}

	private sealed class CountingWorkerRegistry : IWorkerRegistryWriter
	{
		private int _count;

		public int Count => Volatile.Read(ref _count);

		public Task HeartbeatAsync(string workerId, IReadOnlyList<string> jobTypes, bool ready, IReadOnlyList<StarvedWorkerJobType> starvedJobTypes, CancellationToken cancellationToken, bool? toolPresent = null)
		{
			Interlocked.Increment(ref _count);
			return Task.CompletedTask;
		}
	}

	private sealed class ThrowingWorkerRegistry : IWorkerRegistryWriter
	{
		public bool WasCalled { get; private set; }

		public Task HeartbeatAsync(string workerId, IReadOnlyList<string> jobTypes, bool ready, IReadOnlyList<StarvedWorkerJobType> starvedJobTypes, CancellationToken cancellationToken, bool? toolPresent = null)
		{
			WasCalled = true;
			throw new InvalidOperationException("simulated heartbeat write failure");
		}
	}

	private static ResourceAdmissionController CreateResourceAdmission()
	{
		RunnerResourceOptions options = new()
		{
			CgroupRoot = "/does/not/exist",
			FallbackCpuCores = 1.0,
			FallbackMemoryBytes = 1024L * 1024 * 1024,
		};
		return new(
			Options.Create(options),
			new CgroupResourceDiscovery(Options.Create(options), NullLogger<CgroupResourceDiscovery>.Instance),
			NullLogger<ResourceAdmissionController>.Instance);
	}
}
