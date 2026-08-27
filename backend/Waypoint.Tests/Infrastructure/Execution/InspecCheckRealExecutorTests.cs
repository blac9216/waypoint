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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Xunit;
using RealPowerShellExecutor = Waypoint.Infrastructure.PowerShell.PowerShellExecutor;
using RealWaypointRunspacePool = Waypoint.Infrastructure.PowerShell.WaypointRunspacePool;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #984 (live-proven, epic #726 round 3): <c>Test-WaypointInspecCheck</c>'s
/// original bound used <c>Start-Job</c>/<c>Wait-Job</c>, which depends on PowerShell's
/// background-job subsystem -- only wired up in a full <c>pwsh</c> host, never in the
/// compliance-runner's embedded SMA runspace (<see cref="RealWaypointRunspacePool"/>,
/// <c>InitialSessionState.CreateDefault2()</c>, ADR-0013/0014). <c>Wait-Job</c> never
/// observed the job completing there, so EVERY invocation hit the 60s bound and every
/// valid profile was fail-closed quarantined (net promoted content = 0).
///
/// These tests drive <c>Test-WaypointInspecCheck</c> through the REAL
/// <see cref="RealPowerShellExecutor"/>/<see cref="RealWaypointRunspacePool"/> (the exact
/// in-process SMA host that broke on main -- PR #975's real-executor pattern), against an
/// invented stub "inspec" executable (a throwaway shell script, faithful only to the
/// `inspec check &lt;path&gt; --format json` argument contract and exit-code convention,
/// never real InSpec/cinc-auditor bytes, matching docs/testing.md's CI-stub discipline).
///
/// Pre-fix (Start-Job/Wait-Job), <see cref="FastCheck_CompletesBeforeTimeout_ReportsRanAndPassed"/>
/// fails: the fast stub still gets fail-closed "did not complete within Ns" because
/// Wait-Job never observes completion in this host. That is the exact defect this issue
/// closes -- captured as the negative proof in the PR body (revert the psm1 change, rerun,
/// watch this test fail the same way).
///
/// <see cref="Waypoint.Tests.Infrastructure.Execution.InspecCheckPathMutationCollection"/>
/// serializes this class against <c>ContentPullJobHandlerTests</c>' own real-executor
/// end-to-end test -- both mutate the process-wide <c>PATH</c> environment variable
/// (the only way to make <c>Get-Command inspec</c> resolve to an invented stub instead of
/// this dev container's real cinc-auditor install), which is unsafe under xUnit's default
/// class-level parallelism without a shared collection.
/// </summary>
[Collection("InspecCheckPathMutation")]
public sealed class InspecCheckRealExecutorTests : IDisposable
{
	private readonly List<string> _tempDirs = [];

