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

namespace Waypoint.Api.Contracts;

/// <summary>Request body for <c>POST /api/v1/presets/{id}/clone</c> (issue #1450). <c>name</c> is optional; omitted defaults to "&lt;source name&gt; (custom)".</summary>
public sealed record PresetCloneRequest([property: JsonPropertyName("name")] string? Name);

/// <summary>Request body for <c>PUT /api/v1/presets/{id}</c> on a custom clone (issue #1450). A <c>null</c> field leaves the existing value unchanged.</summary>
public sealed record PresetUpdateRequest(
	[property: JsonPropertyName("name")] string? Name,

	[property: JsonPropertyName("line_granularity")] string? LineGranularity,

	[property: JsonPropertyName("anchor_version")] string? AnchorVersion);

/// <summary>Response body for the <c>/api/v1/presets</c> resource.</summary>
public sealed record PresetResponse(
	Guid Id,

	string Stack,

	string Generation,

	string Name,

	[property: JsonPropertyName("line_granularity")] string LineGranularity,

	[property: JsonPropertyName("anchor_version")] string? AnchorVersion,

	[property: JsonPropertyName("is_custom")] bool IsCustom,

	[property: JsonPropertyName("source_preset_id")] Guid? SourcePresetId,

	[property: JsonPropertyName("created_at")] string CreatedAt,

	[property: JsonPropertyName("updated_at")] string UpdatedAt);
