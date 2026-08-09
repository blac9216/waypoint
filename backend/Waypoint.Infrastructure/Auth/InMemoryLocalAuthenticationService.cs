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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Auth;
using Waypoint.Core.Authorization;
using Waypoint.Core.Configuration;

namespace Waypoint.Infrastructure.Auth;

/// <summary>
/// Dev-grade <see cref="ILocalAuthenticationService"/>: a single Admin user sourced from
/// <see cref="LocalAuthOptions"/>, sessions held in an in-memory table. Every session is
/// lost on process restart — acceptable for a single-operator dev milestone; the
/// abstraction is what issue #29 swaps for Keycloak, not this implementation's
/// persistence model.
/// </summary>
public sealed partial class InMemoryLocalAuthenticationService : ILocalAuthenticationService
{
	private readonly ConcurrentDictionary<string, LocalSession> _sessions = new(StringComparer.Ordinal);
	private readonly IOptionsMonitor<LocalAuthOptions> _options;
	private readonly ILogger<InMemoryLocalAuthenticationService> _logger;

	public InMemoryLocalAuthenticationService(
		IOptionsMonitor<LocalAuthOptions> options,
		ILogger<InMemoryLocalAuthenticationService> logger)
	{
		_options = options;
		_logger = logger;
	}

	public LocalSession? Authenticate(string username, string password)
	{
		LocalAuthOptions options = _options.CurrentValue;

		if (string.IsNullOrEmpty(options.AdminPasswordHash))
		{
			LogAdminPasswordHashNotConfigured();
			return null;
		}

		if (!string.Equals(username, options.AdminUsername, StringComparison.Ordinal))
		{
			return null;
		}

		if (!Pbkdf2PasswordHasher.Verify(password, options.AdminPasswordHash))
		{
			return null;
		}

		LocalSession session = new(
			Token: GenerateToken(),
			Username: options.AdminUsername,
			Role: WaypointRole.Admin,
			ExpiresAt: DateTimeOffset.UtcNow.Add(options.SessionLifetime));

		_sessions[session.Token] = session;

		return session;
	}

	public LocalSession? ValidateToken(string token)
	{
		if (!_sessions.TryGetValue(token, out LocalSession? session))
		{
			return null;
		}

		if (session.ExpiresAt <= DateTimeOffset.UtcNow)
		{
			_sessions.TryRemove(token, out _);
			return null;
		}

		return session;
	}

	private static string GenerateToken()
	{
		return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
	}

	[LoggerMessage(
		Level = LogLevel.Warning,
		Message = "Local auth login attempted but LocalAuth:AdminPasswordHash is not configured; " +
			"refusing every login until an operator sets it.")]
	private partial void LogAdminPasswordHashNotConfigured();
}
