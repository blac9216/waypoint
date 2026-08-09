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

namespace Waypoint.Core.Configuration;

/// <summary>
/// Configuration for the dev-grade local authentication stand-in (ADR-0004 rollout
/// note): a single Admin user, no persistence beyond the process. Bound from the
/// <c>LocalAuth</c> section. There is deliberately no compiled-in default password —
/// an unset <see cref="AdminPasswordHash"/> means local auth refuses every login
/// rather than shipping a guessable default credential. This entire section — and
/// <see cref="Auth.ILocalAuthenticationService"/>, the abstraction it configures — is
/// replaced by Keycloak OIDC in issue #29; nothing outside the auth layer should read
/// these values directly.
/// </summary>
public sealed class LocalAuthOptions
{
	public const string SectionName = "LocalAuth";

	/// <summary>The single local admin account's username.</summary>
	public string AdminUsername { get; set; } = "admin";

	/// <summary>
	/// Salted, iterated PBKDF2 hash of the admin password (see
	/// <see cref="Auth.Pbkdf2PasswordHasher"/> for the stored format), generated with
	/// <c>dotnet Waypoint.Api.dll --hash-password</c>. Never a literal password in
	/// configuration, and never committed.
	///
	/// Preferred delivery (issue #333) is a mounted file named by
	/// <see cref="AdminPasswordHashFile"/> — see that property. The
	/// <c>LocalAuth__AdminPasswordHash</c> environment variable is still accepted as a
	/// fallback for existing deployments, but leaks via <c>/proc/&lt;pid&gt;/environ</c>,
	/// <c>docker inspect</c>, and crash dumps, so new deployments should use the file.
	/// A <see cref="Auth.LocalAuthOptionsPostConfigure"/> step resolves the file over the
	/// env var and leaves this property holding whichever source won — code downstream of
	/// options binding (e.g. <see cref="Auth.ILocalAuthenticationService"/>) reads only
	/// this property and never needs to know which source it came from.
	/// </summary>
	public string? AdminPasswordHash { get; set; }

	/// <summary>
	/// Path to a mounted file whose contents are the PBKDF2 admin password hash (a
	/// trailing newline is tolerated and trimmed). Set via the
	/// <c>LocalAuth__AdminPasswordHashFile</c> environment variable (or
	/// <c>LocalAuth:AdminPasswordHashFile</c> in configuration) to the operator-mounted
	/// path — see <c>deploy/docker-compose.yml</c> and <c>deploy/README.md</c> "Bring-up".
	/// This is the preferred delivery mechanism (issue #333); when set and the file
	/// exists, it takes precedence over <see cref="AdminPasswordHash"/> as read directly
	/// from configuration/env.
	/// </summary>
	public string? AdminPasswordHashFile { get; set; }

	/// <summary>How long an issued session token remains valid.</summary>
	public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);
}
