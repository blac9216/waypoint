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
/// later slice, populates this from cached inventory). Profile/product filtering is
/// out of scope for this slice (#273): it constrains *what a scan job does*, not
/// *which targets get a job row*, and nothing here reads it yet -- carried on the raw
/// <see cref="RunSummary.ScopeJson"/> field for the execution slice (#274) to parse.
/// </summary>
public sealed record ScanScope(
	[property: JsonPropertyName("site_id")] Guid? SiteId,
	[property: JsonPropertyName("target_ids")] IReadOnlyList<Guid>? TargetIds);

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

		return scope ?? new ScanScope(null, null);
	}
}
