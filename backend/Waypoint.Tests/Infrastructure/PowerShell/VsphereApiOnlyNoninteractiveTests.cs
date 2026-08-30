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
	/// Issue #1252: the deterministic stand-in for real DNS that
	/// <c>RunDiscoveryWithSessionsAsync</c> passes as <c>Invoke-WaypointDiscovery</c>'s
	/// <c>-NameResolver</c> BY DEFAULT, so a test driving the module with fake sessions
	/// never resolves anything for real -- no NXDOMAIN dependency, no resolver-timeout
	/// latency (issue #1251). It reports a synthetic, per-name-unique "address" (so the
	/// forward-candidate loop in Test-WaypointSessionMatchesVCenter still runs, the way
	/// a resolver that is actually answering would drive it) that never collides across
	/// distinct names, so it never manufactures a false match; the real resolvers'
	/// fail-closed "no addresses" contract is exercised separately by the mocked/opt-in
	/// Pester cases. Records every (mode, value) pair it was asked to resolve into a
	/// runspace-global list -- read back by the helper's audit row, and cleared by the
	/// helper's <c>finally</c> -- and logs each one via <c>Write-Information</c>
	/// (captured into job.log by the shared WaypointLogging adapter) so a test can prove
	/// the seam -- not real DNS -- was actually exercised.
	/// </summary>
	private const string StubNameResolverExpression = """
		{
			param($Mode, $Value)
			$Global:WaypointNameResolverCalls.Add("$($Mode):$($Value)")
			Write-Information "NameResolverAsked:$($Mode):$($Value)"
			@("stub-address-for-$($Value)")
		}
		""";

	/// <summary>
	/// Issue #1252 (round-1 review, finding 1): the class-level guarantee that no test in
	/// this file can fall through to real DNS. Redefines the module's two lowest-level
	/// resolver functions IN <c>WaypointDiscovery</c>'s OWN SCOPE (dot-sourcing into the
	/// module's session state) so that any lookup the <c>-NameResolver</c> seam failed to
	/// intercept is recorded instead of sent to the network -- which the helper below
	/// then asserts never happened. Without this, "the stub is passed" and "no real DNS
	/// ran" are two different claims: <c>Where-Object</c> evaluates EVERY session, so
	/// even a test whose target matches at tier 1 (case/trailing-dot, no DNS) still
	/// forward-resolves for the siblings that fall through -- which is exactly how
	/// <c>…MatchesTheTargetOnlyAfterNormalization…</c> kept hitting real DNS while its
	/// doc comment claimed it "needs no DNS".
	/// </summary>
	private const string PoisonRealDnsExpression = """
		$Global:WaypointRealDnsCalls = [System.Collections.Generic.List[string]]::new()
		$Global:WaypointNameResolverCalls = [System.Collections.Generic.List[string]]::new()
		$WaypointDiscoveryModule = Get-Module WaypointDiscovery
		if (-not $WaypointDiscoveryModule) { throw 'expected the WaypointDiscovery module to be preloaded in this runspace' }
		. $WaypointDiscoveryModule {
			function Resolve-WaypointHostAddresses {
				param([string]$HostNameOrAddress, [int]$TimeoutMilliseconds = 0, [scriptblock]$AddressTaskFactory)
				$Global:WaypointRealDnsCalls.Add("Forward:$HostNameOrAddress")
				return @()
			}
			function Resolve-WaypointReverseHostNames {
				param([string]$IpAddress, [int]$TimeoutMilliseconds = 0, [scriptblock]$HostEntryTaskFactory)
				$Global:WaypointRealDnsCalls.Add("Reverse:$IpAddress")
				return @()
			}
		}
		""";

	/// <summary>The audit row the helper emits after the discovery pass and strips from the returned items.</summary>
	private const string ResolverAuditRowType = "test-name-resolver-audit";

	/// <summary>
	/// Runs the REAL <c>Invoke-WaypointDiscovery</c> with the fake transport's session
	/// list overridden for the duration of this one pipeline, and returns the emitted
	/// item objects. Issue #1081's emission guard can only be pinned against a session
	/// shape a real appliance can produce, and the transport fake is where that shape
	/// is decided.
	///
	/// Issue #1252: hermetic BY DEFAULT -- every call injects
	/// <see cref="StubNameResolverExpression"/> and poisons the module's real resolvers
	/// (<see cref="PoisonRealDnsExpression"/>), and this helper then asserts that the
	/// module made no real-DNS call at all during the session sweep.
	///
	/// Issue #1306: unconditionally hermetic -- the earlier <c>useRealDns</c> opt-out
	/// (and the four branches it gated) is gone. Every call site took the default,
	/// its own doc comment called it something "no test does and none should", and a
	/// genuine real-DNS integration test would need its own dedicated, explicitly
	/// opt-in test rather than a parameter threaded through this shared helper.
	/// </summary>
	private static async Task<(bool Succeeded, string? FailureReason, IReadOnlyList<System.Management.Automation.PSObject> Items, IReadOnlyList<string> ResolverCalls)>
		RunDiscoveryWithSessionsAsync(
			PowerShellExecutor executor, string vCenter, string sessionsExpression)
	{
		string auditRow = $$"""
			[pscustomobject]@{
				Type = '{{ResolverAuditRowType}}'
				RealDnsCalls = @($Global:WaypointRealDnsCalls)
				ResolverCalls = @($Global:WaypointNameResolverCalls)
			}
			""";

		PowerShellExecutionResult result = await executor.ExecuteAsync(
			new PowerShellRequest(
				$$"""
				{{PoisonRealDnsExpression}}
				$Global:WaypointVsphereTransportFakeSessions = {{sessionsExpression}}
				$Global:WaypointVsphereTransportFakeClusters = @([pscustomobject]@{
					Name = 'cluster-alpha'
					ExtensionData = [pscustomobject]@{ MoRef = [pscustomobject]@{ Value = 'domain-c101' } }
				})
				try {
					Invoke-WaypointDiscovery -VCenter '{{vCenter}}' -Username 'administrator@example.internal' -Password 'invented-test-password' -NameResolver {{StubNameResolverExpression}}
					{{auditRow}}
				} finally {
					$Global:WaypointVsphereTransportFakeSessions = $null
					$Global:WaypointVsphereTransportFakeClusters = $null
					$Global:WaypointRealDnsCalls = $null
					$Global:WaypointNameResolverCalls = $null
				}
				""",
				PowerShellRequestKind.Script),
			CancellationToken.None);

		List<System.Management.Automation.PSObject> items = result.Output
			.Where(o => o is not null)
			.Select(o => System.Management.Automation.PSObject.AsPSObject(o!))
			.ToList();

		List<System.Management.Automation.PSObject> auditRows = OfType(items, ResolverAuditRowType);
		items.RemoveAll(i => auditRows.Contains(i));

		IReadOnlyList<string> resolverCalls = [];
		if (result.Succeeded)
		{
			// The class-level guarantee (issue #1252 round-1 review, finding 1): not
			// merely "a stub was passed" but "the module never reached the network on
			// this run", asserted for EVERY caller of this helper rather than test by test.
			System.Management.Automation.PSObject audit = Assert.Single(auditRows);
			List<string> realDnsCalls = AsStrings(audit, "RealDnsCalls");
			Assert.True(
				realDnsCalls.Count == 0,
				$"expected no real-DNS lookup on a hermetic run, but WaypointDiscovery resolved: {string.Join(", ", realDnsCalls)}");
			resolverCalls = AsStrings(audit, "ResolverCalls");
		}

		return (result.Succeeded, result.FailureReason, items, resolverCalls);
	}

	private static List<string> AsStrings(System.Management.Automation.PSObject row, string propertyName)
		=> (row.Properties[propertyName]?.Value as System.Collections.IEnumerable)?
			.Cast<object?>()
			.Select(v => (v is System.Management.Automation.PSObject p ? p.BaseObject : v)?.ToString() ?? string.Empty)
			.ToList() ?? [];

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
	///
	/// Issue #1252: none of these three names has a tier-1 (normalized name/ServiceUri)
	/// match, so resolution used to fall through to real forward DNS for all three --
	/// hermetic only because the test's own resolver returns NXDOMAIN for them, which a
	/// hijacking/wildcard resolver would not do, and which pays a real resolver-timeout
	/// per lookup (issue #1251) even when it does. The injected <see cref="StubNameResolverExpression"/>
	/// makes the "no match" outcome deterministic on any network and removes the
	/// latency entirely -- and the helper's audit assertion proves the module never
	/// reached the network on this run.
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

		// No real DNS was touched -- the helper asserts that for every run (issue #1252),
		// so this outcome is the stub's, not the host resolver's, and it cannot pay a
		// resolver timeout (issue #1251's "silent latency"). No wall-clock assertion:
		// timing assertions are a standing flake source in this repo (issue #658), and the
		// recorded resolver calls prove the same thing deterministically.
		Assert.Contains("Forward:vcsa-01.example.internal", run.ResolverCalls);
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

	/// <summary>
	/// Issue #1115 (round-1 review, finding 1): the CI-enforced regression guard for the
	/// identity-based session match, driving the SHIPPED <c>WaypointDiscovery.psm1</c>
	/// through <c>Invoke-WaypointDiscovery</c>. Two linked sessions are connected -- so
	/// the "exactly one session" fallback cannot carry the case -- and the requested
	/// -VCenter differs from the session's reported name only in case and a trailing
	/// dot. The pre-#1115 exact `$_.Name -eq $VCenter` comparison matched neither
	/// session and emitted NO vcenter row at all; the tier-1 normalized comparison in
	/// Resolve-WaypointPrimarySession matches vcsa-77 and only vcsa-77.
	///
	/// Issue #1252 (round-1 review, finding 1): this test used to claim that a tier-1
	/// (case/trailing-dot) match "needs no DNS, so the assertion is deterministic on any
	/// host". That premise is FALSE, and it is why this test kept resolving for real:
	/// Resolve-WaypointPrimarySession's Where-Object evaluates EVERY session, and the
	/// non-matching sibling vcsa-78 falls through tier 1, so Test-WaypointSessionMatchesVCenter
	/// forward-resolves the target for real. On a wildcard/hijacking resolver both names
	/// resolve to the same synthesized address, tier 2 then matches the sibling too, the
	/// sweep sees 2 matches, ambiguity withholds the row, and this assertion fails. The
	/// helper is hermetic by default now, so the stub -- never the host resolver --
	/// answers, and the helper asserts no real lookup was made.
	/// </summary>
	[Fact]
	public async Task Discovery_WhenALinkedSessionMatchesTheTargetOnlyAfterNormalization_EmitsThatSessionsIdentity()
	{
		PowerShellExecutor executor = CreateExecutor();
		var run = await RunDiscoveryWithSessionsAsync(
			executor,
			"VCSA-77.Example.Internal.",
			"""
			@(
				[pscustomobject]@{ Name = 'vcsa-77.example.internal'; InstanceUuid = 'vcenter-instance-sibling-0077'; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0077' },
				[pscustomobject]@{ Name = 'vcsa-78.example.internal'; InstanceUuid = 'vcenter-instance-sibling-0078'; Version = '0.0.0-invented-unseeded'; Build = 'invented-build-0078' }
			)
			""");

		Assert.True(run.Succeeded, run.FailureReason);
		System.Management.Automation.PSObject vcenter = Assert.Single(OfType(run.Items, "vcenter"));
		Assert.Equal("vcenter-instance-sibling-0077", vcenter.Properties["MoRef"]?.Value as string);
		Assert.Equal("vcsa-77.example.internal", vcenter.Properties["Name"]?.Value as string);

		// The sibling's identity must never be the one adopted.
		Assert.NotEqual("vcenter-instance-sibling-0078", vcenter.Properties["MoRef"]?.Value as string);

		// The rest of the pass is unaffected (the fake transport walks the cluster
		// fixture once per linked session, so two sessions yield two cluster rows).
		Assert.Equal(2, OfType(run.Items, "cluster").Count);

		// Direct evidence for the corrected premise above: a tier-1 match still drove a
		// forward resolution (for the sibling that fell through) -- through the stub.
		Assert.Contains("Forward:VCSA-77.Example.Internal.", run.ResolverCalls);
	}

	/// <summary>
	/// The mirror of the guard above (issue #1115 round-1 review, finding 1, second
	/// half): with two linked sessions and a -VCenter that matches NEITHER by any tier
	/// -- normalized name, forward address, or reverse PTR -- the fail-closed contract
	/// still holds end-to-end on the shipped module: no vcenter row, no sibling
	/// identity adopted by position, and the operator gets a Write-Warning in the job
	/// log naming both counts so the absence is explained rather than silent.
	/// </summary>
	[Fact]
	public async Task Discovery_WhenNoLinkedSessionMatchesByIdentity_EmitsNoVcenterRow_AndWarnsWithBothCounts()
	{
		PowerShellExecutor executor = CreateExecutor(out RecordingLogBuffer logBuffer);
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

		// The absence is explained, not silent: severity 'warning', the target host,
		// and both counts (2 linked sessions, 0 matched).
		Assert.Contains(
			logBuffer.Payloads,
			payload =>
				payload.Contains("\"severity\":\"warning\"", StringComparison.Ordinal) &&
				payload.Contains("could not uniquely identify the vCenter session", StringComparison.Ordinal) &&
				payload.Contains("vcsa-01.example.internal", StringComparison.Ordinal) &&
				payload.Contains("among 2 linked session(s)", StringComparison.Ordinal) &&
				payload.Contains("(0 matched by name/address)", StringComparison.Ordinal));

		// And the pass itself survives the withheld root: inventory still arrives
		// (one cluster row per linked session from the fake transport).
		Assert.Equal(2, OfType(run.Items, "cluster").Count);
	}

	/// <summary>
	/// Issue #1252: the CI-enforced guard for the resolver seam itself (CI does not run
	/// the Pester suites outside the ShapeInventory directory, issue #1245, so this
	/// xunit test -- not <c>WaypointDiscovery.SessionMatch.Tests.ps1</c>'s Pester
	/// equivalent -- is what actually proves the seam in the pipeline). Drives the same
	/// "two linked siblings, no match" shape as the tests above, but asserts directly
	/// on the stub's own recorded calls: if <c>-NameResolver</c> were silently ignored
	/// and real DNS ran instead, nothing would ever be added to
	/// <c>$Global:WaypointNameResolverCalls</c> and this would fail even though the
	/// "no vcenter row" outcome looked identical.
	/// </summary>
	[Fact]
	public async Task Discovery_RoutesForwardResolutionThroughTheInjectedNameResolver_NotRealDns()
	{
		PowerShellExecutor executor = CreateExecutor(out RecordingLogBuffer logBuffer);
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

		// The stub was asked to forward-resolve the target and both candidate names --
		// proof the seam (not real DNS) drove the "no match" outcome above.
		Assert.Contains(logBuffer.Payloads, p => p.Contains("NameResolverAsked:Forward:vcsa-01.example.internal", StringComparison.Ordinal));
		Assert.Contains(logBuffer.Payloads, p => p.Contains("NameResolverAsked:Forward:vcsa-77.example.internal", StringComparison.Ordinal));
		Assert.Contains(logBuffer.Payloads, p => p.Contains("NameResolverAsked:Forward:vcsa-78.example.internal", StringComparison.Ordinal));

		// The same fact off the stub's own recorded-call list (issue #1298: the list is
		// read, not merely written, and the helper clears it after every run).
		Assert.Equal(
			["Forward:vcsa-01.example.internal", "Forward:vcsa-77.example.internal", "Forward:vcsa-78.example.internal"],
			run.ResolverCalls.Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray());
	}
}
