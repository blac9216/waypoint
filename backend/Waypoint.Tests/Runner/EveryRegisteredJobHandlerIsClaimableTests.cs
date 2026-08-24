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
using Waypoint.DownloadRunner;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Infrastructure.Execution.DependencyInjection;
using Xunit;

namespace Waypoint.Tests.Runner;

/// <summary>
/// Issue #619's convention test: "handler-registered-but-never-claimable must die."
/// <c>ManagedToolInstallJobHandler</c> registered with <c>JobType == "tool-install"</c>
/// back in #39/#602, but neither runner host's actual claim allowlist
/// (<c>Waypoint.ComplianceRunner.Program</c>'s <see cref="JobCapabilities.Compliance"/>
/// or <see cref="DownloadRunnerJobTypes.Allowed"/>) was updated to include it -- so
/// every <c>tool-install</c> job queued successfully (the API only checks
/// <c>jobs_job_type_check</c>, a schema-level allowlist, not "does any runner actually
/// claim this") and then sat <c>queued</c> forever, because
/// <c>Waypoint.Runner.Jobs.JobDispatcherHostedService</c> only ever claims a job whose
/// type is in the host's own <c>JobHandlerRegistry.AllowedJobTypes</c>.
///
/// This test exercises <see cref="ServiceCollectionExtensions.AddWaypointExecution"/>
/// exactly as both <c>Program.cs</c> files do (no live Postgres -- like
/// <c>ComplianceRunnerCompositionTests</c>, only DI registrations are resolved) and
/// asserts every <see cref="IJobHandler"/> it registers has a <c>JobType</c> claimable
/// by at least one of the two runner hosts' real allowlists. A future handler that
/// registers but is never added to either host's allowlist now fails this test
/// immediately, instead of silently queuing jobs no runner will ever claim.
/// </summary>
public sealed class EveryRegisteredJobHandlerIsClaimableTests
{
	[Fact]
	public void EveryRegisteredHandlersJobType_IsClaimableBySomeRunnerHost()
	{
		List<KeyValuePair<string, string?>> settings =
		[
			new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
		];
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);

		using ServiceProvider provider = services.BuildServiceProvider();
		IEnumerable<IJobHandler> registeredHandlers = provider.GetServices<IJobHandler>();

		HashSet<string> claimableByAnyHost = new(StringComparer.Ordinal);
		claimableByAnyHost.UnionWith(JobCapabilities.Compliance);
		claimableByAnyHost.UnionWith(DownloadRunnerJobTypes.Allowed);

		List<string> unclaimable = registeredHandlers
			.Select(handler => handler.JobType)
			.Where(jobType => !claimableByAnyHost.Contains(jobType))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(jobType => jobType, StringComparer.Ordinal)
			.ToList();

		Assert.True(
			unclaimable.Count == 0,
			$"Handler(s) registered for job type(s) [{string.Join(", ", unclaimable)}] are never claimable by " +
			"either runner host -- add the job type to JobCapabilities.Compliance (Waypoint.ComplianceRunner.Program) " +
			"or DownloadRunnerJobTypes.Allowed (Waypoint.DownloadRunner), whichever domain owns it, in the same " +
			"change that registers the handler. See issue #619.");
	}

	/// <summary>
	/// The converse direction, so a typo in either allowlist that names a job type with
	/// no registered handler at all is also caught -- <c>JobDispatcherHostedService</c>
	/// would throw "no handler registered" the first time such a type were claimed
	/// (see <c>DownloadRunnerJobTypes</c>'s own doc comment), which is exactly why
	/// <c>DownloadRunnerJobTypes.Allowed</c> is deliberately narrower than
	/// <see cref="JobCapabilities.Download"/>.
	/// </summary>
	[Fact]
	public void DownloadRunnerAllowlist_NamesOnlyJobTypesWithARegisteredHandler()
	{
		List<KeyValuePair<string, string?>> settings =
		[
			new("ConnectionStrings:Waypoint", "Host=127.0.0.1;Port=5432;Database=waypoint_test;Username=u;Password=p"),
		];
		IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

		ServiceCollection services = new();
		services.AddLogging();
		services.AddWaypointInfrastructure(configuration);
		services.AddWaypointExecution(configuration);

		using ServiceProvider provider = services.BuildServiceProvider();
		HashSet<string> registeredJobTypes = new(
			provider.GetServices<IJobHandler>().Select(handler => handler.JobType),
			StringComparer.Ordinal);

		foreach (string jobType in DownloadRunnerJobTypes.Allowed)
		{
			Assert.True(
				registeredJobTypes.Contains(jobType),
				$"DownloadRunnerJobTypes.Allowed names '{jobType}' but no IJobHandler is registered for it -- " +
				"the dispatcher would throw \"no handler registered\" the first time it is claimed.");
		}
	}
}
