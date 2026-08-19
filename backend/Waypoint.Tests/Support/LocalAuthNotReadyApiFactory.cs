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

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Waypoint.Tests.Support;

/// <summary>
/// <see cref="WaypointApiFactory"/> with local auth enabled but no admin password hash
/// resolved -- the issue #505 warm-up window. <c>POST /auth/login</c> must answer
/// <c>503 auth_not_ready</c>, not the generic <c>401 invalid_credentials</c> a real
/// credential rejection produces.
/// </summary>
public sealed class LocalAuthNotReadyApiFactory : WaypointApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureAppConfiguration((_, configBuilder) =>
		{
			// Overrides WaypointApiFactory's own AdminPasswordHash back to unset (the
			// later source wins) -- LocalAuth:Enabled stays true from the base factory.
			configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["LocalAuth:AdminPasswordHash"] = null
			});
		});
	}
}
