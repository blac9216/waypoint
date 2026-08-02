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
