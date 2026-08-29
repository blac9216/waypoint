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

using System.Linq;
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

	private static readonly string LoggingModulePath = Path.GetFullPath(
		Path.Combine(WrapperModulesDirectory, "WaypointLogging", "WaypointLogging.psm1"));

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
		Assert.True(File.Exists(LoggingModulePath), $"expected the shared logging adapter at '{LoggingModulePath}'");
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
		return CreateExecutor(out _);
	}

	private PowerShellExecutor CreateExecutor(out RecordingLogBuffer logBuffer)
	{
		PowerShellOptions options = new() { MaxRunspaces = 1, DefaultInvocationTimeout = TimeSpan.FromSeconds(30) };
		options.ModulePreloadPaths.Add(LoggingModulePath);
		options.ModulePreloadPaths.Add(DiscoveryModulePath);
		options.ModulePreloadPaths.Add(CredentialTestModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		logBuffer = new RecordingLogBuffer();
		return new PowerShellExecutor(_pool, logBuffer, wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	/// <summary>Records every enqueued job.log payload so tests can assert on captured content.</summary>
	private sealed class RecordingLogBuffer : Waypoint.Core.Jobs.IJobLogBuffer
	{
		public List<string> Payloads { get; } = [];

		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson)
		{
			Payloads.Add(payloadJson);
			return true;
		}
	}

	/// <summary>
	/// Issue #579: WaypointDiscovery.psm1 dot-sources the imported transport, which
	/// calls Get-LogSplat/Write-Log (see the fake transport's Connect-StigVIServer).
	/// Before the shared WaypointLogging adapter existed, discovery had neither
	/// function preloaded, and every one of those calls threw a missing-command
	/// error -- this pins that discovery now succeeds AND that the Write-Log calls
	/// actually reach job.log (not merely that they fail to throw).
	/// </summary>
	[Fact]
	public async Task Discovery_WithAnApiCredential_SucceedsWithoutPrompting()
	{
		PowerShellExecutor executor = CreateExecutor(out RecordingLogBuffer logBuffer);
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
		Assert.DoesNotContain(
			logBuffer.Payloads, payload => payload.Contains("is not recognized", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(
			logBuffer.Payloads,
			payload => payload.Contains("Connecting to vCenter", StringComparison.Ordinal));
		Assert.Contains(
			logBuffer.Payloads,
			payload => payload.Contains("Successfully connected to", StringComparison.Ordinal));
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
	/// <summary>
	/// Runs the REAL <c>Invoke-WaypointDiscovery</c> with the fake transport's session
	/// list overridden for the duration of this one pipeline, and returns the emitted
	/// item objects. Issue #1081's emission guard can only be pinned against a session
	/// shape a real appliance can produce, and the transport fake is where that shape
	/// is decided.
	/// </summary>
	private static async Task<(bool Succeeded, string? FailureReason, IReadOnlyList<System.Management.Automation.PSObject> Items)>
		RunDiscoveryWithSessionsAsync(PowerShellExecutor executor, string vCenter, string sessionsExpression)
	{
		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				$$"""
				$Global:WaypointVsphereTransportFakeSessions = {{sessionsExpression}}
				$Global:WaypointVsphereTransportFakeClusters = @([pscustomobject]@{
					Name = 'cluster-alpha'
					ExtensionData = [pscustomobject]@{ MoRef = [pscustomobject]@{ Value = 'domain-c101' } }
				})
				try {
					Invoke-WaypointDiscovery -VCenter '{{vCenter}}' -Username 'administrator@example.internal' -Password 'invented-test-password'
				} finally {
					$Global:WaypointVsphereTransportFakeSessions = $null
					$Global:WaypointVsphereTransportFakeClusters = $null
				}
				""",
				PowerShellRequestKind.Script),
			CancellationToken.None);

		List<System.Management.Automation.PSObject> items = result.Output
			.Where(o => o is not null)
			.Select(o => System.Management.Automation.PSObject.AsPSObject(o!))
			.ToList();
		return (result.Succeeded, result.FailureReason, items);
	}

	private static List<System.Management.Automation.PSObject> OfType(
		IReadOnlyList<System.Management.Automation.PSObject> items, string type)
		=> items.Where(i => string.Equals(i.Properties["Type"]?.Value as string, type, StringComparison.Ordinal)).ToList();

	/// <summary>
	/// Issue #1081 (round-1 review, major 3). The happy path on the SHIPPED module:
	/// a session that supplies a real instanceUuid produces exactly one 'vcenter' row
	/// carrying it, alongside the ordinary inventory walk. This is the control the two
	/// absence tests below are read against -- without it, "no vcenter row" would
	/// prove nothing, because a fixture that never emits one looks identical.
	/// </summary>
	[Fact]
	public async Task Discovery_WhenTheSessionSuppliesAnInstanceUuid_EmitsExactlyOneIdentifiedVcenterRow()
	{
		PowerShellExecutor executor = CreateExecutor();
		var run = await RunDiscoveryWithSessionsAsync(
			executor,
			"vcsa-01.example.internal",
			"""
			@([pscustomobject]@{ Name = 'vcsa-01.example.internal'; InstanceUuid = 'vcenter-instance-aaaa-0001'; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0001' })
			""");

		Assert.True(run.Succeeded, run.FailureReason);
		System.Management.Automation.PSObject vcenter = Assert.Single(OfType(run.Items, "vcenter"));
		Assert.Equal("vcenter-instance-aaaa-0001", vcenter.Properties["MoRef"]?.Value as string);
		Assert.Equal("vcsa-01.example.internal", vcenter.Properties["Name"]?.Value as string);
		Assert.Equal("0.0.0-invented-unseeded", vcenter.Properties["Version"]?.Value as string);
		Assert.Single(OfType(run.Items, "cluster"));
	}

	/// <summary>
	/// Issue #1081 (round-1 review, major 3): the honest-absence path on the SHIPPED
	/// module, not on a pre-#1081 stub. Before the guard the 'vcenter' row was emitted
	/// unconditionally, so a blank instanceUuid produced a row with a blank MoRef --
	/// which <c>DiscoverJobHandler.TryParseItem</c> rejects and
	/// <c>ParseDiscoveredItems</c> counts as malformed, failing the ENTIRE discovery
	/// job under issue #618's no-silent-success rule and taking cluster/host/VM
	/// inventory down with it. Epic #726 section 3 wants an unobservable fact to be
	/// absent and the component skipped, never a failed pass: no vcenter row is
	/// emitted, nothing malformed is produced, and the cluster row still arrives.
	/// </summary>
	[Fact]
	public async Task Discovery_WhenTheSessionCannotSupplyAnInstanceUuid_EmitsNoVcenterRow_AndTheRestOfThePassSurvives()
	{
		PowerShellExecutor executor = CreateExecutor();
		var run = await RunDiscoveryWithSessionsAsync(
			executor,
			"vcsa-01.example.internal",
			"""
			@([pscustomobject]@{ Name = 'vcsa-01.example.internal'; InstanceUuid = '   '; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0001' })
			""");

		Assert.True(run.Succeeded, run.FailureReason);
		Assert.Empty(OfType(run.Items, "vcenter"));

		// The whole point: suppressing the identity-less root costs nothing else.
		Assert.Single(OfType(run.Items, "cluster"));
		System.Management.Automation.PSObject meta = Assert.Single(OfType(run.Items, "discovery-meta"));
		Assert.True(meta.Properties["Complete"]?.Value is true);

		// Nothing emitted carries a blank MoRef/Name, i.e. nothing here can be counted
		// malformed one layer up.
		Assert.All(
			run.Items.Where(i => (i.Properties["Type"]?.Value as string) != "discovery-meta"),
			item =>
			{
				Assert.False(string.IsNullOrWhiteSpace(item.Properties["MoRef"]?.Value as string));
				Assert.False(string.IsNullOrWhiteSpace(item.Properties["Name"]?.Value as string));
			});
	}

	/// <summary>
	/// Issue #1081 (round-1 review, finding (a) folded into major 3): under -AllLinked
	/// the transport can return sibling vCenters. When none matches the requested
	/// -VCenter by name, "first session" would be a GUESS at which linked appliance is
	/// this target's -- attributing a sibling's instanceUuid to this root, exactly the
	/// guessed-identity failure ADR-0023 forbids. Emit nothing instead.
	/// </summary>
	[Fact]
	public async Task Discovery_WhenNoLinkedSessionMatchesByName_EmitsNoVcenterRow_RatherThanGuessingASibling()
	{
		PowerShellExecutor executor = CreateExecutor();
		var run = await RunDiscoveryWithSessionsAsync(
			executor,
			"vcsa-01.example.internal",
			"""
			@(
				[pscustomobject]@{ Name = 'vcsa-77.example.internal'; InstanceUuid = 'vcenter-instance-sibling-0077'; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0077' },
				[pscustomobject]@{ Name = 'vcsa-78.example.internal'; InstanceUuid = 'vcenter-instance-sibling-0078'; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0078' }
			)
			""");

		Assert.True(run.Succeeded, run.FailureReason);
		Assert.Empty(OfType(run.Items, "vcenter"));
		Assert.DoesNotContain(run.Items, i => (i.Properties["MoRef"]?.Value as string)?.Contains("sibling", StringComparison.Ordinal) is true);
	}

	/// <summary>
	/// The counterpart to the test above: a SINGLE session that does not match by name
	/// is not ambiguous at all -- there is only one appliance on the other end of this
	/// connection, and -VCenter given as an IP while the session reports its FQDN (or
	/// vice versa) is an ordinary, expected mismatch. Adopting it keeps issue #1081
	/// working on those deployments instead of silently losing the root.
	/// </summary>
	[Fact]
	public async Task Discovery_WhenTheOnlySessionDoesNotMatchByName_StillEmitsItsIdentity()
	{
		PowerShellExecutor executor = CreateExecutor();
		var run = await RunDiscoveryWithSessionsAsync(
			executor,
			"192.0.2.10",
			"""
			@([pscustomobject]@{ Name = 'vcsa-01.example.internal'; InstanceUuid = 'vcenter-instance-solo-0001'; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0001' })
			""");

		Assert.True(run.Succeeded, run.FailureReason);
		System.Management.Automation.PSObject vcenter = Assert.Single(OfType(run.Items, "vcenter"));
		Assert.Equal("vcenter-instance-solo-0001", vcenter.Properties["MoRef"]?.Value as string);
	}
}
