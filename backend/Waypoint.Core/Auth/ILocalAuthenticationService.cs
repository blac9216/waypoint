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
/// The dev-grade local authentication abstraction (ADR-0004 rollout note). This is the
/// seam issue #29 replaces with Keycloak OIDC token validation — callers (the login
/// endpoint, the authentication handler) depend on this interface only, never on the
/// in-memory implementation directly, so swapping it is a DI registration change, not
/// a rewrite.
/// </summary>
public interface ILocalAuthenticationService
{
	/// <summary>Validates a username/password pair and issues a new session, or returns null on any failure.</summary>
	LocalSession? Authenticate(string username, string password);

	/// <summary>Resolves a bearer token to its session, or returns null if the token is unknown or expired.</summary>
	LocalSession? ValidateToken(string token);
}
