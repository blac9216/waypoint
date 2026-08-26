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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waypoint.Core.Jobs;

/// <summary>
/// The parsed shape of <c>RunCreateRequest.Scope</c> for <c>run_type: "scan"</c>
/// (docs/api-contract.md <c>/runs</c>: "POST body: site_id, scope (products/components
/// + inventory selection)..."). <see cref="TargetIds"/> null or empty means "every
/// target under the site" -- the common case for a full-site scan; a non-empty list
/// scopes the fan-out to exactly those targets (the Start-a-Scan checkbox tree, #23's
/// later slice, populates this from cached inventory).
///
/// <see cref="ProfileId"/> is the pulled compliance-content profile (<c>profiles.id</c>,
/// <c>GET /profiles</c>) this scan executes against -- issue #639: previously a scan
/// always ran InSpec against a fixed, empty <c>ScanOptions.ProfilePath</c>, with no
/// wiring at all to the managed content store <c>content-pull</c> actually populates.
/// <see cref="Waypoint.Infrastructure.Runs.RunCreationService.CreateScanRunAsync"/>
/// requires it (must reference an installed profile, or the run is rejected 4xx) and
/// resolves it once at run-creation time to the profile's <c>profile_key</c>, carried
/// -- not the id -- on every fanned-out <c>scan</c> job's payload: the job handler
/// needs a content-store-relative directory name, not a database surrogate key.
///
/// <see cref="TargetScope"/> is issue #733's additive component-scope field (epic
/// #726 Wave 2, docs/api-contract.md's planned end-state <c>{ site_id, target_scope
/// }</c> shape): when present, <see cref="Waypoint.Infrastructure.Runs.RunCreationService.CreateScanRunAsync"/>
/// additionally resolves it via <see cref="Waypoint.Infrastructure.Runs.ScopeResolutionService"/>
/// and freezes a <see cref="Waypoint.Core.Components.ResolvedTargetScope"/> audit
/// snapshot (migration 0056) alongside the existing target-granular fan-out --
/// component-granular job fan-out itself is #735-#737 (ADR-0024), so this field does
/// not yet change which jobs a scan creates. Optional and independent of
/// <see cref="TargetIds"/>/<see cref="ProfileId"/> in this transitional slice; the
/// full cutover that makes <c>target_scope</c> the ONLY scope shape and rejects
/// <see cref="ProfileId"/> is the documented legacy-migration window (ADR-0025), not
/// this slice.
/// </summary>
public sealed record ScanScope(
	[property: JsonPropertyName("site_id")] Guid? SiteId,
	[property: JsonPropertyName("target_ids")] IReadOnlyList<Guid>? TargetIds,
	[property: JsonPropertyName("profile_id")] Guid? ProfileId = null,
	[property: JsonPropertyName("target_scope")] TargetScopeRequest? TargetScope = null);

/// <summary>Parses and validates a scan run's <c>scope</c> JSON.</summary>
public static class ScanScopeParser
{
	/// <summary>
	/// Parses <paramref name="scopeJson"/> into a <see cref="ScanScope"/>. Throws
	/// <see cref="FormatException"/> on malformed JSON or a non-GUID id -- the caller
	/// (<c>RunsController.CreateRun</c>) maps that to the documented 400
	/// <c>validation_error</c>, matching every other malformed-body path in this
	/// codebase (there is no 422 anywhere in api-contract.md or <c>ApiException</c>).
	/// </summary>
	public static ScanScope Parse(string scopeJson)
	{
		ArgumentNullException.ThrowIfNull(scopeJson);

		ScanScope? scope;
		try
		{
			scope = JsonSerializer.Deserialize<ScanScope>(scopeJson);
		}
		catch (JsonException exception)
		{
			throw new FormatException($"scope is not valid JSON: {exception.Message}", exception);
		}

		return scope ?? new ScanScope(null, null, null);
	}
}
