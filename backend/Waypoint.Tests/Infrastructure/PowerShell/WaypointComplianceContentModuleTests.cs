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

using System.Management.Automation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;
using Xunit;

namespace Waypoint.Tests.Infrastructure.PowerShell;

/// <summary>
/// Issue #617: exercises the REAL <c>WaypointComplianceContent.psm1</c> module (not a
/// stub -- this class's whole purpose is proving the module's filesystem-discovery
/// logic against a shape the stub modules never modeled) via a genuine in-process SMA
/// runspace, the same harness <see cref="PowerShellExecutorTests"/> uses. The fixture
/// tree built in <see cref="CreateNestedFixture"/> is INVENTED -- it mirrors the real
/// vmware/dod-compliance-and-automation repo's reported nesting depth and leaf-name
/// collisions (e.g. multiple "postgresql"-named profile directories under different
/// baselines) without copying any real file content, per docs/testing.md's
/// fixture-monoculture guidance and this repo's sanitization rules.
/// </summary>
public sealed class WaypointComplianceContentModuleTests : IDisposable
{
	private static readonly string ModulePath = Path.Combine(
		AppContext.BaseDirectory, "PowerShell", "Modules", "WaypointComplianceContent", "WaypointComplianceContent.psm1");

	private sealed class NullLogBuffer : IJobLogBuffer
	{
		public bool TryEnqueue(string eventType, Guid? jobId, Guid? runId, string payloadJson) => true;
	}

	private readonly string _contentPath = Directory.CreateTempSubdirectory("wp-compliance-content-fixture").FullName;
	private WaypointRunspacePool _pool = null!;
	private PowerShellExecutor _executor = null!;

	public void Dispose()
	{
		_pool?.Dispose();
		if (Directory.Exists(_contentPath))
		{
			Directory.Delete(_contentPath, recursive: true);
		}
	}

	private PowerShellExecutor CreateExecutor()
	{
		PowerShellOptions options = new() { MaxRunspaces = 2, DefaultInvocationTimeout = TimeSpan.FromMinutes(1) };
		options.ModulePreloadPaths.Add(ModulePath);
		IOptions<PowerShellOptions> wrapped = Options.Create(options);
		_pool = new WaypointRunspacePool(wrapped, NullLogger<WaypointRunspacePool>.Instance);
		return new PowerShellExecutor(_pool, new NullLogBuffer(), wrapped, NullLogger<PowerShellExecutor>.Instance);
	}

	/// <summary>
	/// Invented tree shaped like the real repo's reported layout: several baselines,
	/// each several directories deep, two of which deliberately reuse the leaf
	/// directory name "postgresql" (the exact collision issue #617 reports) plus a
	/// shallow single-level profile so the fix does not regress the flat-layout case
	/// the original unit-test fixtures covered. Only "postgresql-a" and
	/// "postgresql-b" get a controls/ directory with invented *.rb control files;
	/// the rest exist purely to prove non-collapse of the inventory count.
	/// </summary>
	private void CreateNestedFixture()
	{
		string vidmPostgres = Path.Combine(_contentPath, "vidm", "3.3.x", "v1r3-srg", "inspec", "vmware-vidm-3.3.x-stig-baseline", "postgresql");
		string vropsPostgres = Path.Combine(_contentPath, "vrops", "8.6.x", "v1r2-srg", "inspec", "vmware-vrops-8.6.x-stig-baseline", "postgresql");
		string vidmAaa = Path.Combine(_contentPath, "vidm", "3.3.x", "v1r3-srg", "inspec", "vmware-vidm-3.3.x-stig-baseline", "aaa");
		string vropsAaa = Path.Combine(_contentPath, "vrops", "8.6.x", "v1r2-srg", "inspec", "vmware-vrops-8.6.x-stig-baseline", "aaa");
		string flatProfile = Path.Combine(_contentPath, "flat-profile");

		foreach (string dir in new[] { vidmPostgres, vropsPostgres, vidmAaa, vropsAaa, flatProfile })
		{
			Directory.CreateDirectory(dir);
			File.WriteAllText(Path.Combine(dir, "inspec.yml"), "name: invented-fixture-profile\n");
		}

		WriteControl(vidmPostgres, "V-001.rb", "V-001", "Invented control one", "medium");
		WriteControl(vidmPostgres, "V-002.rb", "V-002", "Invented control two", "high");
		WriteControl(vropsPostgres, "V-101.rb", "V-101", "Other baseline's control", "low");
	}

	private static void WriteControl(string profileDir, string fileName, string controlId, string title, string severity)
	{
		string controlsDir = Path.Combine(profileDir, "controls");
		Directory.CreateDirectory(controlsDir);
		string body = $$"""
control '{{controlId}}' do
  title '{{title}}'
  desc 'Invented fixture control body, not real DoD STIG content.'
  tag severity: '{{severity}}'
  describe 'invented' do
    it { should_not be_empty }
  end
end
""";
		File.WriteAllText(Path.Combine(controlsDir, fileName), body);
	}

