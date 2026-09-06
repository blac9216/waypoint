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
/// <c>42501</c> every sweep tick. Round 1 (same PR) found the opposite failure mode on
/// the compliance-runner side: <c>AddContentPullReconcileSweep</c> originally took no
/// <see cref="IConfiguration"/> and registered <see cref="ContentPullReconcileHostedService"/>
/// unconditionally, so a compliance-runner started with no
/// <c>ConnectionStrings:Waypoint</c> configured -- which
/// <see cref="ServiceCollectionExtensions.AddWaypointExecution"/> treats as a legitimate
/// "registers nothing job-shaped" no-op, mirrored by
/// <see cref="Waypoint.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddWaypointInfrastructure"/>'s
/// own guard -- threw <c>Unable to resolve service for type 'ContentPullReconcileService'</c>
/// at startup instead of also no-opping.
///
/// <see cref="ComplianceRunnerCompositionTests"/> already proves the sibling
/// <see cref="Waypoint.Core.Jobs.JobHandlerRegistry"/> domain split this way; this class
/// proves the hosted-service half of the same split by building the part of each host's
/// <see cref="IServiceCollection"/> that can register
/// <see cref="ContentPullReconcileHostedService"/> -- <c>AddWaypointInfrastructure</c> +
/// <c>AddWaypointExecution</c>, plus <c>AddContentPullReconcileSweep</c> on the
/// compliance side only (see <c>Waypoint.ComplianceRunner.Program</c>'s call and
/// <c>Waypoint.DownloadRunner.Program</c>'s deliberate absence of one) -- and then
/// actually calling <c>BuildServiceProvider</c> with both
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> and
/// <see cref="ServiceProviderOptions.ValidateScopes"/> set -- not merely inspecting the
/// resulting <see cref="ServiceDescriptor"/> set, which the CI-configuration case alone
/// cannot distinguish "registered and resolvable" from "registered but missing a
/// dependency" (exactly the empty-configuration regression above). Every case here
/// covers both a configuration with <c>ConnectionStrings:Waypoint</c> set and one
/// without it, since the empty case is the one the original fix missed.
///
/// PR #1745 round 2 (finding A): this is deliberately NOT a full re-composition of
/// either host, and the validated build here therefore validates a strict subset of the
/// real graph. Both helpers omit <c>DatabaseConnectionStringResolver.ResolveAndApply</c>,
/// the hosts' options bindings (<c>RunnerHealthOptions</c> / <c>DownloadRunnerOptions</c>),
/// <c>IWorkerRegistryWriter</c>, the download host's <c>JobHandlerRegistry</c> capability
/// override, and the health/readiness hosted services
/// (<c>RunnerHealthReportingHostedService</c>, <c>ComplianceReadinessCheck</c>,
/// <c>ReadinessReportingHostedService</c>) -- registrations unrelated to whether the
/// content-pull sweep is wired, whose real dependencies (a resolved connection string, a
/// live worker registry) a unit test cannot supply. A green case here therefore means
/// "the content-pull sweep composition is correct and resolvable", not "this host boots";
/// the compliance-runner's boot with no connection string in fact still fails on the
/// unconditional <c>RunnerHealthReportingHostedService</c> registration
/// (<c>Waypoint.ComplianceRunner.Program.cs:110</c>), tracked separately.
/// </summary>
public sealed class RunnerHostedServiceCompositionTests
{
	private static IConfiguration BuildConfiguration(bool withConnectionString)
	{
		List<KeyValuePair<string, string?>> settings = withConnectionString
			?
			[
				new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
			]
			: [];
		return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
	}

	private static bool RegistersContentPullReconcileHostedService(IServiceCollection services) =>
		services.Any(descriptor =>
			descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
			descriptor.ImplementationType == typeof(ContentPullReconcileHostedService));

	private static readonly Microsoft.Extensions.DependencyInjection.ServiceProviderOptions ValidatedProviderOptions = new()
	{
		ValidateOnBuild = true,
		ValidateScopes = true,
	};

	/// <summary>
	/// Builds the compliance-runner's infrastructure + execution registrations --
	/// <c>AddWaypointInfrastructure</c>, <c>AddWaypointExecution</c> and the
	/// <c>AddContentPullReconcileSweep</c> call that only this host makes -- which are the
	/// registrations that decide whether <see cref="ContentPullReconcileHostedService"/>
	/// is wired. It deliberately omits the rest of <c>Waypoint.ComplianceRunner.Program</c>
	/// (see the class comment: <c>ResolveAndApply</c>, the <c>RunnerHealthOptions</c>
	/// binding, <c>IWorkerRegistryWriter</c>, <c>ComplianceReadinessCheck</c> and
	/// <c>RunnerHealthReportingHostedService</c>), none of which can register or suppress
	/// the sweep and none of which is resolvable without a real database and registry.
	/// </summary>
	private static ServiceCollection BuildComplianceRunnerServices(IConfiguration configuration)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);
		services.AddContentPullReconcileSweep(configuration);
		return services;
	}

	/// <summary>
	/// The download-runner's counterpart of
	/// <see cref="BuildComplianceRunnerServices"/>: the same infrastructure + execution
	/// registrations (<see cref="ServiceCollectionExtensions.AddWaypointExecution"/> and
	/// no more), never <c>AddContentPullReconcileSweep</c> -- which is precisely the
	/// difference issue #1707 is about. It omits the same categories of registration the
	/// class comment lists, plus this host's <c>DownloadRunnerOptions</c> binding and its
	/// <c>JobHandlerRegistry</c> capability override; none of them can register the sweep.
	/// </summary>
	private static ServiceCollection BuildDownloadRunnerServices(IConfiguration configuration)
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);
		return services;
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void ComplianceRunnerHost_RegistersContentPullReconcileHostedServiceOnlyWithConnectionString(bool withConnectionString)
	{
		IConfiguration configuration = BuildConfiguration(withConnectionString);
		ServiceCollection services = BuildComplianceRunnerServices(configuration);

		Assert.Equal(withConnectionString, RegistersContentPullReconcileHostedService(services));

		// The CLASS-killing assertion (issue #1707 round 1): a validated build, not just
		// descriptor inspection, catches "registered but its dependencies are not" -- the
		// exact shape of the empty-configuration regression, where
		// AddContentPullReconcileSweep once registered ContentPullReconcileHostedService
		// unconditionally while AddWaypointExecution's "no connection string, no wiring"
		// guard left ContentPullReconcileService/IContentPullCheckFanOutRepository/
		// IOptions<ContentPullReconcileOptions> unregistered.
		using ServiceProvider provider = services.BuildServiceProvider(ValidatedProviderOptions);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void DownloadRunnerHost_NeverRegistersContentPullReconcileHostedService(bool withConnectionString)
	{
		IConfiguration configuration = BuildConfiguration(withConnectionString);
		ServiceCollection services = BuildDownloadRunnerServices(configuration);

		Assert.False(RegistersContentPullReconcileHostedService(services));

		using ServiceProvider provider = services.BuildServiceProvider(ValidatedProviderOptions);
	}
}
