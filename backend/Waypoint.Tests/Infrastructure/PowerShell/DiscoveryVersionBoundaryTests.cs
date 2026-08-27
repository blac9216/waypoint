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
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #974 PS-boundary survival proof: <see cref="Waypoint.Infrastructure.Discovery.DiscoverJobHandler"/>'s
/// new <c>Version</c> read crosses the same PowerShell-&gt;C# pipeline boundary
/// PR #975's <c>PowerShellValueUnwrap</c> chokepoint was introduced for (a nested
/// property inside an array element arriving still wrapped in an extra
/// <see cref="System.Management.Automation.PSObject"/> layer, reading back as null
/// through a plain <c>as string</c> cast). This drives the REAL executor (real
/// in-process SMA runspace, real <c>Invoke-WaypointDiscovery</c> stub module output --
/// not a hand-built PSObject literal) to prove the new field survives non-null at
/// exactly the read <c>DiscoverJobHandler.TryParseItem</c> performs
/// (<c>psObject.Properties["Version"]?.Value as string</c>).
///
/// Unlike PR #975's RawYaml case, Version is a TOP-LEVEL property of the item object
/// (not nested inside a further array element) -- <see cref="PowerShellExecutor"/>
/// already unwraps the outer PSObject layer on every top-level pipeline output object
/// before this handler ever inspects it (see <c>PowerShellExecutor.Unwrap</c>), so this
/// test is expected to pass whether or not #975 has merged; it exists to prove that
/// fact for THIS field rather than assume it by analogy, and to catch a regression if
/// the module's output shape ever nests Version one layer deeper.
/// </summary>
public sealed class DiscoveryVersionBoundaryTests : IDisposable
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

	private static readonly string StubModulePath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointDiscoveryStubModule", "WaypointDiscoveryStubModule.psm1");

	private WaypointRunspacePool _pool = null!;

	private PowerShellExecutor CreateExecutor()
	{
		PowerShellOptions options = new() { MaxRunspaces = 1 };
		options.ModulePreloadPaths.Add(StubModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, new RecordingLogBuffer(), wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	public void Dispose()
	{
		_pool?.Dispose();
	}

	[Fact]
	public async Task HostRow_VersionProperty_SurvivesTheRealExecutorBoundary_NonNull_AndDistinctFromBuild()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_DISCOVERY_STUB_PASS", "1");
		PowerShellExecutor executor = CreateExecutor();

		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDiscovery",
				Parameters: new Dictionary<string, object?>
				{
					["VCenter"] = "vcsa-01.example.internal",
					["Username"] = "administrator@example.internal",
					["Password"] = "invented-boundary-test-password",
				}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);

		System.Management.Automation.PSObject hostRow = System.Management.Automation.PSObject.AsPSObject(
			result.Output.Single(o =>
				System.Management.Automation.PSObject.AsPSObject(o!).Properties["MoRef"]?.Value as string == "host-11")!);

		// This is exactly the read DiscoverJobHandler.TryParseItem/GetProperty<string>
		// performs post-fix. Pre-#974 there was no Version property at all (this
		// assertion would fail with a null Value); the fixture's invented value proves
		// the module->handler contract carries a real, non-null, correctly-typed string
		// distinct from Build across the boundary.
		object? rawVersionValue = hostRow.Properties["Version"]?.Value;
		string? version = rawVersionValue as string;

		Assert.True(
			version is not null,
			$"Version did not surface as a string at the TryParseItem-equivalent read; " +
			$"raw property value runtime type was '{rawVersionValue?.GetType().FullName ?? "null"}'.");
		Assert.Equal("8.0.3", version); // Invented fixture value (WaypointDiscoveryStubModule.psm1) -- matches migration 0064's real seeded row, never a lab-observed build/version.

		string? build = hostRow.Properties["Build"]?.Value as string;
		Assert.NotNull(build);
		Assert.NotEqual(build, version);
	}
}
