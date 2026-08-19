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
using Waypoint.Core.Audit;
using Waypoint.Core.Auth;
using Waypoint.Core.Catalog;
using Waypoint.Core.Configuration;
using Waypoint.Core.Discovery;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Scheduling;
using Waypoint.Core.Users;
using Waypoint.Infrastructure.Audit;
using Waypoint.Infrastructure.Auth;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Discovery;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Scheduling;
using Waypoint.Infrastructure.Sites;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Infrastructure.Users;
using Waypoint.Runner.Jobs;

namespace Waypoint.Infrastructure.DependencyInjection;

/// <summary>
/// Issue #443 (ADR-0013 §1): this project is now control-plane only, so there is a
/// single composition shape -- no more <c>WaypointInfrastructureHostKind</c> branch
/// here. What used to be the "ExecutionOnly" side of that enum (the PowerShell host,
/// every domain <see cref="IJobHandler"/>, the dispatcher/lease-recovery hosted
/// services, and the <c>RunnerResourceOptions</c>/resource-admission wiring) now lives
/// entirely in the sibling <c>Waypoint.Infrastructure.Execution</c> project's own
/// composition root (<c>AddWaypointExecution</c>), which the two dedicated runner
/// executables call in addition to <see cref="AddWaypointInfrastructure"/>.
/// <c>Waypoint.Api</c> has no reference to <c>Waypoint.Infrastructure.Execution</c> at
/// all, which is what makes "the API process cannot execute a job" a build-time fact
/// (missing assembly reference, not a registration a config change could reactivate)
/// rather than a runtime configuration choice. See ADR-0013 §1 and the epic (#433).
///
/// Composition-root entry point for this project's two exposed methods:
/// <see cref="AddWaypointInfrastructure"/> (repositories, migrations
/// (<see cref="ISchemaMigrator"/> -- a small raw-SQL pipeline, not an ORM, issue #4),
/// envelope-encryption of writes, and the job enqueue/control/query surface -- shared
/// by the API and both runners) and <see cref="AddWaypointApiSurface"/> (SSE feed/
/// replay and the run-secret lifecycle -- API-only; see that method's doc comment for
/// why these two are not simply folded into the first). Neither method starts a job
/// dispatcher, lease recovery, or any PowerShell host -- see
/// <c>Waypoint.Infrastructure.Execution</c>'s own
/// <c>ServiceCollectionExtensions.AddWaypointExecution</c> in the sibling project for
/// that half of what used to be registered here before issue #443.
/// </summary>
public static class ServiceCollectionExtensions
{
	/// <summary>
	/// The standard ASP.NET Core connection-string slot this project reads the
	/// database connection from (<c>ConnectionStrings:Waypoint</c>).
	/// </summary>
	public const string ConnectionStringName = "Waypoint";

	/// <summary>
	/// Registers the control-plane services every host needs: options, repositories
	/// (behind the "no connection string, no wiring" guard below), crypto, and the
	/// enqueue/control/query surface. Called by both <c>Waypoint.Api.Program</c> (which
	/// then also calls <see cref="AddWaypointApiSurface"/> for the two API-only hosted
	/// services -- see that method's doc comment for why they are NOT registered here)
	/// and by the two runner executables' Program.cs (which then also call
	/// <c>Waypoint.Infrastructure.Execution</c>'s own <c>AddWaypointExecution</c> for
	/// the PowerShell host and job handlers). A runner needs the same repositories this
	/// method wires and nothing this method deliberately withholds.
	/// </summary>
	public static IServiceCollection AddWaypointInfrastructure(this IServiceCollection services, IConfiguration configuration)
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

		services.AddOptions<Waypoint.Core.SystemState.WorkerRegistryOptions>()
			.Bind(configuration.GetSection(Waypoint.Core.SystemState.WorkerRegistryOptions.SectionName));

		services.AddOptions<ScheduleDispatchOptions>()
			.Bind(configuration.GetSection(ScheduleDispatchOptions.SectionName));

		services.AddSingleton<ILocalAuthenticationService, InMemoryLocalAuthenticationService>();

