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

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #580 regression coverage: inventory discovery (<c>Invoke-WaypointDiscovery</c>)
/// and vSphere API credential testing (<c>Invoke-WaypointVCenterCredentialTest</c>) must
/// connect using only the supplied vSphere API credential and must NEVER invoke
/// <c>Get-Credential</c> -- the noninteractive compliance-runner host can never satisfy
/// that prompt. Runs the REAL <c>WaypointDiscovery.psm1</c> / <c>WaypointCredentialTest.psm1</c>
/// wrappers (only their doc comments/parameterization are Waypoint-owned; the connection
/// logic they dot-source is the shared transport) against
/// <c>Assets/WaypointVsphereTransportFake/WaypointVsphereTransportFake.ps1</c>, an invented
/// stand-in for the sibling repo's <c>module.transport.vmware.ps1</c> that mirrors the real
/// <c>Connect-StigVIServer</c>'s <c>-SkipVCSACredential</c> contract (the #580 fix) closely
/// enough to pin the wrapper-level behavior: a fake <c>Get-Credential</c> that always throws
/// catches any regression that lets either wrapper fall into the prompt fallback, and a fake
/// <c>Connect-VIServer</c> that requires a non-empty username pins "missing vSphere API
/// credential fails fast with an actionable error", not a hang.
/// </summary>
public sealed class VsphereApiOnlyNoninteractiveTests : IDisposable
{
	private static readonly string WrapperModulesDirectory = Path.Combine(
		AppContext.BaseDirectory, "..", "..", "..", "..", "Waypoint.Infrastructure.Execution", "PowerShell", "Modules");

	private static readonly string DiscoveryModulePath = Path.GetFullPath(
		Path.Combine(WrapperModulesDirectory, "WaypointDiscovery", "WaypointDiscovery.psm1"));

	private static readonly string CredentialTestModulePath = Path.GetFullPath(
		Path.Combine(WrapperModulesDirectory, "WaypointCredentialTest", "WaypointCredentialTest.psm1"));

	private static readonly string FakeTransportPath = Path.Combine(
		AppContext.BaseDirectory, "Assets", "WaypointVsphereTransportFake", "WaypointVsphereTransportFake.ps1");

	private readonly string? _originalTransportPathEnv =
		Environment.GetEnvironmentVariable("WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH");

	private WaypointRunspacePool? _pool;

	public VsphereApiOnlyNoninteractiveTests()
	{
		Assert.True(File.Exists(DiscoveryModulePath), $"expected the real wrapper at '{DiscoveryModulePath}'");
		Assert.True(File.Exists(CredentialTestModulePath), $"expected the real wrapper at '{CredentialTestModulePath}'");
		Assert.True(File.Exists(FakeTransportPath), $"expected the fake transport at '{FakeTransportPath}'");

		// The wrappers read $env:WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH as their
		// -VmwareStigDockerTransportPath default -- pointing it at the fake for the
		// scope of these tests is how the real dot-source line picks it up unmodified.
		Environment.SetEnvironmentVariable("WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH", FakeTransportPath);
	}

	public void Dispose()
	{
		Environment.SetEnvironmentVariable("WAYPOINT_VMWARE_STIG_DOCKER_TRANSPORT_PATH", _originalTransportPathEnv);
		_pool?.Dispose();
	}

	private PowerShellExecutor CreateExecutor()
	{
		PowerShellOptions options = new() { MaxRunspaces = 1, DefaultInvocationTimeout = TimeSpan.FromSeconds(30) };
		options.ModulePreloadPaths.Add(DiscoveryModulePath);
		options.ModulePreloadPaths.Add(CredentialTestModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, new NullLogBuffer(), wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	private sealed class NullLogBuffer : Waypoint.Core.Jobs.IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	[Fact]
	public async Task Discovery_WithAnApiCredential_SucceedsWithoutPrompting()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDiscovery",
				Parameters: new Dictionary<string, object?>
				{
					["VCenter"] = "vcsa-01.example.internal",
					["Username"] = "administrator@example.internal",
					["Password"] = "invented-test-password",
				}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);
	}

	[Fact]
	public async Task CredentialTest_WithAnApiCredential_SucceedsWithoutPrompting()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointVCenterCredentialTest",
				Parameters: new Dictionary<string, object?>
				{
					["VCenter"] = "vcsa-01.example.internal",
					["Username"] = "administrator@example.internal",
					["Password"] = "invented-test-password",
				}),
			CancellationToken.None);

		Assert.True(result.Succeeded, result.FailureReason);
		System.Management.Automation.PSObject output = System.Management.Automation.PSObject.AsPSObject(result.Output[0]!);
		Assert.True(output.Properties["Success"].Value is true);
		Assert.Null(output.Properties["FailureReason"].Value);
	}

	/// <summary>
	/// A successful API connection must mark the vSphere credential test successful
	/// regardless of whether any VCSA SSH binding exists -- neither wrapper resolves
	/// or supplies a VCSA credential at all, so there is nothing to bind; this pins
	/// that the fake transport's -SkipVCSACredential path never requires one.
	/// </summary>
	[Fact]
	public async Task CredentialTest_NeverRequestsOrEvaluatesAVcsaCredential()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointVCenterCredentialTest",
				Parameters: new Dictionary<string, object?>
				{
					["VCenter"] = "vcsa-02.example.internal",
					["Username"] = "administrator@example.internal",
					["Password"] = "invented-test-password",
				}),
			CancellationToken.None);

		// The fake's Get-Credential always throws; success here already proves no
		// VCSA-credential prompt fired. Asserting Succeeded again documents intent.
		Assert.True(result.Succeeded, result.FailureReason);
	}

	[Fact]
	public async Task Discovery_WithAMissingApiCredential_FailsFastWithAnActionableError_NeverPrompting()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointDiscovery",
				Parameters: new Dictionary<string, object?>
				{
					["VCenter"] = "vcsa-01.example.internal",
					["Username"] = string.Empty,
					["Password"] = "invented-test-password",
				}),
			CancellationToken.None);

		// ValidateNotNullOrEmpty on -Username rejects the call before the function
		// body (and therefore before any connect attempt) ever runs -- this is the
		// "fail fast, precise, noninteractive" contract, not a hang or a prompt.
		Assert.False(result.Succeeded);
		Assert.NotNull(result.FailureReason);
		Assert.DoesNotContain("Get-Credential", result.FailureReason, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CredentialTest_WithAMissingApiCredential_FailsFastWithAnActionableError_NeverPrompting()
	{
		PowerShellExecutor executor = CreateExecutor();
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				"Invoke-WaypointVCenterCredentialTest",
				Parameters: new Dictionary<string, object?>
				{
					["VCenter"] = "vcsa-01.example.internal",
					["Username"] = string.Empty,
					["Password"] = "invented-test-password",
				}),
			CancellationToken.None);

		Assert.False(result.Succeeded);
		Assert.NotNull(result.FailureReason);
		Assert.DoesNotContain("Get-Credential", result.FailureReason, StringComparison.Ordinal);
	}
}
