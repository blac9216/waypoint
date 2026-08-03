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
using Waypoint.Core.Auth;
using Waypoint.Core.Configuration;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Auth;
using Waypoint.Infrastructure.Data;

namespace Waypoint.Infrastructure.DependencyInjection;

/// <summary>
/// Composition-root entry point for everything this project provides. Postgres access
/// lands with the schema (issue #4) as a small raw-SQL migrations pipeline
/// (<see cref="ISchemaMigrator"/>), not an ORM — see that type's doc comment for why.
/// PowerShell runspace hosting lands with the job engine.
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

		services.AddSingleton<ILocalAuthenticationService, InMemoryLocalAuthenticationService>();

		// Placeholder scrubber (issue #6 supplies the real one) — see ISecretRedactor.
		services.AddSingleton<ISecretRedactor, NoOpSecretRedactor>();

		string? connectionString = configuration.GetConnectionString(ConnectionStringName);
		if (!string.IsNullOrWhiteSpace(connectionString))
		{
			services.AddSingleton<ISchemaMigrator>(serviceProvider => new NpgsqlSchemaMigrator(
				connectionString,
				serviceProvider.GetRequiredService<ILogger<NpgsqlSchemaMigrator>>()));
		}

		return services;
	}
}