		// One scrubber instance serves both sides of security.md control 1: sinks read
		// through ISecretRedactor, decrypting code registers through ISecretTracker.
		services.AddSingleton<InPlaySecretRedactor>();
		services.AddSingleton<ISecretRedactor>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());
		services.AddSingleton<ISecretTracker>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());

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

			// Issue #443: worker_registry read side -- the control plane's only
			// consumer of this table (see migration 0027's comment: neither runner
			// role is granted SELECT). The write side
			// (WorkerRegistryRepository.HeartbeatAsync) is wired the same way by each
			// runner host's own composition alongside its existing readiness-report
			// loop, not from this control-plane registration.
			services.AddSingleton<Waypoint.Core.SystemState.IWorkerRegistryReader>(new SystemState.WorkerRegistryRepository(connectionString));

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
			services.AddSingleton<IScheduleRepository>(new ScheduleRepository(connectionString));
			services.AddSingleton<IUserDirectory>(new UserRepository(connectionString));
			services.AddSingleton<IAuditRepository>(new AuditRepository(connectionString));
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
				serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Waypoint.Core.Secrets.RunSecretOptions>>(),
				serviceProvider.GetRequiredService<ILogger<Secrets.RunSecretStore>>()));

			// Issue #311: the convert-stage upload/enrichment coordinator and the
			// retry route (JobsController) share this one instance.
			services.AddSingleton<Scans.ScanUploadCoordinator>();

			// Issue #414: RunsController's extracted control-plane application services
			// (run creation/fan-out, artifact projection, pause/resume/abort
			// orchestration) -- same "concrete singleton, no interface" shape as
			// ScanUploadCoordinator above, since each has exactly one caller today.
			services.AddSingleton<Runs.RunCreationService>();
			services.AddSingleton<Runs.RunArtifactProjectionService>();
			services.AddSingleton<Runs.RunControlService>();

			// Issue #513: dashboard aggregate service, reusing RunArtifactProjectionService's
			// HDF-derived CAT counting rather than a second implementation.
			services.AddSingleton<Runs.DashboardAggregateService>();
		}

		return services;
	}

	/// <summary>
	/// Issue #443 (found by the live-stack runner-parity verification this issue's
	/// acceptance criteria require, not by a unit test): <c>RunSecretCleanupHostedService</c>
	/// (the ad hoc "my credentials" sweep) and <see cref="JobEventStreamService"/> (SSE
	/// fan-out) are API-surface concerns, not something every caller of
	/// <see cref="AddWaypointInfrastructure"/> should get. The two dedicated runners also
	/// call <c>AddWaypointInfrastructure</c> (for the control-plane repositories they
	/// share with the API), so registering these two hosted services unconditionally
	/// INSIDE that method -- which an earlier revision of this file did -- started an SSE
	/// tail-poll loop and a run-secret sweep inside compliance-runner/download-runner too.
	/// Both promptly failed: <c>waypoint_compliance_runner</c>/<c>waypoint_download_runner</c>
	/// are deliberately not granted <c>SELECT</c> on <c>job_events</c> (migration 0025's
	/// least-privilege grants), so <c>JobEventStreamService</c>'s poll loop logged a
	/// permission-denied error every tick forever -- caught only by actually bringing up
	/// compliance-runner against real Postgres and reading its logs, not by any
	/// composition-registration unit test, because "this hosted service starts" is not
	/// wrong in itself; it is wrong that it starts on a host whose database ROLE cannot
	/// use it. <c>Waypoint.Api.Program</c> is the only caller of this method.
	/// </summary>
	public static IServiceCollection AddWaypointApiSurface(this IServiceCollection services, IConfiguration configuration)
	{
		string? connectionString = configuration.GetConnectionString(ConnectionStringName);
		if (string.IsNullOrWhiteSpace(connectionString))
		{
			return services;
		}

		services.AddHostedService<Secrets.RunSecretCleanupHostedService>();

		// Issue #31: control-plane schedule dispatch. API-surface only -- see this
		// method's own doc comment for why a runner host (which also calls
		// AddWaypointInfrastructure for its shared repositories) must never start this
		// loop too, mirroring RunSecretCleanupHostedService's placement above.
		services.AddSingleton<ScheduleDispatchService>();
		services.AddHostedService<ScheduleDispatchHostedService>();

		services.AddSingleton<JobEventStreamService>(serviceProvider => new JobEventStreamService(
			connectionString,
			serviceProvider.GetRequiredService<IOptions<JobEngineOptions>>(),
			serviceProvider.GetRequiredService<ILogger<JobEventStreamService>>()));
		services.AddSingleton<IJobEventFeed>(serviceProvider => serviceProvider.GetRequiredService<JobEventStreamService>());
		services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobEventStreamService>());

		return services;
	}
}
