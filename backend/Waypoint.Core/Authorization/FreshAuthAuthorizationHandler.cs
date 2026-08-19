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
using Microsoft.Extensions.Options;
using Waypoint.Core.Configuration;

namespace Waypoint.Core.Authorization;

/// <summary>
/// Succeeds a <see cref="FreshAuthRequirement"/> when the current principal's
/// authentication counts as "fresh enough" for a step-up-gated action (issue #521) —
/// see <c>docs/security.md</c> "Step-up re-authentication" for the full design this
/// implements.
///
/// Two paths to success, mirroring that design exactly:
/// - The dev-flag local-session scheme (<see cref="WaypointClaimTypes.LocalSessionAuthenticationType"/>)
///   has no IdP behind it to re-prompt, so every local-auth-authenticated request is
///   treated as fresh — an explicit, documented carve-out, not a fail-open default.
/// - Every other (OIDC) principal must carry a <see cref="WaypointClaimTypes.AuthTime"/>
///   claim whose age is within <see cref="StepUpAuthOptions.FreshnessWindow"/>. A
///   missing or unparseable claim fails closed — the same rule
///   <see cref="MinimumRoleAuthorizationHandler"/> applies to a missing role claim.
///
/// This handler alone cannot distinguish "step up and retry" from "forbidden" in the
/// framework's binary Forbid outcome, so <c>RequireFreshAuthAttribute.Check</c> is
/// what controllers actually call to get the distinct <c>step_up_required</c> error
/// code (see that type's doc comment) — this handler exists so the policy is also
/// visible to anything that inspects ASP.NET Core's authorization metadata
/// declaratively (Swagger, policy audits), the same discoverability
/// <c>[Require*Role]</c> gives today.
/// </summary>
public sealed class FreshAuthAuthorizationHandler : AuthorizationHandler<FreshAuthRequirement>
{
	private readonly IOptionsMonitor<StepUpAuthOptions> _options;

	public FreshAuthAuthorizationHandler(IOptionsMonitor<StepUpAuthOptions> options)
	{
		_options = options;
	}

	protected override Task HandleRequirementAsync(
		AuthorizationHandlerContext context,
		FreshAuthRequirement requirement)
	{
		if (FreshAuthEvaluator.IsFresh(context.User, _options.CurrentValue.FreshnessWindow, DateTimeOffset.UtcNow))
		{
			context.Succeed(requirement);
		}

		return Task.CompletedTask;
	}
}
