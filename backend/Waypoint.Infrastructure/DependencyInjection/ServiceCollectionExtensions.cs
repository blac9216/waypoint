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
using Waypoint.Core.Configuration;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Auth;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;

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

		services.AddSingleton<ILocalAuthenticationService, InMemoryLocalAuthenticationService>();

		// One scrubber instance serves both sides of security.md control 1: sinks read
		// through ISecretRedactor, decrypting code registers through ISecretTracker.
		services.AddSingleton<InPlaySecretRedactor>();
		services.AddSingleton<ISecretRedactor>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());
		services.AddSingleton<ISecretTracker>(serviceProvider => serviceProvider.GetRequiredService<InPlaySecretRedactor>());

		services.AddSingleton<JobHandlerRegistry>();

		string? connectionString = configuration.GetConnectionString(ConnectionStringName);
		if (!string.IsNullOrWhiteSpace(connectionString))
		{
			services.AddSingleton<ISchemaMigrator>(serviceProvider => new NpgsqlSchemaMigrator(
				connectionString,
				serviceProvider.GetRequiredService<ILogger<NpgsqlSchemaMigrator>>()));

			services.AddSingleton<IJobQueueRepository>(serviceProvider => new JobQueueRepository(
				connectionString,
				serviceProvider.GetRequiredService<ILogger<JobQueueRepository>>()));

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

			services.AddSingleton<JobDispatcherHostedService>();
			services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<JobDispatcherHostedService>());
			services.AddHostedService<LeaseRecoveryHostedService>();
		}

		return services;
	}
}
