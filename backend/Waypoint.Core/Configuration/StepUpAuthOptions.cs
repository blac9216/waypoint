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
/// Configuration for step-up re-authentication (issue #521, AC3 of #29) — see
/// <c>docs/security.md</c> "Step-up re-authentication" for the full design. Bound from
/// the <c>StepUpAuth</c> section.
/// </summary>
public sealed class StepUpAuthOptions
{
	public const string SectionName = "StepUpAuth";

	/// <summary>
	/// How recent the token's <c>auth_time</c> claim must be for a
	/// <c>[RequireFreshAuth]</c>-guarded action to proceed. Default 5 minutes: long
	/// enough that the `prompt=login` redirect round trip and the subsequent API call
	/// both comfortably land inside it, short enough that a walked-away session cannot
	/// be replayed against a sensitive write.
	/// </summary>
	public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromMinutes(5);
}
