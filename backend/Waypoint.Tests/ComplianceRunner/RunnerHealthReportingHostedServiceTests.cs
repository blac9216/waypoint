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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.ComplianceRunner.Readiness;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Resources;
using Xunit;

namespace Waypoint.Tests.ComplianceRunner;

/// <summary>
/// Issue #440 AC4's "reports its capabilities" plus the readiness-race fix mirrored from
/// the download-runner (#441): the first health report is written in
/// <see cref="RunnerHealthReportingHostedService.StartAsync"/> (awaited before the host
/// reports started), so a StopAsync immediately after StartAsync -- exactly what this
/// test does -- always finds the report file already present. Deferring that first write
/// into ExecuteAsync would let StopAsync race the file's existence and make this flaky.
/// </summary>
public sealed class RunnerHealthReportingHostedServiceTests : IDisposable
{
	private readonly string _tempRoot = Directory.CreateTempSubdirectory("waypoint-compliance-runner-health-").FullName;

	public void Dispose()
	{
		try
		{
			Directory.Delete(_tempRoot, recursive: true);
		}
		catch (IOException)
		{
			// Best effort; the OS temp directory gets swept eventually regardless.
		}
	}

	[Fact]
	public async Task StartThenImmediateStop_WritesReportWithCapabilities()
	{
		string reportFile = Path.Combine(_tempRoot, "health.json");
		RunnerHealthReportingHostedService service = BuildService(reportFile, ready: true);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.True(File.Exists(reportFile));
		RunnerHealthReport report = JsonSerializer.Deserialize<RunnerHealthReport>(await File.ReadAllTextAsync(reportFile))!;

		Assert.True(report.Ready);
		Assert.Equal(
			new HashSet<string>(JobCapabilities.Compliance, StringComparer.Ordinal),
			new HashSet<string>(report.Capabilities, StringComparer.Ordinal));
	}

	[Fact]
	public async Task StartThenImmediateStop_WhenDependenciesMissing_WritesNotReadyReport()
	{
		string reportFile = Path.Combine(_tempRoot, "health.json");
		RunnerHealthReportingHostedService service = BuildService(reportFile, ready: false);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.True(File.Exists(reportFile));
		RunnerHealthReport report = JsonSerializer.Deserialize<RunnerHealthReport>(await File.ReadAllTextAsync(reportFile))!;

		Assert.False(report.Ready);
		Assert.NotEmpty(report.Problems);
	}

	[Fact]
	public async Task Report_IncludesResourceAdmissionCapacity()
	{
		// Issue #437: the health report must carry BuildCapacityReport()'s snapshot of the
		// runner's discovered/effective/admitted resource state, not just readiness +
		// capabilities. A ResourceAdmissionController pointed at a nonexistent cgroup root
		// falls back to its configured conservative defaults, which the capacity block must
		// then surface verbatim.
		string reportFile = Path.Combine(_tempRoot, "health.json");
		RunnerHealthReportingHostedService service = BuildService(
			reportFile,
			ready: true,
			fallbackCpuCores: 3.0,
			fallbackMemoryBytes: 6L * 1024 * 1024 * 1024);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		RunnerHealthReport report = JsonSerializer.Deserialize<RunnerHealthReport>(await File.ReadAllTextAsync(reportFile))!;

		Assert.NotNull(report.Capacity);
		Assert.Equal(HostResourceLimitSource.Fallback.ToString(), report.Capacity!.Source);
		Assert.True(report.Capacity.IsFallback);
		Assert.Equal(3.0, report.Capacity.DiscoveredCpuCores, precision: 6);
		Assert.Equal(6L * 1024 * 1024 * 1024, report.Capacity.DiscoveredMemoryBytes);
		Assert.Equal(3.0, report.Capacity.EffectiveCpuCores, precision: 6);
		Assert.Equal(6L * 1024 * 1024 * 1024, report.Capacity.EffectiveMemoryBytes);
		// Nothing has been admitted, so the running-job commitments are all zero.
		Assert.Equal(0.0, report.Capacity.AdmittedCpuCores, precision: 6);
		Assert.Equal(0L, report.Capacity.AdmittedMemoryBytes);
		Assert.Equal(0, report.Capacity.AdmittedJobCount);
	}

	[Fact]
	public async Task UnwritableReportPath_DoesNotThrowAndWritesNoFile()
	{
		// The report file's parent path is a *file*, not a directory, so both
		// Directory.CreateDirectory and the write-then-move fail with an IOException. The
		// service must swallow that (log-and-continue) rather than crash the runner over a
		// health side channel -- and no report file is produced.
		string blockingFile = Path.Combine(_tempRoot, "not-a-directory");
		await File.WriteAllTextAsync(blockingFile, "occupied");
		string reportFile = Path.Combine(blockingFile, "health.json");

		RunnerHealthReportingHostedService service = BuildService(reportFile, ready: true);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
		await service.StartAsync(cts.Token);
		await service.StopAsync(CancellationToken.None);

		Assert.False(File.Exists(reportFile));
	}

	private RunnerHealthReportingHostedService BuildService(
		string reportFile,
		bool ready,
		double fallbackCpuCores = 1.0,
		long fallbackMemoryBytes = 1024L * 1024 * 1024)
	{
		string modulesDir = Path.Combine(_tempRoot, "modules");
		string profileDir = Path.Combine(_tempRoot, "profiles", "vsphere");
		string nsxDir = Path.Combine(_tempRoot, "profiles", "nsx");
		string srgDir = Path.Combine(_tempRoot, "profiles", "srg");
		string artifactDir = Path.Combine(_tempRoot, "artifacts");

		Directory.CreateDirectory(modulesDir);

		if (ready)
		{
			Directory.CreateDirectory(profileDir);
			Directory.CreateDirectory(nsxDir);
			Directory.CreateDirectory(srgDir);
			Directory.CreateDirectory(artifactDir);
		}

		PowerShellOptions powerShellOptions = new();
		powerShellOptions.ModulePreloadPaths.Add(modulesDir);

		ScanOptions scanOptions = new()
		{
			ProfilePath = profileDir,
			NsxProfilePath = nsxDir,
			SrgProfilePath = srgDir,
			ArtifactStorePath = artifactDir,
		};

		ComplianceReadinessCheck readiness = new(
			Options.Create(powerShellOptions),
			Options.Create(scanOptions),
			new FakeMasterKeyProvider());

		JobHandlerRegistry registry = new([], JobCapabilities.Compliance);

		RunnerResourceOptions resourceOptions = new()
		{
			CgroupRoot = "/does/not/exist",
			FallbackCpuCores = fallbackCpuCores,
			FallbackMemoryBytes = fallbackMemoryBytes,
		};
		ResourceAdmissionController resourceAdmission = new(
			Options.Create(resourceOptions),
			new CgroupResourceDiscovery(Options.Create(resourceOptions), NullLogger<CgroupResourceDiscovery>.Instance),
			NullLogger<ResourceAdmissionController>.Instance);

		return new RunnerHealthReportingHostedService(
			readiness,
			registry,
			Options.Create(new RunnerHealthOptions
			{
				ReportFilePath = reportFile,
				RefreshInterval = TimeSpan.FromMinutes(5),
			}),
			resourceAdmission,
			NullLogger<RunnerHealthReportingHostedService>.Instance);
	}

	private sealed class FakeMasterKeyProvider : IMasterKeyProvider
	{
		public MasterKey GetKey() => new(new byte[32], "wpk-fake0000");
	}
}
