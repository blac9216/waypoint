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
using Microsoft.Extensions.Options;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Execution.DependencyInjection;
using Xunit;

namespace Waypoint.Tests.Infrastructure;

/// <summary>
/// The composition root's "no connection string, no wiring" guard, both ways.
///
/// The negative half is the one that matters and the one nothing covered: the
/// <c>WebApplicationFactory</c> suite runs without a connection string, so every existing
/// test exercised only the branch where the job engine is *not* registered. A regression
/// that wired the engine unconditionally would therefore have gone unnoticed until a
/// deployment without a database crashed on startup -- and no test would have moved.
///
/// Issue #443: <c>AddWaypointInfrastructure</c> is now control-plane only (repositories,
/// enqueue/control/query) -- these tests exercise it alone, matching exactly what
/// <c>Waypoint.Api.Program</c> calls. <see cref="ExecutionCompositionTests"/> covers the
/// <c>AddWaypointExecution</c> half a runner host adds on top.
/// </summary>
public sealed class JobEngineWiringTests
{
	[Fact]
	public void WithAConnectionString_TheQueueRepositoryAndEventPublisherAreRegistered()
	{
		using ServiceProvider provider = BuildProvider("Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p");

		Assert.NotNull(provider.GetService<IJobControlRepository>());
		Assert.NotNull(provider.GetService<IJobRunnerRepository>());
		Assert.NotNull(provider.GetService<IJobEventPublisher>());
	}

	[Fact]
	public void WithoutAConnectionString_NeitherIsRegistered()
	{
		using ServiceProvider provider = BuildProvider(connectionString: null);

		Assert.Null(provider.GetService<IJobControlRepository>());
		Assert.Null(provider.GetService<IJobRunnerRepository>());
		Assert.Null(provider.GetService<IJobEventPublisher>());
	}

	/// <summary>
	/// A blank connection string is treated as absent rather than as a value to hand to
	/// Npgsql, which would fail later and further away.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void WithABlankConnectionString_NeitherIsRegistered(string connectionString)
	{
		using ServiceProvider provider = BuildProvider(connectionString);

		Assert.Null(provider.GetService<IJobControlRepository>());
		Assert.Null(provider.GetService<IJobRunnerRepository>());
		Assert.Null(provider.GetService<IJobEventPublisher>());
	}

	/// <summary>
	/// <see cref="JobEngineOptions"/> binds from the <c>JobEngine</c> configuration
	/// section. Pinned here because the section name is a string that appears in
	/// <c>appsettings.json</c> and in code, and a mismatch between them fails silently:
	/// every option quietly keeps its default and the engine looks configured.
	/// </summary>
	[Fact]
	public void JobEngineOptions_BindFromTheJobEngineSection()
	{
		using ServiceProvider provider = BuildProvider(
			connectionString: null,
			("JobEngine:MaxConcurrency", "11"),
			("JobEngine:ConsecutiveAuthFailureThreshold", "5"),
			("JobEngine:EventCommandTimeoutSeconds", "9"));

		JobEngineOptions options = provider.GetRequiredService<IOptions<JobEngineOptions>>().Value;

		Assert.Equal(11, options.MaxConcurrency);
		Assert.Equal(5, options.ConsecutiveAuthFailureThreshold);
		Assert.Equal(9, options.EventCommandTimeoutSeconds);
	}

	/// <summary>
	/// Issue #443: the control-plane composition alone registers no <see cref="IJobHandler"/>
	/// at all -- there is nothing left in <c>Waypoint.Infrastructure</c> that implements
	/// one; every handler moved to <c>Waypoint.Infrastructure.Execution</c>. This is the
	/// unit-level half of the composition proof
	/// <c>Waypoint.Tests.Api.ApiProcessHostsNoExecutionTests</c> makes at the whole-API-host
	/// level.
	/// </summary>
	[Fact]
	public void ControlPlaneAlone_RegistersNoJobHandlers()
	{
		using ServiceProvider provider = BuildProvider("Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p");

		Assert.Empty(provider.GetServices<IJobHandler>());
	}

	/// <summary>
	/// Issue #443 regression: <c>AddWaypointInfrastructure</c> ALONE (without the
	/// separate <c>AddWaypointApiSurface</c> call) must register neither
	/// <see cref="IJobEventFeed"/> nor <c>RunSecretCleanupHostedService</c> -- both
	/// runners call exactly this method (for the control-plane repositories they share
	/// with the API) and neither database role has the <c>job_events</c> SELECT grant
	/// <see cref="IJobEventFeed"/>'s poll loop needs (migration 0025). An earlier
	/// revision of this file registered both unconditionally inside
	/// <c>AddWaypointInfrastructure</c> itself; that passed every unit test in this
	/// file (which only ever asserted on job-queue/handler types, never on these two)
	/// and was only caught by bringing up compliance-runner against real Postgres and
	/// reading a permission-denied error in its logs -- see
	/// <c>AddWaypointApiSurface</c>'s doc comment for the full story. Pinned here so a
	/// future change cannot silently reintroduce it.
	/// </summary>
	[Fact]
	public void ControlPlaneAlone_RegistersNeitherJobEventFeedNorRunSecretCleanup()
	{
		using ServiceProvider provider = BuildProvider("Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p");

		Assert.Null(provider.GetService<IJobEventFeed>());
		Assert.DoesNotContain(
			provider.GetServices<IHostedService>(),
			service => service.GetType().Name == "RunSecretCleanupHostedService" || service.GetType().Name == "JobEventStreamService");
	}

	private static ServiceProvider BuildProvider(string? connectionString, params (string Key, string Value)[] settings)
	{
		List<KeyValuePair<string, string?>> values = [.. settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value))];
		if (connectionString is not null)
		{
			values.Add(new KeyValuePair<string, string?>("ConnectionStrings:Waypoint", connectionString));
		}

		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		return services.BuildServiceProvider();
	}
}

