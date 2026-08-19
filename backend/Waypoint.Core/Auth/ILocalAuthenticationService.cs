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

namespace Waypoint.Core.Auth;

/// <summary>
/// The dev-grade local authentication abstraction (ADR-0004 rollout note; issue #29
/// made this an explicit dev-flag-only path behind OIDC). Callers (the login endpoint,
/// the authentication handler) depend on this interface only, never on the in-memory
/// implementation directly.
/// </summary>
public interface ILocalAuthenticationService
{
	/// <summary>
	/// Validates a username/password pair and issues a new session, or returns null if
	/// the credentials are rejected. Throws <see cref="LocalAuthNotReadyException"/>
	/// (issue #505) when the auth backend itself is not yet able to evaluate
	/// credentials — e.g. the admin password hash has not finished resolving from its
	/// configured source — so callers can distinguish "not ready" from "wrong
	/// credentials" instead of collapsing both into the same generic rejection.
	/// </summary>
	LocalSession? Authenticate(string username, string password);

	/// <summary>Resolves a bearer token to its session, or returns null if the token is unknown or expired.</summary>
	LocalSession? ValidateToken(string token);
}

/// <summary>
/// Thrown by <see cref="ILocalAuthenticationService.Authenticate"/> when the local auth
/// backend cannot yet evaluate credentials — distinct from "credentials rejected"
/// (issue #505). Mirrors the <c>MasterKeyUnavailableException</c> /
/// <c>master_key_unavailable</c> 503 precedent (issues #409/#429): the API's error
/// middleware maps this to <c>503 auth_not_ready</c> rather than the generic
/// <c>401 invalid_credentials</c>, so a human hitting the warm-up window is told the
/// backend isn't ready yet, not that their password is wrong.
/// </summary>
public sealed class LocalAuthNotReadyException : Exception
{
	public LocalAuthNotReadyException(string message)
		: base(message)
	{
	}
}
