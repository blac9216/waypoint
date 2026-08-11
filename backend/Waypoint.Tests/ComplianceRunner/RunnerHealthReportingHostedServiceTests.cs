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

	private RunnerHealthReportingHostedService BuildService(string reportFile, bool ready)
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

		return new RunnerHealthReportingHostedService(
			readiness,
			registry,
			Options.Create(new RunnerHealthOptions
			{
				ReportFilePath = reportFile,
				RefreshInterval = TimeSpan.FromMinutes(5),
			}),
			NullLogger<RunnerHealthReportingHostedService>.Instance);
	}

	private sealed class FakeMasterKeyProvider : IMasterKeyProvider
	{
		public MasterKey GetKey() => new(new byte[32], "wpk-fake0000");
	}
}
