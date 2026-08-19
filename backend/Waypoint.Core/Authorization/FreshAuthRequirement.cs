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

using Microsoft.AspNetCore.Authorization;

namespace Waypoint.Core.Authorization;

/// <summary>
/// Authorization requirement satisfied when the current principal's authentication is
/// "fresh enough" for a step-up-gated action (issue #521) — see <c>docs/security.md</c>
/// "Step-up re-authentication" for the full design and <see cref="FreshAuthAuthorizationHandler"/>
/// for the freshness check itself. Unlike <see cref="MinimumRoleRequirement"/> this
/// carries no data of its own — the freshness window is read live from
/// <c>IOptionsMonitor&lt;StepUpAuthOptions&gt;</c> by the handler rather than baked into
/// the requirement instance at policy-registration (startup) time, so an operator can
/// change <c>StepUpAuth:FreshnessWindow</c> without a restart. A singleton marker,
/// mirroring how <see cref="MinimumRoleRequirement"/> is instantiated once per role at
/// startup, just with no per-instance parameter to vary.
/// </summary>
public sealed class FreshAuthRequirement : IAuthorizationRequirement
{
	public static readonly FreshAuthRequirement Instance = new();

	private FreshAuthRequirement()
	{
	}
}
