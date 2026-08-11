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
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.ComplianceRunner;

/// <summary>
/// Issue #440 AC1: "Discovery, credential-test, and scan jobs execute only in the
/// compliance runner." This test exercises the exact composition <c>Program.cs</c>
/// performs: call <see cref="ServiceCollectionExtensions.AddWaypointInfrastructure"/>
/// (the same call <c>Waypoint.Api</c> makes) and then re-register
/// <see cref="JobHandlerRegistry"/> narrowed to <see cref="JobCapabilities.Compliance"/>,
/// filtering <see cref="IJobHandler"/> instances by <c>JobType</c> rather than by
/// concrete type -- so a future compliance handler is included automatically and a
/// download handler is excluded automatically, with no per-handler-type list to
/// maintain here or drift from <c>Program.cs</c>.
///
/// No live Postgres is needed: like <c>JobEngineWiringTests</c>, this only proves DI
/// registrations resolve to the right types/counts, never opening a connection.
/// </summary>
public sealed class ComplianceRunnerCompositionTests
{
	[Fact]
	public void HandlerRegistry_ResolvesOnlyComplianceJobTypes()
	{
		JobHandlerRegistry registry = BuildComplianceHandlerRegistry();

		Assert.Equal(JobCapabilities.Compliance, registry.AllowedJobTypes);

		Assert.True(registry.TryResolve("discover", out _));
		Assert.True(registry.TryResolve("credential-test", out _));
		Assert.True(registry.TryResolve("scan", out _));
	}

	[Theory]
	[InlineData("catalog-index")]
	[InlineData("download")]
	public void HandlerRegistry_DoesNotResolveDownloadJobTypes(string downloadJobType)
	{
		JobHandlerRegistry registry = BuildComplianceHandlerRegistry();

		Assert.False(registry.TryResolve(downloadJobType, out IJobHandler? handler));
		Assert.Null(handler);
	}

	[Fact]
	public void AllowedJobTypes_NeverIncludesADownloadCapability()
	{
		JobHandlerRegistry registry = BuildComplianceHandlerRegistry();

		foreach (string downloadJobType in JobCapabilities.Download)
		{
			Assert.DoesNotContain(downloadJobType, registry.AllowedJobTypes);
		}
	}

	/// <summary>Mirrors <c>Waypoint.ComplianceRunner.Program</c>'s composition exactly.</summary>
	private static JobHandlerRegistry BuildComplianceHandlerRegistry()
	{
		List<KeyValuePair<string, string?>> settings =
		[
			new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
		];
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);

		services.AddSingleton(serviceProvider =>
		{
			IEnumerable<IJobHandler> complianceHandlers = serviceProvider
				.GetServices<IJobHandler>()
				.Where(handler => JobCapabilities.Compliance.Contains(handler.JobType));
			return new JobHandlerRegistry(complianceHandlers, JobCapabilities.Compliance);
		});

		using ServiceProvider provider = services.BuildServiceProvider();
		return provider.GetRequiredService<JobHandlerRegistry>();
	}
}
