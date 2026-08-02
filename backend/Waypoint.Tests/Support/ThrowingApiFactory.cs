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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Waypoint.Tests.Support;

/// <summary>
/// A host identical to <see cref="WaypointApiFactory"/> except it wires in one extra
/// test-only route (<see cref="ThrowPath"/>) that always throws a plain
/// <see cref="InvalidOperationException"/>. Exists solely to drive a real, non-
/// <c>ApiException</c> fault through the real HTTP pipeline so the generic
/// <c>catch (Exception)</c> arm of <c>ErrorHandlingMiddleware</c> — otherwise
/// untested — can be pinned end to end (issue #75). Production code (<c>Program.cs</c>)
/// is untouched; the route is added via <see cref="IStartupFilter"/> registered only in
/// this test host's DI container.
/// </summary>
public sealed class ThrowingApiFactory : WaypointApiFactory
{
	/// <summary>Route that always throws. Deliberately outside <c>api/v1/_stub</c> — this is not a scaffold convention, it's a fault injector.</summary>
	public const string ThrowPath = "/api/v1/_test/throw";

	/// <summary>
	/// The exact exception message the test-only route throws. Exposed so the test can
	/// assert this literal text does <em>not</em> appear anywhere in the response body —
	/// if it does, the middleware has started echoing exception details to the client.
	/// </summary>
	public const string ExceptionMessage =
		"test-only: this message, and this exception's type name and stack trace, must never reach an HTTP client";

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		base.ConfigureWebHost(builder);

		builder.ConfigureTestServices(services =>
		{
			services.AddSingleton<IStartupFilter, ThrowingEndpointStartupFilter>();
		});
	}

	/// <summary>
	/// Appends a middleware that throws for <see cref="ThrowPath"/> after every
	/// production middleware in <c>Program.cs</c> has already been registered — so the
	/// thrown exception flows back up through the real <c>ErrorHandlingMiddleware</c>,
	/// <c>UseStatusCodePages</c>, and auth/routing exactly as it would for any other
	/// unhandled fault in production code.
	/// </summary>
	private sealed class ThrowingEndpointStartupFilter : IStartupFilter
	{
		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
		{
			return app =>
			{
				next(app);

				app.Use(async (context, nextMiddleware) =>
				{
					if (string.Equals(context.Request.Path, ThrowPath, StringComparison.Ordinal))
					{
						throw new InvalidOperationException(ExceptionMessage);
					}

					await nextMiddleware(context);
				});
			};
		}
	}
}
