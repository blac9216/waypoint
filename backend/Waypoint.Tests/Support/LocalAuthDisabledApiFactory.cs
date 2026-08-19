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
/// <see cref="WaypointApiFactory"/> with <c>LocalAuth:Enabled</c> forced back to false --
/// the production default (issue #29). Used to prove <c>POST /auth/login</c> answers
/// 404 rather than silently succeeding or 401ing when local auth is off, and that the
/// policy scheme never routes a request to the (still-registered but unreachable)
/// LocalSession handler in that state.
/// </summary>
public sealed class LocalAuthDisabledApiFactory : WaypointApiFactory
{
	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureAppConfiguration((_, configBuilder) =>
		{
			// Layered after WaypointApiFactory's own AddInMemoryCollection (which sets
			// this to "true") -- the later source wins, so this genuinely overrides it
			// back to disabled for this factory only.
			configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["LocalAuth:Enabled"] = "false"
			});
		});
	}
}