	[Fact]
	public async Task GetProfiles_NestedRepoWithCollidingBasenames_YieldsOneRowPerProfile_KeyedByRelativePath()
	{
		CreateNestedFixture();
		_executor = CreateExecutor();

		PowerShellExecutionResult result = await _executor.ExecuteAsync(
			new PowerShellRequest(
				"Get-WaypointComplianceContentProfiles",
				Parameters: new Dictionary<string, object?> { ["ContentPath"] = _contentPath, ["Commit"] = "deadbeef" }),
			CancellationToken.None);

		Assert.True(result.Succeeded);

		List<string> keys = [.. result.Output
			.Select(o => PSObject.AsPSObject(o!).Properties["ProfileKey"]!.Value?.ToString())
			.Where(k => k is not null)!
			.Cast<string>()];

		// Five distinct profiles, none collapsed despite two pairs sharing a leaf
		// basename ("postgresql" and "aaa") -- the exact defect issue #617 reports.
		Assert.Equal(5, keys.Count);
		Assert.Equal(5, keys.Distinct(StringComparer.Ordinal).Count());

		Assert.Contains(keys, k => k.EndsWith("vmware-vidm-3.3.x-stig-baseline/postgresql", StringComparison.Ordinal));
		Assert.Contains(keys, k => k.EndsWith("vmware-vrops-8.6.x-stig-baseline/postgresql", StringComparison.Ordinal));
		Assert.Contains(keys, k => k.EndsWith("vmware-vidm-3.3.x-stig-baseline/aaa", StringComparison.Ordinal));
		Assert.Contains(keys, k => k.EndsWith("vmware-vrops-8.6.x-stig-baseline/aaa", StringComparison.Ordinal));
		Assert.Contains(keys, k => k == "flat-profile");

		// Keys never contain a backslash, even though ContentPath's segments were
		// joined with Path.Combine -- normalized so the stored key is stable across
		// a runner rebuilt on a different OS.
		Assert.All(keys, k => Assert.DoesNotContain('\\', k));
	}

	[Fact]
	public async Task GetProfiles_SameLayoutPulledTwice_ProducesIdenticalKeys()
	{
		CreateNestedFixture();
		_executor = CreateExecutor();

		async Task<List<string>> RunOnceAsync()
		{
			PowerShellExecutionResult result = await _executor.ExecuteAsync(
				new PowerShellRequest(
					"Get-WaypointComplianceContentProfiles",
					Parameters: new Dictionary<string, object?> { ["ContentPath"] = _contentPath, ["Commit"] = "c1" }),
				CancellationToken.None);
			Assert.True(result.Succeeded);
			return [.. result.Output
				.Select(o => PSObject.AsPSObject(o!).Properties["ProfileKey"]!.Value!.ToString()!)
				.OrderBy(k => k, StringComparer.Ordinal)];
		}

		// Issue #617 key-stability requirement: a re-pull of the same tree must
		// upsert existing profile rows (ON CONFLICT (profile_key)), not duplicate
		// them -- which only holds if the same profile yields the same key every time.
		List<string> first = await RunOnceAsync();
		List<string> second = await RunOnceAsync();

		Assert.Equal(first, second);
	}

	[Fact]
	public async Task GetControls_NestedProfileDirectory_ParsesControlsRegardlessOfDepth()
	{
		CreateNestedFixture();
		_executor = CreateExecutor();
		string nestedProfileDir = Path.Combine(
			_contentPath, "vidm", "3.3.x", "v1r3-srg", "inspec", "vmware-vidm-3.3.x-stig-baseline", "postgresql");

		PowerShellExecutionResult result = await _executor.ExecuteAsync(
			new PowerShellRequest(
				"Get-WaypointComplianceContentControls",
				Parameters: new Dictionary<string, object?> { ["ProfileDirectory"] = nestedProfileDir }),
			CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Equal(2, result.Output.Count);

		List<PSObject> controls = [.. result.Output.Select(o => PSObject.AsPSObject(o!))];
		Assert.Contains(controls, c => c.Properties["ControlId"]!.Value!.ToString() == "V-001"
			&& c.Properties["Title"]!.Value!.ToString() == "Invented control one"
			&& c.Properties["Severity"]!.Value!.ToString() == "medium");
		Assert.Contains(controls, c => c.Properties["ControlId"]!.Value!.ToString() == "V-002");
	}

	[Fact]
	public async Task GetControls_SiblingProfileWithNoControlsProperty_ReturnsEmpty_NotFailure()
	{
		CreateNestedFixture();
		_executor = CreateExecutor();
		string aaaDir = Path.Combine(
			_contentPath, "vidm", "3.3.x", "v1r3-srg", "inspec", "vmware-vidm-3.3.x-stig-baseline", "aaa");

		PowerShellExecutionResult result = await _executor.ExecuteAsync(
			new PowerShellRequest(
				"Get-WaypointComplianceContentControls",
				Parameters: new Dictionary<string, object?> { ["ProfileDirectory"] = aaaDir }),
			CancellationToken.None);

		Assert.True(result.Succeeded);
		Assert.Empty(result.Output);
	}
}
