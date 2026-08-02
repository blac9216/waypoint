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
	/// SHA-256 hex digest of the admin password. Set via the
	/// <c>LocalAuth__AdminPasswordHash</c> environment variable (or an operator secret
	/// mount) — never a literal password in configuration, and never committed.
	/// </summary>
	public string? AdminPasswordHash { get; set; }

	/// <summary>How long an issued session token remains valid.</summary>
	public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);
}
