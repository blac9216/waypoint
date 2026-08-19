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

using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Waypoint.Core.Errors;

namespace Waypoint.Core.Authorization;

/// <summary>
/// Marks an action as requiring step-up re-authentication (issue #521) — see
/// <c>docs/security.md</c> "Step-up re-authentication" for the full design. Stacks
/// alongside a <c>[Require*Role]</c> attribute on the same action (ASP.NET Core ANDs
/// multiple <see cref="AuthorizeAttribute"/> policies), and — like
/// <see cref="RequireRoleAttribute"/> — sets <see cref="AuthorizeAttribute.Policy"/> so
/// the requirement is visible to anything that inspects authorization metadata
/// declaratively. Use this pipeline-level form on an action that is *unconditionally*
/// sensitive (every call needs step-up, regardless of the request body) — the
/// remediation/update-apply call sites this issue's design anticipates land this way
/// once those endpoints exist.
///
/// Unlike the role guard, this attribute alone does not produce the response a
/// step-up-aware client needs: a bare framework 403 cannot be distinguished from
/// "forbidden, full stop." The action must additionally call <see cref="Check"/>
/// (typically first, before doing any work) to get the distinct
/// <c>step_up_required</c> <see cref="ApiException"/> the frontend acts on — this
/// attribute's declarative policy is a defense-in-depth backstop for the
/// unconditional case, not the primary enforcement mechanism.
///
/// When the gate is *conditional* on the request body — e.g.
/// <c>CredentialsController.Update</c>, where only a request that overwrites secret
/// material is gated and a plain rename must not be — do not apply this attribute at
/// all: a bare `[Authorize]` policy runs before model binding, so it cannot see the
/// body and would reject every PUT indiscriminately. Call <see cref="Check"/> alone,
/// after inspecting the body, exactly as that controller does.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireFreshAuthAttribute : AuthorizeAttribute
{
	public RequireFreshAuthAttribute()
	{
		Policy = WaypointAuthorizationPolicies.FreshAuth;
	}

	/// <summary>
	/// Throws <see cref="ApiException"/> with the distinct <c>step_up_required</c> code
	/// (403) when <paramref name="principal"/>'s authentication is not fresh enough per
	/// <see cref="FreshAuthEvaluator.IsFresh"/>. Call this at the top of a step-up-gated
	/// action (or the body-conditional branch of one) before any work — e.g.
	/// <c>CredentialsController.Update</c> when the request body overwrites secret
	/// material.
	/// </summary>
	public static void Check(ClaimsPrincipal principal, TimeSpan freshnessWindow)
	{
		if (!FreshAuthEvaluator.IsFresh(principal, freshnessWindow, DateTimeOffset.UtcNow))
		{
			throw new ApiException(
				HttpStatusCode.Forbidden,
				"step_up_required",
				"This action requires you to re-authenticate first.",
				"The caller's token auth_time is missing or older than the configured StepUpAuth:FreshnessWindow. " +
				"Re-run the authorization-code flow with prompt=login (or max_age=0) and retry.");
		}
	}
}
