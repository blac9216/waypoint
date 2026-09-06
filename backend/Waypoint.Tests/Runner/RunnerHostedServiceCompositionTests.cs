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
using Microsoft.Extensions.Hosting;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Infrastructure.Execution.ComplianceContent;
using Waypoint.Infrastructure.Execution.DependencyInjection;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// Issue #1707 regression coverage: validation run 1 (epic #1704) found
/// <see cref="ContentPullReconcileHostedService"/> started in BOTH runner hosts even
/// though migration 0073 grants <c>content_pull_checks</c> to
/// <c>waypoint_compliance_runner</c> alone, flooding the download-runner's log with
/// <c>42501</c> every sweep tick. <see cref="ComplianceRunnerCompositionTests"/>
/// already proves the sibling <c>JobHandlerRegistry</c> domain split this way; this
/// class proves the hosted-service half of the same split by building each host's
/// <see cref="IServiceCollection"/> exactly as its own <c>Program.cs</c> does (see
/// <c>Waypoint.ComplianceRunner.Program</c>'s <c>AddContentPullReconcileSweep</c> call
/// and <c>Waypoint.DownloadRunner.Program</c>'s deliberate absence of one) and
/// inspecting the resulting <see cref="ServiceDescriptor"/> set -- no live Postgres, no
/// service provider build, exactly like the sibling test.
/// </summary>
public sealed class RunnerHostedServiceCompositionTests
{
	private static IConfiguration BuildConfiguration()
	{
		List<KeyValuePair<string, string?>> settings =
		[
			new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
		];
		return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
	}

	private static bool RegistersContentPullReconcileHostedService(IServiceCollection services) =>
		services.Any(descriptor =>
			descriptor.ServiceType == typeof(IHostedService) &&
			descriptor.ImplementationType == typeof(ContentPullReconcileHostedService));

	/// <summary>Mirrors <c>Waypoint.ComplianceRunner.Program</c>'s composition exactly, including its <c>AddContentPullReconcileSweep</c> call.</summary>
	[Fact]
	public void ComplianceRunnerHost_RegistersContentPullReconcileHostedService()
	{
		IConfiguration configuration = BuildConfiguration();
		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);
		services.AddContentPullReconcileSweep();

		Assert.True(RegistersContentPullReconcileHostedService(services));
	}

	/// <summary>
	/// Mirrors <c>Waypoint.DownloadRunner.Program</c>'s composition exactly --
	/// <see cref="ServiceCollectionExtensions.AddWaypointExecution"/> only, never
	/// <c>AddContentPullReconcileSweep</c>. This is the exact regression: before issue
	/// #1707, <c>AddWaypointExecution</c> alone already started the sweep, which is why
	/// this asserts absence rather than merely "the download-runner doesn't call the
	/// new method" (a tautology that would not have caught the original bug).
	/// </summary>
	[Fact]
	public void DownloadRunnerHost_DoesNotRegisterContentPullReconcileHostedService()
	{
		IConfiguration configuration = BuildConfiguration();
		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);

		Assert.False(RegistersContentPullReconcileHostedService(services));
	}
}
