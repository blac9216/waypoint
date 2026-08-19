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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Waypoint.Core.Auth;

namespace Waypoint.Tests.Support;

/// <summary>
/// The default in-process test host: real local auth (<see cref="TestAdminPassword"/>
/// configured as the single admin user's password), real "LocalSession" authentication
/// scheme. Use this for anything that should behave exactly as production does —
/// health, login, and the unauthenticated (401) path.
/// </summary>
public class WaypointApiFactory : WebApplicationFactory<Program>
{
	/// <summary>The password the in-memory admin user accepts in this test host.</summary>
	public const string TestAdminPassword = "test-only-dev-password";

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment("Testing");
		builder.ConfigureAppConfiguration((_, configBuilder) =>
		{
			configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
			{
				// Issue #29: local auth is a dev-flag-only path now (off by default) --
				// this test host opts in so the pre-existing login/me/warm-up coverage
				// keeps exercising the real "LocalSession" scheme end to end.
				["LocalAuth:Enabled"] = "true",
				["LocalAuth:AdminPasswordHash"] = Pbkdf2PasswordHasher.Hash(TestAdminPassword)
			});
		});
	}
}
