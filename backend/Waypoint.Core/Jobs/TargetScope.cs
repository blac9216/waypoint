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

using System.Text.Json.Serialization;

namespace Waypoint.Core.Jobs;

/// <summary>
/// The closed <c>target_scope.mode</c> vocabulary (issue #733, epic #726 Wave 2,
/// docs/api-contract.md's planned end-state <c>{ site_id, target_scope }</c> shape).
/// <see cref="All"/> expands to every catalog-compatible component discovered beneath
/// the named top-level targets after the mandatory pre-scan refresh (ADR-0023 §3:
/// "Top-level 'all' expands against refreshed inventory and includes newly discovered
/// compatible components"). <see cref="Explicit"/> is the exact stable-component set
/// the caller named -- it never widens, including when the caller submits an empty
/// list (issue #733 AC "No scan silently falls back from an empty explicit selection
/// to the whole site").
/// </summary>
public static class TargetScopeModes
{
	public const string All = "all";
	public const string Explicit = "explicit";

	public static readonly IReadOnlyCollection<string> Values = [All, Explicit];

	public static bool IsValid(string? mode) => mode is All or Explicit;
}

/// <summary>
/// The parsed, not-yet-resolved shape of a scan run's <c>scope.target_scope</c>
/// (docs/api-contract.md planned end-state: "<c>target_scope</c> is exactly one of
/// <c>{ "mode": "all", "target_ids": [...] }</c> ... or <c>{ "mode": "explicit",
/// "component_ids": [...] }</c>"). This is the tri-state REQUEST shape -- see
/// <see cref="Components.ResolvedTargetScope"/> for what it resolves to once joined
/// against live component identity. Exactly one of <see cref="TargetIds"/>/
/// <see cref="ComponentIds"/> is meaningful depending on <see cref="Mode"/>; the other
/// is ignored by <see cref="Waypoint.Infrastructure.Runs.ScopeResolutionService"/> rather
/// than rejected, so a caller that (harmlessly) echoes both fields back is not
/// penalized -- only naming BOTH modes at once (the wire's own
/// <c>scope.mode</c>/<c>scope.profile_id</c> co-presence checks, mirrored here) is a
/// 400.
/// </summary>
public sealed record TargetScopeRequest(
	[property: JsonPropertyName("mode")] string? Mode,
	[property: JsonPropertyName("target_ids")] IReadOnlyList<Guid>? TargetIds,
	[property: JsonPropertyName("component_ids")] IReadOnlyList<Guid>? ComponentIds);
