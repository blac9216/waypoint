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
using Waypoint.Core.Capacity;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Capacity;
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

		services.AddOptions<Waypoint.Core.ComplianceContent.ComplianceContentOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.ComplianceContent.ComplianceContentOptions.SectionName));

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

		// Issue #687: the connected vendor catalog-pull job -- distinct job type from
		// catalog-index above (that one stays local/credential-free, issue #690 AC).
		// Gated on issue #691's depot_enrollment state; registered here as its own
		// handler like every other job type rather than folded into
		// CatalogIndexJobHandler, matching this file's one-handler-per-job-type
		// convention.
		services.AddSingleton<IJobHandler, Catalog.CatalogPullJobHandler>();

		// Download execution always runs behind the ADR-0015 tool-presence gate now
		// that the API no longer hosts an ungated download path (issue #443 removed
		// the Combined/ungated branch this used to have -- see git history on this
		// file for the pre-#443 shape). ToolGatedDownloadJobHandler wraps the concrete
		// DownloadJobHandler; the tool is provisioned at the runner's
		// ManagedTool:ToolStatePath (ADR-0015).
		services.AddSingleton<Downloads.DownloadJobHandler>();
		services.AddSingleton<IJobHandler, Downloads.ToolGatedDownloadJobHandler>();

		// Issue #39 (ADR-0015): the local-repository and manual-upload install paths,
		// unwrapped by ToolGatedDownloadJobHandler's gate above -- an install job must
		// run precisely when the tool is NOT yet present, so it cannot be behind the
		// same presence gate a download job is.
		services.AddSingleton<IJobHandler, Downloads.ManagedToolInstallJobHandler>();

		// Issue #691: assisted Software Depot enrollment (Depot ID generation +
		// Activation Code validation) -- a download-runner job type, since it invokes
		// the same managed vcf-download-tool binary tool-install/depot-fetch already
		// require to be mounted (ADR-0015), never compliance-runner.
		services.AddSingleton<IJobHandler, Downloads.DepotEnrollmentJobHandler>();

		services.AddSingleton<IJobHandler, Discovery.DiscoverJobHandler>();
		services.AddSingleton<IJobHandler, Scans.ScanJobHandler>();
		services.AddSingleton<IJobHandler, Credentials.CredentialTestJobHandler>();

		// Issue #40 (ADR-0017): content-pull is a compliance-runner job type -- see
		// JobCapabilities.Compliance.
		services.AddSingleton<IJobHandler, ComplianceContent.ContentPullJobHandler>();

		// Issue #594 (epic #577): purge deletes a terminal run's on-disk scan-artifact
		// files -- compliance-runner only, see JobCapabilities.Compliance's doc comment.
		services.AddSingleton<IJobHandler, Scans.PurgeJobHandler>();

		// Issue #437 (ADR-0014 §5): resource-aware admission -- every host that calls
		// this method is a dedicated single-process runner bounded by its own
		// container's cgroup allocation, so this is unconditional here (unlike the old
		// Combined/ExecutionOnly branch, there is no longer a non-runner caller of
		// this method to skip it for).
		services.AddSingleton<CgroupResourceDiscovery>();
		services.AddSingleton<ResourceAdmissionController>();

		// Issue #569 (ADR-0018 Option B / ADR-0020): the shared capacity lease pool.
		// Optional by configuration (CapacityPool:Enabled=false keeps ADR-0014 §5's
		// per-runner-only admission); when enabled, the coordinator gates every claim
		// AFTER local admission -- the runner's own discovered/capped budget stays the
		// authoritative upper bound -- and the existing recovery sweep also reaps
		// expired capacity leases. Bound to the same connection string this method
		// already requires: no connection string, no pool (and no dispatcher either).
		services.AddOptions<CapacityPoolOptions>()
			.Bind(configuration.GetSection(CapacityPoolOptions.SectionName));
		bool capacityPoolEnabled = configuration.GetSection(CapacityPoolOptions.SectionName)
			.GetValue(nameof(CapacityPoolOptions.Enabled), defaultValue: true);
		if (capacityPoolEnabled)
		{
			services.AddSingleton<IHostCapabilitySource, SystemHostCapabilitySource>();
			services.AddSingleton<ICapacityLeasePool>(new CapacityLeasePoolRepository(connectionString));
			services.AddSingleton<CapacityLeaseCoordinator>();
			services.AddHostedService<CapacityPoolRegistrationHostedService>();
		}

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
			serviceProvider.GetService<CapacityLeaseCoordinator>(),
			serviceProvider.GetRequiredService<ILogger<JobDispatcherHostedService>>()));
		services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobDispatcherHostedService>());
		services.AddHostedService(serviceProvider => new LeaseRecoveryHostedService(
			serviceProvider.GetRequiredService<IJobRunnerRepository>(),
			serviceProvider.GetRequiredService<IJobEventPublisher>(),
			serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
			serviceProvider.GetService<ICapacityLeasePool>(),
			serviceProvider.GetRequiredService<ILogger<LeaseRecoveryHostedService>>()));

		return services;
	}
}