	[Fact]
	public async Task FastCheck_CompletesBeforeTimeout_ReportsRanAndPassed()
	{
		string stubInspec = WriteStubInspec(exitCode: 0, sleepSeconds: 0);
		string profileDir = WriteProfileDirectory();

		PSObjectResult result = await InvokeTestWaypointInspecCheckAsync(stubInspec, profileDir, timeoutSeconds: 5);

		Assert.True(result.Ran, "expected Ran=true for a stub that exits immediately");
		Assert.True(result.Passed, $"expected Passed=true for a zero-exit-code stub; Detail was: {result.Detail}");
		Assert.DoesNotContain("did not complete within", result.Detail ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task SlowCheck_ExceedsBound_QuarantinesWithHonestTimeoutReason()
	{
		string stubInspec = WriteStubInspec(exitCode: 0, sleepSeconds: 30);
		string profileDir = WriteProfileDirectory();

		PSObjectResult result = await InvokeTestWaypointInspecCheckAsync(stubInspec, profileDir, timeoutSeconds: 2);

		Assert.True(result.Ran, "a genuine timeout is still a completed (fail-closed) check, not an 'inspec missing' case");
		Assert.False(result.Passed);
		Assert.Contains("did not complete within 2s", result.Detail ?? string.Empty, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FailingCheck_NonZeroExit_PassesThroughAsItsOwnQuarantineReason()
	{
		string stubInspec = WriteStubInspec(exitCode: 1, sleepSeconds: 0, stderr: "invented: profile.yml is not valid");
		string profileDir = WriteProfileDirectory();

		PSObjectResult result = await InvokeTestWaypointInspecCheckAsync(stubInspec, profileDir, timeoutSeconds: 5);

		Assert.True(result.Ran);
		Assert.False(result.Passed);
		Assert.Contains("invented: profile.yml is not valid", result.Detail ?? string.Empty, StringComparison.Ordinal);
	}

	private sealed record PSObjectResult(bool Ran, bool Passed, string? Detail);

	/// <summary>
	/// Builds a real in-process SMA runspace pool (PR #975's pattern) with the production
	/// <c>WaypointComplianceContent.psm1</c> preloaded, prepends the stub "inspec"
	/// directory onto PATH for the duration of the call so <c>Get-Command inspec</c>
	/// resolves to the stub, and invokes <c>Test-WaypointInspecCheck</c> as a real
	/// <see cref="PowerShellRequest"/> through <see cref="RealPowerShellExecutor.ExecuteAsync"/>.
	/// </summary>
	private static async Task<PSObjectResult> InvokeTestWaypointInspecCheckAsync(string stubInspecPath, string profileDirectory, int timeoutSeconds)
	{
		string realModulePath = Path.Combine(
			AppContext.BaseDirectory, "PowerShell", "Modules", "WaypointComplianceContent", "WaypointComplianceContent.psm1");
		Assert.True(File.Exists(realModulePath), $"real module not found at '{realModulePath}' -- Waypoint.Infrastructure.Execution's PowerShell\\Modules content did not copy into the test output.");

		PowerShellOptions options = new()
		{
			MaxRunspaces = 1,
			DefaultInvocationTimeout = TimeSpan.FromSeconds(timeoutSeconds + 30),
			StopGracePeriod = TimeSpan.FromSeconds(2),
		};
		options.ModulePreloadPaths.Add(realModulePath);
		IOptions<PowerShellOptions> wrappedOptions = Options.Create(options);

		string stubDirectory = Path.GetDirectoryName(stubInspecPath)!;
		string originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		Environment.SetEnvironmentVariable("PATH", stubDirectory + Path.PathSeparator + originalPath);
		try
		{
			using RealWaypointRunspacePool pool = new(wrappedOptions, NullLogger<RealWaypointRunspacePool>.Instance);
			NoopJobLogBuffer logBuffer = new();
			RealPowerShellExecutor executor = new(pool, logBuffer, wrappedOptions, NullLogger<RealPowerShellExecutor>.Instance);

			PowerShellRequest request = new(
				JobId: Guid.NewGuid(),
				RunId: Guid.NewGuid(),
				Kind: PowerShellRequestKind.Command,
				Command: "Test-WaypointInspecCheck",
				Parameters: new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["ProfileDirectory"] = profileDirectory,
					["TimeoutSeconds"] = timeoutSeconds,
				},
				Timeout: TimeSpan.FromSeconds(timeoutSeconds + 30));

			PowerShellExecutionResult executionResult = await executor.ExecuteAsync(request, CancellationToken.None);

			Assert.True(executionResult.Succeeded, $"executor invocation itself failed: {executionResult.FailureReason}");
			object single = Assert.Single(executionResult.Output)!;

			bool ran = (bool)GetProperty(single, "Ran")!;
			bool passed = (bool)GetProperty(single, "Passed")!;
			string? detail = (string?)GetProperty(single, "Detail");

			return new PSObjectResult(ran, passed, detail);
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", originalPath);
		}
	}

	/// <summary>
	/// <see cref="PowerShellValueUnwrap"/> unwraps a [pscustomobject]'s note properties
	/// down to plain CLR values before they reach here (same rule the real executor
	/// applies at every nested layer), so the returned object is expected to be an
	/// <see cref="IDictionary{TKey,TValue}"/>-shaped structure OR still a raw PSObject
	/// depending on unwrap depth; both are handled defensively via reflection so this
	/// test does not need to depend on System.Management.Automation's PSObject type
	/// directly for a value that has already crossed the executor boundary.
	/// </summary>
	private static object? GetProperty(object obj, string name)
	{
		if (obj is System.Collections.IDictionary dict && dict.Contains(name))
		{
			return dict[name];
		}

		System.Reflection.PropertyInfo? property = obj.GetType().GetProperty(name);
		if (property is not null)
		{
			return property.GetValue(obj);
		}

		if (obj is System.Management.Automation.PSObject psObject)
		{
			return psObject.Properties[name]?.Value;
		}

		throw new InvalidOperationException($"could not read property '{name}' off a value of type {obj.GetType()}");
	}

	private string WriteProfileDirectory()
	{
		string dir = Directory.CreateTempSubdirectory("wp-984-inspec-profile").FullName;
		_tempDirs.Add(dir);
		Directory.CreateDirectory(Path.Combine(dir, "controls"));
		File.WriteAllText(Path.Combine(dir, "inspec.yml"), "name: invented-fixture-profile\ntitle: Invented fixture\n");
		return dir;
	}

	/// <summary>
	/// Writes a throwaway shell script named "inspec" that mirrors `inspec check`'s
	/// argument contract (accepts `check &lt;path&gt; --format json`) and exit-code
	/// convention, faithful to the CLI grammar only -- never real InSpec/cinc-auditor
	/// bytes or output (docs/testing.md "CI stubs vs live-lab validation" discipline,
	/// same convention this repo already applies to the licensed VCFDT tool).
	/// </summary>
	private string WriteStubInspec(int exitCode, int sleepSeconds, string? stderr = null)
	{
		Assert.True(
			RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
			"this stub is a POSIX shell script -- the compliance-runner image and this repo's CI are both Linux.");

		string dir = Directory.CreateTempSubdirectory("wp-984-inspec-stub").FullName;
		_tempDirs.Add(dir);
		string stubPath = Path.Combine(dir, "inspec");

		string stderrLine = stderr is null ? string.Empty : $"echo '{stderr}' 1>&2\n";
		string script = "#!/bin/sh\n"
			+ "# Invented stub for issue #984's real-executor bounded-check tests -- mirrors\n"
			+ "# `inspec check <path> --format json`'s argument contract only, never real\n"
			+ "# InSpec/cinc-auditor bytes.\n"
			+ $"sleep {sleepSeconds}\n"
			+ stderrLine
			+ "echo '{\"controls\": []}'\n"
			+ $"exit {exitCode}\n";
		File.WriteAllText(stubPath, script.ReplaceLineEndings("\n"));

#pragma warning disable CA1416 // Linux-only test asset; platform-gated above.
		File.SetUnixFileMode(
			stubPath,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
				| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

		return stubPath;
	}

	/// <summary>Records job.log events without a real IJobLogBuffer backend -- these tests only need the real executor to run, not to inspect its stream output.</summary>
	private sealed class NoopJobLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	public void Dispose()
	{
		foreach (string dir in _tempDirs)
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (Exception)
			{
				// Best-effort cleanup only.
			}
		}
	}
}

/// <summary>
/// Binds every test that mutates the process-wide <c>PATH</c> environment variable to
/// resolve <c>Get-Command inspec</c> onto an invented stub -- serializes them under
/// xUnit's default class-level parallelism (issue #984: two such tests racing on PATH
/// intermittently made one see the other's stub, or this dev container's real
/// cinc-auditor install, instead of its own).
/// </summary>
[CollectionDefinition("InspecCheckPathMutation")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public sealed class InspecCheckPathMutationCollection
#pragma warning restore CA1711
{
}
