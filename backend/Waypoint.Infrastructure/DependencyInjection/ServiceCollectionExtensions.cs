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

namespace Waypoint.Infrastructure.DependencyInjection;

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

	public static IServiceCollection AddWaypointInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<LocalAuthOptions>()
			.Bind(configuration.GetSection(LocalAuthOptions.SectionName));

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

		services.AddOptions<DiscoveryOptions>()
			.Bind(configuration.GetSection(DiscoveryOptions.SectionName));

		services.AddOptions<Waypoint.Core.Scans.ScanOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.Scans.ScanOptions.SectionName));

		services.AddSingleton<ILocalAuthenticationService, InMemoryLocalAuthenticationService>();

		// One scrubber instance serves both sides of security.md control 1: sinks read
		// through ISecretRedactor, decrypting code registers through ISecretTracker.
		services.AddSingleton<InPlaySecretRedactor>();
		services.AddSingleton<ISecretRedactor>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());
		services.AddSingleton<ISecretTracker>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());

		// ADR-0011 ad hoc "my credentials" flow (issue #276): process-memory only, no
		// connection-string gate -- registered unconditionally like the redactor above
		// so a host without Postgres configured still boots (the audit write inside it
		// no-ops without a connection string, same pattern as other best-effort writes).
		services.AddSingleton<Waypoint.Core.Secrets.IEphemeralCredentialCache>(serviceProvider =>
		{
			string? ephemeralConnectionString = configuration.GetConnectionString(ConnectionStringName);
			return new Secrets.EphemeralCredentialCache(
				serviceProvider.GetRequiredService<ISecretTracker>(),
				ephemeralConnectionString,
				serviceProvider.GetRequiredService<ILogger<Secrets.EphemeralCredentialCache>>());
		});

		services.AddSingleton<JobHandlerRegistry>();

		// Disk usage is a filesystem stat, not a database read -- registered
		// unconditionally so GET /system still reports store usage on a host with no
		// connection string configured (issue #226).
		services.AddSingleton<Waypoint.Core.SystemState.IArtifactStoreDiskUsageProvider, ArtifactStoreDiskUsageProvider>();

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

			services.AddSingleton<IJobQueueRepository>(serviceProvider => new JobQueueRepository(
				connectionString,
				serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>(),
				serviceProvider.GetRequiredService<IJobEventPublisher>()));

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

			// Issue #311: the convert-stage upload/enrichment coordinator and the
			// retry route (JobsController) share this one instance.
			services.AddSingleton<Scans.ScanUploadCoordinator>();

			services.AddSingleton<JobEventStreamService>(serviceProvider => new JobEventStreamService(
				connectionString,
				serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
				serviceProvider.GetRequiredService<ILogger<JobEventStreamService>>()));
			services.AddSingleton<IJobEventFeed>(serviceProvider => serviceProvider.GetRequiredService<JobEventStreamService>());
			services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobEventStreamService>());

			// The first production job handler registration (issue #194, epic #9 slice
			// 2) -- constructed and registered here, not self-registering, the same
			// pattern PowerShellJobHandler's doc comment describes for the real job
			// types (jobs.job_type is a closed CHECK set with no generic member).
			services.AddSingleton<IJobHandler, Catalog.CatalogIndexJobHandler>();
			services.AddSingleton<IJobHandler, Downloads.DownloadJobHandler>();
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
