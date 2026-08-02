using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Core.Auth;
using Waypoint.Core.Configuration;
using Waypoint.Core.Logging;
using Waypoint.Infrastructure.Auth;

namespace Waypoint.Infrastructure.DependencyInjection;

/// <summary>
/// Composition-root entry point for everything this project provides. Deliberately DI
/// wiring only at this milestone (issue #3) — EF Core / Postgres access lands with the
/// schema in issue #4; PowerShell runspace hosting lands with the job engine.
/// </summary>
public static class ServiceCollectionExtensions
{
	public static IServiceCollection AddWaypointInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<LocalAuthOptions>()
			.Bind(configuration.GetSection(LocalAuthOptions.SectionName));

		services.AddOptions<WaypointBuildOptions>()
			.Bind(configuration.GetSection(WaypointBuildOptions.SectionName));

		services.AddSingleton<ILocalAuthenticationService, InMemoryLocalAuthenticationService>();

		// Placeholder scrubber (issue #6 supplies the real one) — see ISecretRedactor.
		services.AddSingleton<ISecretRedactor, NoOpSecretRedactor>();

		return services;
	}
}
