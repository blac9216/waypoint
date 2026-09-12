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

/// <summary>
/// Issue #1450: the wire request body for <c>POST</c>/<c>PUT /api/v1/subscriptions</c>.
/// Adopting a preset (a non-null <c>preset_id</c> on create) seeds <c>line_granularity</c>
/// and <c>anchor_version</c> from the preset -- any value supplied here for those two
/// fields is ignored on that path (<see cref="Waypoint.Core.Subscriptions.SubscriptionService.CreateAsync"/>).
/// </summary>
public sealed record SubscriptionWriteRequest(
	[property: JsonPropertyName("product")] string? Product,

	[property: JsonPropertyName("lane")] string? Lane,

	[property: JsonPropertyName("line_granularity")] string? LineGranularity,

	[property: JsonPropertyName("anchor_version")] string? AnchorVersion,

	[property: JsonPropertyName("preset_id")] Guid? PresetId,

	[property: JsonPropertyName("refresh_window_days")] int? RefreshWindowDays,

	[property: JsonPropertyName("retention_override_days")] int? RetentionOverrideDays,

	[property: JsonPropertyName("is_enabled")] bool? IsEnabled);

/// <summary>Response body for the <c>/api/v1/subscriptions</c> resource.</summary>
public sealed record SubscriptionResponse(
	Guid Id,

	string Product,

	string Lane,

	[property: JsonPropertyName("line_granularity")] string LineGranularity,

	[property: JsonPropertyName("anchor_version")] string AnchorVersion,

	[property: JsonPropertyName("preset_id")] Guid? PresetId,

	[property: JsonPropertyName("refresh_window_days")] int? RefreshWindowDays,

	[property: JsonPropertyName("retention_override_days")] int? RetentionOverrideDays,

	[property: JsonPropertyName("is_enabled")] bool IsEnabled,

	[property: JsonPropertyName("created_at")] string CreatedAt,

	[property: JsonPropertyName("updated_at")] string UpdatedAt);
