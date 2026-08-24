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

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.DownloadRunner;
using Waypoint.Runner.Readiness;
using Waypoint.Runner.Resources;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.DownloadRunner;

/// <summary>
/// Issue #441's "runner-local health/capability reporting" acceptance criterion: the
/// background reporter writes a readiness snapshot that fails closed when a required
/// dependency (here: no configured database) is unavailable, reports the exact
/// job-type allowlist, and does not let a merely-absent managed tool flip the whole
/// snapshot to not-ready (ADR-0015).
/// </summary>
public sealed class ReadinessReportingHostedServiceTests : IDisposable
{
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-download-runner-readiness-test-").FullName;

	public void Dispose()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	private static IConfiguration EmptyConfiguration() => new ConfigurationBuilder().Build();

	/// <summary>
	/// A real controller pointed at a nonexistent cgroup root -- exercises the
	/// documented fallback path (issue #437) rather than a mock, since this type has no
	/// interface seam and its constructor-time discovery is cheap/deterministic. Supplies
	/// an unusable-by-design <see cref="IHostCapabilitySource"/> (ADR-0018, issue #555)
	/// so this test host's own real CPU/memory never intervenes between the missing
	/// cgroup root and the configured fallback constants these tests assert against.
	/// </summary>
	private static ResourceAdmissionController CreateResourceAdmission(
		double fallbackCpuCores = 1.0,
		long fallbackMemoryBytes = 1024L * 1024 * 1024)
	{
		RunnerResourceOptions options = new()
		{
			CgroupRoot = "/does/not/exist",
			FallbackCpuCores = fallbackCpuCores,
			FallbackMemoryBytes = fallbackMemoryBytes,
		};
		return new(
			Options.Create(options),
			new CgroupResourceDiscovery(Options.Create(options), new UnusableHostCapabilitySource(), NullLogger<CgroupResourceDiscovery>.Instance),
			NullLogger<ResourceAdmissionController>.Instance);
	}

	/// <summary>Zero/zero always fails <c>CgroupResourceDiscovery</c>'s host-derivation usability check, forcing the constant fallback.</summary>
	private sealed class UnusableHostCapabilitySource : IHostCapabilitySource
	{
		public double AvailableCpuCores() => 0;

		public long TotalMemoryBytes() => 0;
	}

	[Fact]
	public async Task NoConnectionString_ReportsNotReady()
	{
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		ReadinessReportingHostedService service = new(
			EmptyConfiguration(),
			new FakeManagedToolPresenceChecker(present: false),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions { ReadinessFilePath = readinessFile, ReadinessInterval = TimeSpan.FromMinutes(5) }),
			CreateResourceAdmission(),
			null,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.True(File.Exists(readinessFile));
		ReadinessSnapshot snapshot = JsonSerializer.Deserialize<ReadinessSnapshot>(await File.ReadAllTextAsync(readinessFile))!;

		Assert.False(snapshot.Ready);
		Assert.True(snapshot.ArtifactStoreWritable);
		Assert.True(snapshot.DepotPathReadable);
		Assert.False(snapshot.ToolPresent);
		Assert.Equal(Waypoint.DownloadRunner.DownloadRunnerJobTypes.Allowed, new HashSet<string>(snapshot.JobTypes, StringComparer.Ordinal));
	}

