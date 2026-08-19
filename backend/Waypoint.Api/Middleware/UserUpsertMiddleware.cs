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

using System.Security.Claims;
using Waypoint.Core.Authorization;
using Waypoint.Core.Users;

namespace Waypoint.Api.Middleware;

/// <summary>
/// Issue #512: upserts the caller's <c>users</c> row on every authenticated request,
/// after <c>UseAuthentication</c> (so <see cref="HttpContext.User"/> is populated) and
/// before authorization/controllers run. This is the "populated/updated from OIDC
/// claims at authenticated request time" half of #512's acceptance criteria -- there
/// is no separate "login" event to hook in a plain OIDC relying party (ADR-0004): the
/// bearer token is presented fresh on every request, so request time IS login time.
///
/// <see cref="MinimumSeenInterval"/> is the throttle #512 calls for ("record last-seen
/// without hammering the DB on every request") -- <see cref="IUserDirectory.RecordSeenAsync"/>
/// only advances <c>last_seen_at</c> when the stored value is already older than this,
/// still refreshing username/role/auth_method every time (cheap, and role must reflect
/// "next token evaluation" promptly per #512's AC). Five minutes is a fixed constant
/// rather than a new options section -- the interval has no operator-facing tradeoff
/// worth exposing, unlike e.g. <c>JobEngineOptions</c>' polling knobs.
///
/// Runs best-effort: a directory write failure must never turn an otherwise-successful
/// authenticated request into a 500 (this is bookkeeping, not the request's own
/// business logic), so exceptions are logged and swallowed here.
///
/// <see cref="IUserDirectory"/> is resolved via <c>context.RequestServices.GetService</c>
/// (optional), not a required constructor/InvokeAsync parameter: <see cref="Waypoint.Infrastructure.DependencyInjection.ServiceCollectionExtensions.AddWaypointInfrastructure"/>
/// only registers it when a connection string is configured, and most of the existing
/// controller test hosts (<c>WaypointApiFactory</c> and its many per-controller
/// subclasses, e.g. <c>SchedulesTestApiFactory</c>) run with none, registering only the
/// one fake repository each test class actually needs. A required parameter here would
/// have made every one of those pre-existing, unrelated test hosts throw on their first
/// authenticated request.
/// </summary>
public sealed partial class UserUpsertMiddleware
{
	internal static readonly TimeSpan MinimumSeenInterval = TimeSpan.FromMinutes(5);

	private readonly RequestDelegate _next;
	private readonly ILogger<UserUpsertMiddleware> _logger;

	public UserUpsertMiddleware(RequestDelegate next, ILogger<UserUpsertMiddleware> logger)
	{
		_next = next;
		_logger = logger;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		IUserDirectory? userDirectory = context.RequestServices.GetService(typeof(IUserDirectory)) as IUserDirectory;
		ClaimsPrincipal user = context.User;
		if (userDirectory is not null && user.Identity?.IsAuthenticated == true)
		{
			string? sub = user.FindFirst(WaypointClaimTypes.Subject)?.Value;
			string? username = user.FindFirst(ClaimTypes.Name)?.Value;
			string? roleClaim = user.FindFirst(WaypointClaimTypes.Role)?.Value;

			if (!string.IsNullOrWhiteSpace(sub) && !string.IsNullOrWhiteSpace(username)
				&& Enum.TryParse(roleClaim, ignoreCase: true, out WaypointRole role))
			{
				// "local:" prefix matches LocalSessionAuthenticationHandler's synthesized
				// subject (issue #512) -- everything else reaching here authenticated via
				// the Oidc scheme (see OidcOrLocalPolicySchemeDefaults).
				string authMethod = sub.StartsWith("local:", StringComparison.Ordinal) ? "local" : "oidc";

				try
				{
					await userDirectory.RecordSeenAsync(sub, username, role, authMethod, MinimumSeenInterval, context.RequestAborted)
						.ConfigureAwait(false);
				}
				catch (Exception exception) when (exception is not OperationCanceledException)
				{
					LogUpsertFailed(exception, sub);
				}
			}
		}

		await _next(context).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to record users row for subject {Subject}; continuing the request.")]
	private partial void LogUpsertFailed(Exception exception, string subject);
}
