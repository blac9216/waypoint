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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Resources;

namespace Waypoint.Infrastructure.Execution.DependencyInjection;

/// <summary>
/// Issue #443 (ADR-0013 §1): the execution-only composition root, the sibling of
/// <c>Waypoint.Infrastructure.DependencyInjection.ServiceCollectionExtensions
/// .AddWaypointInfrastructure</c>. Only <c>Waypoint.ComplianceRunner</c> and
/// <c>Waypoint.DownloadRunner</c> call this method -- <c>Waypoint.Api</c> has no
/// reference to this assembly at all, so it structurally cannot resolve a
/// <see cref="JobHandlerRegistry"/>, a PowerShell runspace, or a dispatcher, no matter
/// what configuration it is started with. That is the property this issue calls
/// "reactivation must be structurally impossible" -- there is no dormant registration
/// to flip back on, because the code implementing it is not linked into the API's
/// output at all.
///
/// Callers must call <c>AddWaypointInfrastructure</c> first (this method assumes the
/// control-plane repositories, options, and connection-string-gated registrations it
/// provides already exist in <paramref name="services"/>) and must register their own
/// <see cref="JobHandlerRegistry"/> afterwards, narrowed to their own
/// <see cref="JobCapabilities"/> allowlist by filtering the <see cref="IJobHandler"/>
/// instances this method registers -- see <c>Waypoint.ComplianceRunner.Program</c> and
/// <c>Waypoint.DownloadRunner.Program</c> for the exact pattern, unchanged from what
/// <c>AddWaypointInfrastructure(..., WaypointInfrastructureHostKind.ExecutionOnly)</c>
/// did before this split.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// Registers the PowerShell host, every domain <see cref="IJobHandler"/>, the
	/// dispatcher/lease-recovery hosted services, the job-log buffer, and resource
	/// admission. No-ops (registers nothing job-shaped) if
	/// <c>ConnectionStrings:Waypoint</c> is absent, mirroring
	/// <c>AddWaypointInfrastructure</c>'s own "no connection string, no wiring" guard --
	/// a runner with no database configured has nothing to dispatch from.
	/// </summary>
	public static IServiceCollection AddWaypointExecution(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<RunnerResourceOptions>()
			.Bind(configuration.GetSection(RunnerResourceOptions.SectionName));

		services.AddOptions<PowerShellOptions>()
			.Bind(configuration.GetSection(PowerShellOptions.SectionName));

		string? connectionString = configuration.GetConnectionString(
			Waypoint.Infrastructure.DependencyInjection.ServiceCollectionExtensions.ConnectionStringName);
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			return services;
		}

		// The job.log buffer and the PS host that streams into it are both
		// execution-only: nothing in the control plane writes to IJobLogBuffer (see
		// BufferedJobEventWriter's doc comment -- "a PowerShell stream callback must
		// not block on Postgres").
		services.AddSingleton<BufferedJobEventWriter>(serviceProvider => new BufferedJobEventWriter(
			connectionString,
			serviceProvider.GetRequiredService<Waypoint.Core.Logging.ISecretRedactor>(),
			serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
			serviceProvider.GetRequiredService<ILogger<BufferedJobEventWriter>>()));
		services.AddSingleton<IJobLogBuffer>(serviceProvider => serviceProvider.GetRequiredService<BufferedJobEventWriter>());
		services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BufferedJobEventWriter>());

		services.AddSingleton<PowerShell.WaypointRunspacePool>();
		services.AddSingleton<IPowerShellExecutor, PowerShell.PowerShellExecutor>();

		// The first production job handler registration (issue #194, epic #9 slice 2)
		// -- constructed and registered here, not self-registering, the same pattern
		// PowerShellJobHandler's doc comment describes for the real job types
		// (jobs.job_type is a closed CHECK set with no generic member).
		services.AddSingleton<IJobHandler, Catalog.CatalogIndexJobHandler>();

		// Download execution always runs behind the ADR-0015 tool-presence gate now
		// that the API no longer hosts an ungated download path (issue #443 removed
		// the Combined/ungated branch this used to have -- see git history on this
		// file for the pre-#443 shape). ToolGatedDownloadJobHandler wraps the concrete
		// DownloadJobHandler; the tool is provisioned at the runner's
		// ManagedTool:ToolStatePath (ADR-0015).
		services.AddSingleton<Downloads.DownloadJobHandler>();
		services.AddSingleton<IJobHandler, Downloads.ToolGatedDownloadJobHandler>();

		services.AddSingleton<IJobHandler, Discovery.DiscoverJobHandler>();
		services.AddSingleton<IJobHandler, Scans.ScanJobHandler>();
		services.AddSingleton<IJobHandler, Credentials.CredentialTestJobHandler>();

		// Issue #437 (ADR-0014 §5): resource-aware admission -- every host that calls
		// this method is a dedicated single-process runner bounded by its own
		// container's cgroup allocation, so this is unconditional here (unlike the old
		// Combined/ExecutionOnly branch, there is no longer a non-runner caller of
		// this method to skip it for).
		services.AddSingleton<CgroupResourceDiscovery>();
		services.AddSingleton<ResourceAdmissionController>();

		// Issue #436: JobHandlerRegistry is the mandatory capability-registration
		// point (fails closed on an empty allowlist or a duplicate handler -- see its
		// doc comment). This default (JobCapabilities.All) is only ever a placeholder:
		// every real caller of this method re-registers JobHandlerRegistry afterwards
		// with its own narrower allowlist (see this class's doc comment), the same
		// pattern the pre-split ExecutionOnly host kind used.
		services.AddSingleton(serviceProvider => new JobHandlerRegistry(
			serviceProvider.GetServices<IJobHandler>(), JobCapabilities.All));

		services.AddSingleton<JobDispatcherHostedService>(serviceProvider => new JobDispatcherHostedService(
			serviceProvider.GetRequiredService<IJobRunnerRepository>(),
			serviceProvider.GetRequiredService<IJobControlRepository>(),
			serviceProvider.GetRequiredService<IJobEventPublisher>(),
			serviceProvider.GetRequiredService<JobHandlerRegistry>(),
			serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
			serviceProvider.GetRequiredService<ResourceAdmissionController>(),
			serviceProvider.GetRequiredService<ILogger<JobDispatcherHostedService>>()));
		services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobDispatcherHostedService>());
		services.AddHostedService<LeaseRecoveryHostedService>();

		return services;
	}
}