/// <summary>
/// Issue #443: <c>AddWaypointApiSurface</c>, the API-only half split out of
/// <c>AddWaypointInfrastructure</c> after the live-stack verification described in that
/// method's doc comment. Only <c>Waypoint.Api.Program</c> calls this.
/// </summary>
public sealed class ApiSurfaceCompositionTests
{
	[Fact]
	public void RegistersJobEventFeedAndRunSecretCleanup()
	{
		List<KeyValuePair<string, string?>> values =
		[
			new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
		];
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointApiSurface(configuration);
		using ServiceProvider provider = services.BuildServiceProvider();

		Assert.NotNull(provider.GetService<IJobEventFeed>());
		Assert.Contains(
			provider.GetServices<IHostedService>(),
			service => service.GetType().Name == "RunSecretCleanupHostedService");
		Assert.Contains(
			provider.GetServices<IHostedService>(),
			service => service.GetType().Name == "JobEventStreamService");
	}

	[Fact]
	public void WithoutAConnectionString_RegistersNeither()
	{
		IConfiguration configuration = new ConfigurationBuilder().Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointApiSurface(configuration);
		using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Null(provider.GetService<IJobEventFeed>());
	}
}

/// <summary>
/// Issue #443: the <c>AddWaypointExecution</c> half of the composition, mirroring what
/// <c>Waypoint.ComplianceRunner.Program</c>/<c>Waypoint.DownloadRunner.Program</c> do --
/// call <c>AddWaypointInfrastructure</c> first, then <c>AddWaypointExecution</c> on top.
/// Which concrete <c>download</c> handler resolves is load-bearing: since #443 removed
/// the API's ungated download path, every caller of <c>AddWaypointExecution</c> gets the
/// ADR-0015 tool-gated handler unconditionally -- there is no more Combined/ungated
/// branch to choose between.
/// </summary>
public sealed class ExecutionCompositionTests
{
	[Fact]
	public void RegistersTheToolGatedDownloadHandler()
	{
		using ServiceProvider provider = BuildProvider();

		List<IJobHandler> download = [.. provider.GetServices<IJobHandler>().Where(handler => handler.JobType == "download")];
		IJobHandler handler = Assert.Single(download);
		Assert.IsType<ToolGatedDownloadJobHandler>(handler);
	}

	[Fact]
	public void RegistersEveryDomainHandler()
	{
		using ServiceProvider provider = BuildProvider();

		HashSet<string> jobTypes = [.. provider.GetServices<IJobHandler>().Select(handler => handler.JobType)];
		Assert.Equal(
			new HashSet<string>
			{
				"catalog-index", "catalog-pull", "binaries-download", "download", "tool-install", "depot-enrollment", "retention-sweep", "discover", "scan", "credential-test", "content-pull", "content-check", "purge",
			},
			jobTypes);
	}

	/// <summary>
	/// PR #763 class-killing guard (issue #687 review round 1): the production container
	/// must be able to construct <c>CatalogPullJobHandler</c> with the REAL
	/// <see cref="IManagedToolCatalogVerifier"/> satisfying its catalog-only
	/// <see cref="IManagedToolCatalogVerifier.AuthenticateCatalogAsync"/> abstraction --
	/// resolving the handler forces its constructor to run, so a future change that (say)
	/// drops the verifier registration, or a signature change that leaves the handler
	/// depending on a type the container cannot supply, breaks HERE and loudly rather than
	/// only at runtime on a real pull. This is the cheap guard the reviewer asked for after
	/// the fake-verifier E2E hid the dead production authentication path (Finding 1/2). The
	/// bound verifier is asserted to be the concrete production type so the guard cannot be
	/// satisfied by an accidentally-registered test double.
	/// </summary>
	[Fact]
	public void ResolvesCatalogPullHandlerWithTheRealCatalogVerifier()
	{
		using ServiceProvider provider = BuildProvider();

		IJobHandler handler = Assert.Single(provider.GetServices<IJobHandler>().Where(h => h.JobType == "catalog-pull"));
		Assert.IsType<Waypoint.Infrastructure.Catalog.CatalogPullJobHandler>(handler);

		IManagedToolCatalogVerifier verifier = provider.GetRequiredService<IManagedToolCatalogVerifier>();
		Assert.IsType<BroadcomManagedToolCatalogVerifier>(verifier);
	}

	/// <summary>
	/// Issue #443 regression -- see <c>ApiSurfaceCompositionTests</c> and
	/// <c>AddWaypointApiSurface</c>'s doc comment for the full story: a runner
	/// composition (<c>AddWaypointInfrastructure</c> + <c>AddWaypointExecution</c>, with
	/// no <c>AddWaypointApiSurface</c> call, exactly what
	/// <c>Waypoint.ComplianceRunner.Program</c>/<c>Waypoint.DownloadRunner.Program</c> do)
	/// must never register <see cref="IJobEventFeed"/> or the run-secret cleanup sweep.
	/// </summary>
	[Fact]
	public void DoesNotRegisterJobEventFeedOrRunSecretCleanup()
	{
		using ServiceProvider provider = BuildProvider();

		Assert.Null(provider.GetService<IJobEventFeed>());
		Assert.DoesNotContain(
			provider.GetServices<IHostedService>(),
			service => service.GetType().Name == "RunSecretCleanupHostedService" || service.GetType().Name == "JobEventStreamService");
	}

	private static ServiceProvider BuildProvider()
	{
		List<KeyValuePair<string, string?>> values =
		[
			new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
		];
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);
		return services.BuildServiceProvider();
	}
}
