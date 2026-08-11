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
using Waypoint.Core.Auth;
using Waypoint.Core.Catalog;
using Waypoint.Core.Configuration;
using Waypoint.Core.Discovery;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Auth;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Sites;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Jobs;

namespace Waypoint.Infrastructure.DependencyInjection;

/// <summary>
/// Issue #441: which control-plane-only hosted services
/// <see cref="ServiceCollectionExtensions.AddWaypointInfrastructure(IServiceCollection, IConfiguration, WaypointInfrastructureHostKind)"/>
/// starts alongside the shared repositories/PowerShell host/job dispatcher every host
/// kind needs. <see cref="Combined"/> is today's <c>Waypoint.Api</c> behavior
/// (unchanged); <see cref="ExecutionOnly"/> is for a dedicated runner host (e.g.
/// download-runner) that has no SSE surface or run/scan endpoints of its own.
/// </summary>
public enum WaypointInfrastructureHostKind
{
	/// <summary>Today's single combined process: control plane + execution, every hosted service starts.</summary>
	Combined,

	/// <summary>A dedicated execution-only runner host: skips API-only hosted services (SSE fan-out, run-secret cleanup).</summary>
	ExecutionOnly
}

/// <summary>
/// Composition-root entry point for everything this project provides. Postgres access
/// lands with the schema (issue #4) as a small raw-SQL migrations pipeline
/// (<see cref="ISchemaMigrator"/>), not an ORM — see that type's doc comment for why.
/// The job engine's queue primitives (issue #128: the claim/lease repository and the
/// job_events write path) are wired here too, behind the same "no connection string, no
/// wiring" guard as the migrator. The dispatcher and lease-recovery hosted services land
/// with #129; PowerShell runspace hosting lands with #6.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// The standard ASP.NET Core connection-string slot this project reads the
	/// database connection from (<c>ConnectionStrings:Waypoint</c>).
	/// </summary>
	public const string ConnectionStringName = "Waypoint";

	public static IServiceCollection AddWaypointInfrastructure(this IServiceCollection services, IConfiguration configuration) =>
		services.AddWaypointInfrastructure(configuration, WaypointInfrastructureHostKind.Combined);

	/// <summary>
	/// Issue #441: the additive seam a dedicated runner host (download-runner today;
	/// a future compliance-runner split takes the same seam) uses to reuse this
	/// project's whole composition root without also starting the two hosted services
	/// that are ADR-0013 §1 control-plane concerns, not execution concerns --
	/// <c>JobEventStreamService</c> (SSE fan-out; the API is the only SSE surface) and
	/// <c>RunSecretCleanupHostedService</c> (the ad hoc "my credentials" run-secret
	/// sweep, meaningful only alongside the API's run/scan endpoints). Everything else
	/// -- repositories, the PowerShell host, the job dispatcher, lease recovery, the
	/// event writer -- is identical for every <see cref="WaypointInfrastructureHostKind"/>;
	/// only <see cref="JobHandlerRegistry"/>'s allowlist and <see cref="IJobHandler"/>
	/// set differ by which handlers a given runner registers on top of this call.
	/// The default overload above keeps today's combined behavior unchanged for
	/// <c>Waypoint.Api</c> and every existing test.
	/// </summary>
	public static IServiceCollection AddWaypointInfrastructure(
		this IServiceCollection services, IConfiguration configuration, WaypointInfrastructureHostKind hostKind)
	{
		services.AddOptions<LocalAuthOptions>()
			.Bind(configuration.GetSection(LocalAuthOptions.SectionName));

		// Issue #333: resolves AdminPasswordHash from a mounted file over the legacy
		// env var. Registered as IPostConfigureOptions so it runs after the Bind above
		// regardless of options-pipeline ordering.
		services.AddSingleton<IPostConfigureOptions<LocalAuthOptions>, Waypoint.Core.Auth.LocalAuthOptionsPostConfigure>();

		services.AddOptions<WaypointBuildOptions>()
			.Bind(configuration.GetSection(WaypointBuildOptions.SectionName));

		services.AddOptions<WaypointDatabaseOptions>()
			.Bind(configuration.GetSection(WaypointDatabaseOptions.SectionName));

		services.AddOptions<JobEngineOptions>()
			.Bind(configuration.GetSection(JobEngineOptions.SectionName));

		services.AddOptions<PowerShellOptions>()
			.Bind(configuration.GetSection(PowerShellOptions.SectionName));

		services.AddOptions<CatalogOptions>()
			.Bind(configuration.GetSection(CatalogOptions.SectionName));

		services.AddOptions<DownloadOptions>()
			.Bind(configuration.GetSection(DownloadOptions.SectionName));

		services.AddOptions<Waypoint.Core.Downloads.ManagedToolOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.Downloads.ManagedToolOptions.SectionName));

		services.AddOptions<DiscoveryOptions>()
			.Bind(configuration.GetSection(DiscoveryOptions.SectionName));

		services.AddOptions<Waypoint.Core.Scans.ScanOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.Scans.ScanOptions.SectionName));

		services.AddOptions<Waypoint.Core.StigManager.StigManagerClientOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.StigManager.StigManagerClientOptions.SectionName));

		services.AddOptions<Waypoint.Core.Secrets.RunSecretOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.Secrets.RunSecretOptions.SectionName));

		services.AddSingleton<ILocalAuthenticationService, InMemoryLocalAuthenticationService>();

		// One scrubber instance serves both sides of security.md control 1: sinks read
		// through ISecretRedactor, decrypting code registers through ISecretTracker.
		services.AddSingleton<InPlaySecretRedactor>();
		services.AddSingleton<ISecretRedactor>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());
		services.AddSingleton<ISecretTracker>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());

		// Issue #436: JobHandlerRegistry is the mandatory capability-registration
		// point (fails closed on an empty allowlist or a duplicate handler -- see its
		// doc comment). Waypoint.Api still hosts both execution domains combined
		// today, so it registers JobCapabilities.All; a split compliance-runner/
		// download-runner registers its own narrower set through the same
		// constructor instead.
		services.AddSingleton(serviceProvider => new JobHandlerRegistry(
			serviceProvider.GetServices<IJobHandler>(), JobCapabilities.All));

		// Disk usage is a filesystem stat, not a database read -- registered
		// unconditionally so GET /system still reports store usage on a host with no
		// connection string configured (issue #226).
		services.AddSingleton<Waypoint.Core.SystemState.IArtifactStoreDiskUsageProvider, ArtifactStoreDiskUsageProvider>();

		// Issue #441: a filesystem stat like the disk-usage provider above -- no
		// connection string dependency, registered unconditionally so a host with no
		// database configured can still answer capability/readiness questions about
		// the managed-tool mount.
		services.AddSingleton<Waypoint.Core.Downloads.IManagedToolPresenceChecker, Downloads.ManagedToolPresenceChecker>();

		// ADR-0005 crypto core (epic #8 slice 1). Registered unconditionally: the
		// provider is lazy and fail-closed, so a host without a mounted key boots
		// fine and refuses secret operations with an operator-actionable error.
		services.AddSingleton<Waypoint.Core.Secrets.IMasterKeyProvider>(new Secrets.FileMasterKeyProvider());
		services.AddSingleton<Waypoint.Core.Secrets.IEnvelopeCipher, Secrets.AesGcmEnvelopeCipher>();

		// Issue #310: the STIG Manager reachability probe's HTTP boundary. Registered
		// unconditionally (like the crypto core above) -- IHttpClientFactory itself
		// needs no connection string, only the repository below does.
		services.AddHttpClient();
		services.AddSingleton<Waypoint.Core.StigManager.IStigManagerProbe, StigManager.HttpStigManagerProbe>();

		// Issue #311: the CKL upload + benchmark enrichment HTTP boundary, same
		// unconditional registration as the #310 probe above (stubbed in tests, no
		// connection string dependency of its own).
		services.AddSingleton<Waypoint.Core.StigManager.IStigManagerUploadClient, StigManager.HttpStigManagerUploadClient>();

		string? connectionString = configuration.GetConnectionString(ConnectionStringName);
		if (!string.IsNullOrWhiteSpace(connectionString))
		{
			services.AddSingleton<ISchemaMigrator>(serviceProvider => new NpgsqlSchemaMigrator(
				connectionString,
				serviceProvider.GetRequiredService<ILogger<NpgsqlSchemaMigrator>>()));

			// Issue #415: one JobQueueRepository singleton satisfies both focused
			// interfaces the ADR-0013/0014 process boundary calls for --
			// IJobControlRepository (API enqueue/control/query) and IJobRunnerRepository
			// (runner claim/lease/state/recovery). Registering the concrete instance once
			// and exposing it under both interface types keeps every consumer's
			// constructor scoped to only the operations it owns without duplicating the
			// underlying SQL/transaction implementation.
			services.AddSingleton(serviceProvider => new JobQueueRepository(
				connectionString,
				serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>(),
				serviceProvider.GetRequiredService<IJobEventPublisher>()));
			services.AddSingleton<IJobControlRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());
			services.AddSingleton<IJobRunnerRepository>(serviceProvider => serviceProvider.GetRequiredService<JobQueueRepository>());

			services.AddSingleton<IJobEventPublisher>(serviceProvider =>
			{
				JobEngineOptions options = serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>().Value;
				return new JobEventPublisher(
					connectionString,
					options.EventCommandTimeoutSeconds,
					serviceProvider.GetRequiredService<ISecretRedactor>(),
					serviceProvider.GetRequiredService<ILogger<JobEventPublisher>>());
			});

			services.AddSingleton<BufferedJobEventWriter>(serviceProvider => new BufferedJobEventWriter(
				connectionString,
				serviceProvider.GetRequiredService<ISecretRedactor>(),
				serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
				serviceProvider.GetRequiredService<ILogger<BufferedJobEventWriter>>()));
			services.AddSingleton<IJobLogBuffer>(serviceProvider => serviceProvider.GetRequiredService<BufferedJobEventWriter>());
			services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BufferedJobEventWriter>());

			// The PS host needs the job.log buffer for stream capture, so it lives in
			// the same connection-string gate as the rest of the engine.
			services.AddSingleton<PowerShell.WaypointRunspacePool>();
			services.AddSingleton<IPowerShellExecutor, PowerShell.PowerShellExecutor>();

			services.AddSingleton(new Secrets.CredentialRepository(connectionString));
			services.AddSingleton<IDepotArtifactRepository>(new DepotArtifactRepository(connectionString));
			services.AddSingleton<IDownloadRepository>(new DownloadRepository(connectionString));
			services.AddSingleton<Waypoint.Core.SystemState.IApplianceStateRepository>(new ApplianceStateRepository(connectionString));
			services.AddSingleton(new SiteRepository(connectionString));
			services.AddSingleton(new TargetRepository(connectionString));
			services.AddSingleton(new InventoryRepository(connectionString));
			services.AddSingleton(new ConfigDocRepository(connectionString));
			services.AddSingleton(new AttestationSnapshotRepository(connectionString));
			services.AddSingleton(new StigManager.StigManagerRepository(connectionString));
			services.AddSingleton<Waypoint.Core.Secrets.ICredentialSecretStore>(serviceProvider => new Secrets.CredentialSecretStore(
				connectionString,
				serviceProvider.GetRequiredService<Waypoint.Core.Secrets.IEnvelopeCipher>(),
				serviceProvider.GetRequiredService<ISecretTracker>(),
				serviceProvider.GetRequiredService<ILogger<Secrets.CredentialSecretStore>>()));

			// Issue #188: POST /credentials with a secret commits the metadata row and
			// the secret atomically -- see CredentialCreationCoordinator's doc comment.
			services.AddSingleton<Waypoint.Core.Secrets.ICredentialCreationCoordinator>(serviceProvider => new Secrets.CredentialCreationCoordinator(
				connectionString,
				serviceProvider.GetRequiredService<Waypoint.Core.Secrets.IEnvelopeCipher>(),
				serviceProvider.GetRequiredService<ILogger<Secrets.CredentialCreationCoordinator>>()));

			// ADR-0011 ad hoc "my credentials" flow, issue #434: replaces the
			// process-memory-only IEphemeralCredentialCache with encrypted, run-scoped
			// Postgres state (run_secrets, migration 0023) -- registered here rather than
			// unconditionally because, unlike the predecessor cache, it needs the
			// connection string from the moment it is constructed (no best-effort no-op
			// mode; a host with no Postgres configured has no run/scan endpoints wired
			// either, so this dependency is never actually missing in practice).
			services.AddSingleton<Waypoint.Core.Secrets.IRunSecretStore>(serviceProvider => new Secrets.RunSecretStore(
				connectionString,
				serviceProvider.GetRequiredService<Waypoint.Core.Secrets.IEnvelopeCipher>(),
				serviceProvider.GetRequiredService<ISecretTracker>(),
				serviceProvider.GetRequiredService<ILogger<Secrets.RunSecretStore>>()));

			// Issue #311: the convert-stage upload/enrichment coordinator and the
			// retry route (JobsController) share this one instance.
			services.AddSingleton<Scans.ScanUploadCoordinator>();

			// Issue #441: RunSecretCleanupHostedService and JobEventStreamService are
			// ADR-0013 §1 control-plane concerns (the ad hoc run-secret sweep and the
			// SSE fan-out the API alone exposes) -- a dedicated execution-only runner
			// host has no run/scan endpoints or SSE surface to serve, so it opts out
			// via hostKind rather than starting background work with nothing to do.
			if (hostKind == WaypointInfrastructureHostKind.Combined)
			{
				services.AddHostedService<Secrets.RunSecretCleanupHostedService>();

				services.AddSingleton<JobEventStreamService>(serviceProvider => new JobEventStreamService(
					connectionString,
					serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
					serviceProvider.GetRequiredService<ILogger<JobEventStreamService>>()));
				services.AddSingleton<IJobEventFeed>(serviceProvider => serviceProvider.GetRequiredService<JobEventStreamService>());
				services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobEventStreamService>());
			}

			// The first production job handler registration (issue #194, epic #9 slice
			// 2) -- constructed and registered here, not self-registering, the same
			// pattern PowerShellJobHandler's doc comment describes for the real job
			// types (jobs.job_type is a closed CHECK set with no generic member).
			services.AddSingleton<IJobHandler, Catalog.CatalogIndexJobHandler>();

			// Issue #441: DownloadJobHandler is registered concretely (not as
			// IJobHandler) so ToolGatedDownloadJobHandler -- the ADR-0015 tool-presence
			// gate -- can wrap it; only the gated wrapper is registered as the
			// "download" IJobHandler the dispatcher resolves.
			services.AddSingleton<Downloads.DownloadJobHandler>();
			services.AddSingleton<IJobHandler, Downloads.ToolGatedDownloadJobHandler>();
			services.AddSingleton<IJobHandler, Discovery.DiscoverJobHandler>();
			services.AddSingleton<IJobHandler, Scans.ScanJobHandler>();
			services.AddSingleton<IJobHandler, Credentials.CredentialTestJobHandler>();

			services.AddSingleton<JobDispatcherHostedService>();
			services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobDispatcherHostedService>());
			services.AddHostedService<LeaseRecoveryHostedService>();
		}

		return services;
	}
}
