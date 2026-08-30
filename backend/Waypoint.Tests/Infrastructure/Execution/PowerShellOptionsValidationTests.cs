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

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Execution.DependencyInjection;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #1305 (review round 1) + issue #1324: <see cref="PowerShellOptions"/>'s
/// configuration surface, both halves.
///
/// <para><b>Bounds.</b> <see cref="PowerShellOptions.DiscoveryDnsTimeoutMilliseconds"/>
/// exists for one reason -- to bound a DNS hang -- and the values just outside its
/// accepted range defeat that reason silently rather than loudly:
/// <c>-1</c> is <c>Timeout.Infinite</c>, so <c>Task.Wait(-1)</c> in
/// <c>WaypointDiscovery.psm1</c> waits for a blackholed resolver forever and never
/// reaches the warning branch (the unbounded stall issues #1251/#1297 removed, quietly
/// reinstated by one plausible operator value); <c>0</c> makes every lookup time out
/// instantly, disabling DNS-based session matching; and an absurdly large value is a
/// hang with extra steps. So the runner must refuse to start on any of them, with a
/// message that names the option, rather than accept the value and misbehave at the
/// first lookup. These tests drive the real <c>AddWaypointExecution</c> registration,
/// so they fail if the <c>.ValidateDataAnnotations().ValidateOnStart()</c> pair (or the
/// <c>[Range]</c> itself) is ever dropped.</para>
///
/// <para><b>Binding.</b> The <c>PowerShell__…</c> environment-variable form is the only
/// way a deployment sets these (see <c>deploy/compose.yaml</c>'s
/// <c>PowerShell__ModulePreloadPaths__0</c>), and nothing proved that prefix actually
/// reaches <see cref="PowerShellOptions"/>: a section-name or double-underscore
/// mistake fails silently, leaving every option on its default while the deployment
/// looks configured.</para>
/// </summary>
public sealed class PowerShellOptionsValidationTests
{
	/// <summary>The shipped default, unchanged by this issue: the module's own ceiling.</summary>
	[Fact]
	public void DiscoveryDnsTimeoutMilliseconds_DefaultsToTheModulesOwnCeiling()
	{
		using ServiceProvider provider = BuildProvider();

		Assert.Equal(3000, provider.GetRequiredService<IOptions<PowerShellOptions>>().Value.DiscoveryDnsTimeoutMilliseconds);
	}

	/// <summary>
	/// -1 (<c>Timeout.Infinite</c>), 0 and an absurd value all fail startup validation.
	/// <see cref="IStartupValidator"/> is what <c>ValidateOnStart</c> registers and what
	/// the host runs before serving, so calling it here is the unit-level equivalent of
	/// booting the runner.
	/// </summary>
	[Theory]
	[InlineData("-1")]
	[InlineData("0")]
	[InlineData("99")]
	[InlineData("999999")]
	public void DiscoveryDnsTimeoutMilliseconds_OutOfRange_FailsStartupValidation(string configured)
	{
		using ServiceProvider provider = BuildProvider(("PowerShell:DiscoveryDnsTimeoutMilliseconds", configured));

		OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
			() => provider.GetRequiredService<IStartupValidator>().Validate());

		string message = string.Join(" ", exception.Failures);
		Assert.Contains(nameof(PowerShellOptions.DiscoveryDnsTimeoutMilliseconds), message, StringComparison.Ordinal);
		Assert.Contains("100", message, StringComparison.Ordinal);
		Assert.Contains("60000", message, StringComparison.Ordinal);
	}

	/// <summary>Both ends of the accepted range, and a realistic slow-resolver value, start cleanly.</summary>
	[Theory]
	[InlineData("100")]
	[InlineData("8000")]
	[InlineData("60000")]
	public void DiscoveryDnsTimeoutMilliseconds_InRange_PassesStartupValidation(string configured)
	{
		using ServiceProvider provider = BuildProvider(("PowerShell:DiscoveryDnsTimeoutMilliseconds", configured));

		provider.GetRequiredService<IStartupValidator>().Validate();

		Assert.Equal(
			int.Parse(configured, System.Globalization.CultureInfo.InvariantCulture),
			provider.GetRequiredService<IOptions<PowerShellOptions>>().Value.DiscoveryDnsTimeoutMilliseconds);
	}

	/// <summary>
	/// Issue #1324: the <c>PowerShell__</c> env-var prefix really binds, double
	/// underscore and all. Asserted against the real <c>AddEnvironmentVariables()</c>
	/// provider rather than an in-memory <c>PowerShell:…</c> key, because the
	/// double-underscore translation is precisely the step a deployment gets wrong.
	/// </summary>
	[Fact]
	public void PowerShellEnvironmentVariablePrefix_BindsToPowerShellOptions()
	{
		const string Variable = "PowerShell__DiscoveryDnsTimeoutMilliseconds";
		string? original = Environment.GetEnvironmentVariable(Variable);
		try
		{
			Environment.SetEnvironmentVariable(Variable, "4500");

			IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
			ServiceCollection services = new();
			services.AddLogging();
			services.AddWaypointExecution(configuration);
			using ServiceProvider provider = services.BuildServiceProvider();

			Assert.Equal(4500, provider.GetRequiredService<IOptions<PowerShellOptions>>().Value.DiscoveryDnsTimeoutMilliseconds);
		}
		finally
		{
			Environment.SetEnvironmentVariable(Variable, original);
		}
	}

	/// <summary>
	/// No connection string: <c>AddWaypointExecution</c> returns before wiring any
	/// job-shaped service, but the options registrations above that guard still run --
	/// which is exactly the surface these tests need, with no database required.
	/// </summary>
	private static ServiceProvider BuildProvider(params (string Key, string Value)[] settings)
	{
		IConfiguration configuration = new ConfigurationBuilder()
			.AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
			.Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointExecution(configuration);
		return services.BuildServiceProvider();
	}
}
