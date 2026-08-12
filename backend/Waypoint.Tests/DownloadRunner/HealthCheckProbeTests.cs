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
using Waypoint.DownloadRunner;
using Xunit;

namespace Waypoint.Tests.DownloadRunner;

/// <summary>
/// The download-runner's file-based container health probe (issue #441) -- no HTTP
/// listener exists in this process (ADR-0013 §1), so <c>--health-check</c> reads the
/// readiness marker <see cref="ReadinessReportingHostedService"/> writes instead of
/// probing loopback like <c>Waypoint.Api.Diagnostics.HealthCheckProbe</c> does.
/// </summary>
public sealed class HealthCheckProbeTests : IDisposable
{
	private readonly string _tempDirectory = Directory.CreateTempSubdirectory("waypoint-download-runner-health-test-").FullName;
	private readonly TimeSpan _maxAge = TimeSpan.FromSeconds(60);

	private string ReadinessFilePath => Path.Combine(_tempDirectory, "readiness.json");

	public void Dispose()
	{
		if (Directory.Exists(_tempDirectory))
		{
			Directory.Delete(_tempDirectory, recursive: true);
		}
	}

	[Theory]
	[InlineData("--health-check", true)]
	[InlineData("--HEALTH-CHECK", true)]
	[InlineData("--something-else", false)]
	public void IsHealthCheckInvocation_MatchesCaseInsensitively(string argument, bool expected)
	{
		Assert.Equal(expected, HealthCheckProbe.IsHealthCheckInvocation([argument]));
	}

	[Fact]
	public void IsHealthCheckInvocation_EmptyArgs_IsFalse()
	{
		Assert.False(HealthCheckProbe.IsHealthCheckInvocation([]));
	}

	[Fact]
	public async Task MissingFile_IsUnhealthy()
	{
		int exitCode = await HealthCheckProbe.RunAsync(ReadinessFilePath, _maxAge, DateTimeOffset.UtcNow);
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public async Task UnparseableFile_IsUnhealthy()
	{
		await File.WriteAllTextAsync(ReadinessFilePath, "not json");
		int exitCode = await HealthCheckProbe.RunAsync(ReadinessFilePath, _maxAge, DateTimeOffset.UtcNow);
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public async Task ReadyAndFresh_IsHealthy()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		ReadinessSnapshot snapshot = new(
			Ready: true, JobTypes: ["catalog-index", "download"], ToolPresent: false,
			ArtifactStoreWritable: true, DepotPathReadable: true, DatabaseReachable: true, Capacity: null, GeneratedAt: now);
		await File.WriteAllTextAsync(ReadinessFilePath, JsonSerializer.Serialize(snapshot));

		int exitCode = await HealthCheckProbe.RunAsync(ReadinessFilePath, _maxAge, now.AddSeconds(1));
		Assert.Equal(0, exitCode);
	}

	[Fact]
	public async Task NotReady_IsUnhealthyEvenWhenFresh()
	{
		DateTimeOffset now = DateTimeOffset.UtcNow;
		ReadinessSnapshot snapshot = new(
			Ready: false, JobTypes: ["catalog-index", "download"], ToolPresent: false,
			ArtifactStoreWritable: false, DepotPathReadable: true, DatabaseReachable: true, Capacity: null, GeneratedAt: now);
		await File.WriteAllTextAsync(ReadinessFilePath, JsonSerializer.Serialize(snapshot));

		int exitCode = await HealthCheckProbe.RunAsync(ReadinessFilePath, _maxAge, now);
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public async Task StaleSnapshot_IsUnhealthyEvenWhenReady()
	{
		DateTimeOffset generatedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		ReadinessSnapshot snapshot = new(
			Ready: true, JobTypes: ["catalog-index", "download"], ToolPresent: true,
			ArtifactStoreWritable: true, DepotPathReadable: true, DatabaseReachable: true, Capacity: null, GeneratedAt: generatedAt);
		await File.WriteAllTextAsync(ReadinessFilePath, JsonSerializer.Serialize(snapshot));

		int exitCode = await HealthCheckProbe.RunAsync(ReadinessFilePath, _maxAge, generatedAt.Add(_maxAge).AddSeconds(1));
		Assert.Equal(1, exitCode);
	}

	[Fact]
	public async Task ToolAbsent_DoesNotByItselfMakeTheProbeUnhealthy()
	{
		// ADR-0015: the managed tool is optional operator-installed state -- readiness
		// (and therefore container health) must stay true so catalog-index traffic is
		// not blocked by a tool that simply has not been installed yet.
		DateTimeOffset now = DateTimeOffset.UtcNow;
		ReadinessSnapshot snapshot = new(
			Ready: true, JobTypes: ["catalog-index", "download"], ToolPresent: false,
			ArtifactStoreWritable: true, DepotPathReadable: true, DatabaseReachable: true, Capacity: null, GeneratedAt: now);
		await File.WriteAllTextAsync(ReadinessFilePath, JsonSerializer.Serialize(snapshot));

		int exitCode = await HealthCheckProbe.RunAsync(ReadinessFilePath, _maxAge, now);
		Assert.Equal(0, exitCode);
	}
}
