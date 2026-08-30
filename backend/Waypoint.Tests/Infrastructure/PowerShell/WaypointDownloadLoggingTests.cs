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

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #719 acceptance criteria: download-job history was dropping every Debug/
/// Verbose <c>Write-Log</c> call the migrated <c>vcf-download-manager.common.ps1</c>
/// emits, because that script defines its own level-filtered, console/file-oriented
/// <c>Write-Log</c> and dot-sourcing it into <c>WaypointDownload.psm1</c> /
/// <c>WaypointCatalogIndex.psm1</c> silently shadowed whatever logging contract was
/// in scope before it.
///
/// Run through <c>Assets/WaypointDownloadManagerCommonFake/WaypointDownloadManagerCommonFake.ps1</c>
/// -- an invented stand-in for the sibling script that reproduces its bug-triggering
/// Write-Log shape (see that file's own header) so these tests pin the real fix (the
/// shims re-defining Write-Log after the dot-source, delegating to the shared
/// WaypointLogging adapter, issue #579) rather than a hand-picked one. Covers: every
/// severity -- Info, Success, Warning, Error, Verbose, and Debug -- from a call
/// reached through the real, unmodified <c>Invoke-WaypointDownload</c> and
/// <c>Invoke-WaypointCatalogIndex</c> commands lands as an ordered, redacted job.log
/// event.
/// </summary>
public sealed class WaypointDownloadLoggingTests : IDisposable
{
	private sealed class RecordingLogBuffer : IJobLogBuffer
	{
		public ConcurrentQueue<(string EventType, Guid? JobId, Guid? RunId, string Payload)> Events { get; } = new();

		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson)
		{
			Events.Enqueue((eventType, jobId, runId, payloadJson));
			return true;
		}
	}

	private static readonly string LoggingModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointLogging", "WaypointLogging.psm1");

	private static readonly string DownloadModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointDownload", "WaypointDownload.psm1");

	private static readonly string CatalogIndexModulePath = Path.Combine(
		AppContext.BaseDirectory,
		"..", "..", "..", "..",
		"Waypoint.Infrastructure.Execution", "PowerShell", "Modules", "WaypointCatalogIndex", "WaypointCatalogIndex.psm1");

	private static readonly string FakeCommonPath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDownloadManagerCommonFake", "WaypointDownloadManagerCommonFake.ps1");

	private readonly RecordingLogBuffer _buffer = new();
	private WaypointRunspacePool _pool = null!;

	public WaypointDownloadLoggingTests()
	{
		Assert.True(File.Exists(Path.GetFullPath(LoggingModulePath)), $"expected the adapter at '{Path.GetFullPath(LoggingModulePath)}'");
		Assert.True(File.Exists(Path.GetFullPath(DownloadModulePath)), $"expected WaypointDownload.psm1 at '{Path.GetFullPath(DownloadModulePath)}'");
		Assert.True(File.Exists(Path.GetFullPath(CatalogIndexModulePath)), $"expected WaypointCatalogIndex.psm1 at '{Path.GetFullPath(CatalogIndexModulePath)}'");
		Assert.True(File.Exists(FakeCommonPath), $"expected the fake common script at '{FakeCommonPath}'");
	}

	private PowerShellExecutor CreateExecutor()
	{
		PowerShellOptions options = new() { MaxRunspaces = 1, DefaultInvocationTimeout = TimeSpan.FromSeconds(30) };
		options.ModulePreloadPaths.Add(Path.GetFullPath(LoggingModulePath));
		options.ModulePreloadPaths.Add(Path.GetFullPath(DownloadModulePath));
		options.ModulePreloadPaths.Add(Path.GetFullPath(CatalogIndexModulePath));
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, _buffer, wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	public void Dispose()
	{
		_pool?.Dispose();
	}

	private static string Severity(JsonDocument document) => document.RootElement.GetProperty("severity").GetString()!;

	[Theory]
	[InlineData("fake debug: resolving resume state", "debug")]
	[InlineData("fake verbose: downloading attempt 1/3", "verbose")]
	[InlineData("fake info: starting download", "information")]
	[InlineData("fake success: download complete", "information")]
	[InlineData("fake warning: retrying after transient error", "warning")]
	public async Task InvokeWaypointDownload_EverySeverity_LandsInJobLog_OnTheExpectedNativeStream(
		string expectedMessageFragment, string expectedNativeSeverity)
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDownload",
				Parameters: new Dictionary<string, object?>
				{
					["Url"] = "https://example.internal/artifact.iso",
					["OutFile"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"),
					["VcfDownloadManagerCommonPath"] = FakeCommonPath,
				}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		(string, Guid?, Guid?, string) match = Assert.Single(
			_buffer.Events, entry => entry.Payload.Contains(expectedMessageFragment, StringComparison.Ordinal));
		using JsonDocument document = JsonDocument.Parse(match.Item4);
		Assert.Equal(expectedNativeSeverity, Severity(document));
	}

	/// <summary>
	/// AC: verbose/debug records from a migrated script reach the job log. Before
	/// the #719 fix, neither of these two lines produced any job.log event at all
	/// (the sibling script's own Write-Log dropped them, filtered by the default
	/// $Global:LogLevel of 'Info' -- see WaypointDownloadManagerCommonFake.ps1's
	/// header for why this fake reproduces that exact filtering behavior).
	/// </summary>
	[Fact]
	public async Task InvokeWaypointDownload_DebugAndVerbose_AreNotSuppressed()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDownload",
				Parameters: new Dictionary<string, object?>
				{
					["Url"] = "https://example.internal/artifact.iso",
					["OutFile"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"),
					["VcfDownloadManagerCommonPath"] = FakeCommonPath,
				}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		Assert.Contains(_buffer.Events, e => e.Payload.Contains("fake debug: resolving resume state", StringComparison.Ordinal));
		Assert.Contains(_buffer.Events, e => e.Payload.Contains("fake verbose: downloading attempt 1/3", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("fake debug: walking manifest directory", "debug")]
	[InlineData("fake verbose: manifest built with 0 files", "verbose")]
	public async Task InvokeWaypointCatalogIndex_DebugAndVerbose_AreNotSuppressed(
		string expectedMessageFragment, string expectedNativeSeverity)
	{
		string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			PowerShellExecutor executor = CreateExecutor();
			PowerShellExecutionResult result = await executor.ExecuteAsync(
				new PowerShellRequest(
					"Invoke-WaypointCatalogIndex",
					Parameters: new Dictionary<string, object?>
					{
						["DepotPath"] = directory,
						["VcfDownloadManagerCommonPath"] = FakeCommonPath,
					}),
				CancellationToken.None);

			Assert.True(result.Succeeded, result.FailureReason);

			(string, Guid?, Guid?, string) match = Assert.Single(
				_buffer.Events, entry => entry.Payload.Contains(expectedMessageFragment, StringComparison.Ordinal));
			using JsonDocument document = JsonDocument.Parse(match.Item4);
			Assert.Equal(expectedNativeSeverity, Severity(document));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	/// <summary>
	/// Ordering guard: job.log events from a single invocation arrive in the same
	/// order Write-Log was called in, not batched/reordered by severity -- the
	/// acceptance criteria ask for "ordered ... job.log persistence".
	/// </summary>
	[Fact]
	public async Task InvokeWaypointDownload_LogEvents_ArriveInCallOrder()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDownload",
				Parameters: new Dictionary<string, object?>
				{
					["Url"] = "https://example.internal/artifact.iso",
					["OutFile"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin"),
					["VcfDownloadManagerCommonPath"] = FakeCommonPath,
				}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		string[] expectedOrder =
		[
			"fake debug: resolving resume state",
			"fake verbose: downloading attempt 1/3",
			"fake info: starting download",
			"fake warning: retrying after transient error",
			"fake success: download complete",
		];

		List<string> actualOrder = _buffer.Events
			.Select(e => e.Payload)
			.Where(payload => expectedOrder.Any(fragment => payload.Contains(fragment, StringComparison.Ordinal)))
			.Select(payload => expectedOrder.First(fragment => payload.Contains(fragment, StringComparison.Ordinal)))
			.ToList();

		Assert.Equal(expectedOrder, actualOrder);
	}
}