	[Fact]
	public async Task UnreadableDepotPath_ReportsNotReadyAndDepotPathReadableFalse()
	{
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "does-not-exist");
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		ReadinessReportingHostedService service = new(
			EmptyConfiguration(),
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions { ReadinessFilePath = readinessFile, ReadinessInterval = TimeSpan.FromMinutes(5) }),
			CreateResourceAdmission(),
			null,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		ReadinessSnapshot snapshot = JsonSerializer.Deserialize<ReadinessSnapshot>(await File.ReadAllTextAsync(readinessFile))!;
		Assert.False(snapshot.DepotPathReadable);
		Assert.False(snapshot.Ready);
		// Reported for operator visibility even though it does not gate readiness (ADR-0015).
		Assert.True(snapshot.ToolPresent);
	}

	[Fact]
	public async Task Snapshot_IncludesResourceAdmissionCapacity()
	{
		// Issue #437: the download-runner's snapshot must also carry BuildCapacityReport()'s
		// resource-admission state. The controller's nonexistent cgroup root forces the
		// documented conservative fallback, which the capacity block surfaces verbatim.
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		ReadinessReportingHostedService service = new(
			EmptyConfiguration(),
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions { ReadinessFilePath = readinessFile, ReadinessInterval = TimeSpan.FromMinutes(5) }),
			CreateResourceAdmission(fallbackCpuCores: 2.0, fallbackMemoryBytes: 4L * 1024 * 1024 * 1024),
			null,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		ReadinessSnapshot snapshot = JsonSerializer.Deserialize<ReadinessSnapshot>(await File.ReadAllTextAsync(readinessFile))!;

		Assert.NotNull(snapshot.Capacity);
		Assert.Equal(HostResourceLimitSource.Fallback.ToString(), snapshot.Capacity!.Source);
		Assert.True(snapshot.Capacity.IsFallback);
		Assert.Equal(2.0, snapshot.Capacity.DiscoveredCpuCores, precision: 6);
		Assert.Equal(4L * 1024 * 1024 * 1024, snapshot.Capacity.DiscoveredMemoryBytes);
		Assert.Equal(2.0, snapshot.Capacity.EffectiveCpuCores, precision: 6);
		Assert.Equal(4L * 1024 * 1024 * 1024, snapshot.Capacity.EffectiveMemoryBytes);
		Assert.Equal(0, snapshot.Capacity.AdmittedJobCount);
		Assert.Empty(snapshot.Capacity.StarvedJobTypes);
	}

	[Fact]
	public async Task Snapshot_WithStarvedJobType_ReportsItInCapacity()
	{
		// Issue #467: a job type this runner can never admit (profile exceeds a
		// 1-core budget) must show up in the snapshot's Capacity.StarvedJobTypes, not
		// just in the log -- this is what worker_registry's heartbeat and GET /system
		// read to surface admission starvation.
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string readinessFile = Path.Combine(_tempDirectory, "readiness.json");

		ResourceAdmissionController resourceAdmission = CreateResourceAdmission(fallbackCpuCores: 1.0, fallbackMemoryBytes: 4L * 1024 * 1024 * 1024);
		resourceAdmission.TryAdmit(Guid.NewGuid(), "scan"); // 2.0 cores > 1.0-core budget -- permanently starved.

		ReadinessReportingHostedService service = new(
			EmptyConfiguration(),
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions { ReadinessFilePath = readinessFile, ReadinessInterval = TimeSpan.FromMinutes(5) }),
			resourceAdmission,
			null,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		ReadinessSnapshot snapshot = JsonSerializer.Deserialize<ReadinessSnapshot>(await File.ReadAllTextAsync(readinessFile))!;

		StarvedJobTypeReport starved = Assert.Single(snapshot.Capacity!.StarvedJobTypes);
		Assert.Equal("scan", starved.JobType);
		Assert.True(starved.Permanent);
	}

	[Fact]
	public async Task UnwritableReadinessPath_DoesNotThrowAndWritesNoFile()
	{
		// The readiness file's parent path is a *file*, so Directory.CreateDirectory and the
		// write-then-move both fail. The reporter must log-and-continue rather than crash the
		// runner over a health side channel, leaving no snapshot file behind.
		string artifactStore = Path.Combine(_tempDirectory, "artifacts");
		string depotPath = Path.Combine(_tempDirectory, "depot");
		Directory.CreateDirectory(depotPath);
		string blockingFile = Path.Combine(_tempDirectory, "not-a-directory");
		await File.WriteAllTextAsync(blockingFile, "occupied");
		string readinessFile = Path.Combine(blockingFile, "readiness.json");

		ReadinessReportingHostedService service = new(
			EmptyConfiguration(),
			new FakeManagedToolPresenceChecker(present: true),
			Options.Create(new DownloadOptions { ArtifactStorePath = artifactStore }),
			Options.Create(new CatalogOptions { DepotPath = depotPath }),
			Options.Create(new DownloadRunnerOptions { ReadinessFilePath = readinessFile, ReadinessInterval = TimeSpan.FromMinutes(5) }),
			CreateResourceAdmission(),
			null,
			NullLogger<ReadinessReportingHostedService>.Instance);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.False(File.Exists(readinessFile));
	}
}
