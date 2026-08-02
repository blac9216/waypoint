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

using Waypoint.Core.Authorization;

namespace Waypoint.Core.Auth;

/// <summary>An issued local-auth session: the token handed to the client plus the identity it represents.</summary>
/// <param name="Token">Opaque bearer token; the only thing the client presents on subsequent requests.</param>
/// <param name="Username">The authenticated username.</param>
/// <param name="Role">The role granted to this session.</param>
/// <param name="ExpiresAt">UTC expiry; the session is invalid at or after this instant.</param>
public sealed record LocalSession(string Token, string Username, WaypointRole Role, DateTimeOffset ExpiresAt);
