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
using Waypoint.Core.Downloads;

namespace Waypoint.Api.Contracts;

/// <summary>Response body for <c>GET /api/v1/downloads/esx/platforms</c> (issue #1470).</summary>
public sealed record EsxPlatformVocabularyResponse(
	[property: JsonPropertyName("platforms")]
	IReadOnlyList<string> Platforms);

/// <summary>Request body for <c>POST /api/v1/downloads/esx/subscriptions</c>.</summary>
public sealed record CreateEsxAcquisitionSubscriptionRequest(
	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("selected_platforms")]
	IReadOnlyList<string> SelectedPlatforms,

	[property: JsonPropertyName("enabled")]
	bool? Enabled);

/// <summary>
/// Request body for <c>PATCH /api/v1/downloads/esx/subscriptions/{id}</c>. Every field
/// is optional -- an omitted field leaves that column unchanged (issue #1470 AC:
/// disabling via <c>enabled: false</c> never touches <see cref="SelectedPlatforms"/>
/// or <see cref="Name"/>).
/// </summary>
public sealed record UpdateEsxAcquisitionSubscriptionRequest(
	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("selected_platforms")]
	IReadOnlyList<string>? SelectedPlatforms,

	[property: JsonPropertyName("enabled")]
	bool? Enabled);

/// <summary>Response body for the ESX acquisition subscription CRUD endpoints.</summary>
public sealed record EsxAcquisitionSubscriptionResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("selected_platforms")]
	IReadOnlyList<string> SelectedPlatforms,

	[property: JsonPropertyName("enabled")]
	bool Enabled,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static EsxAcquisitionSubscriptionResponse FromDomain(EsxAcquisitionSubscription subscription) => new(
		subscription.Id.ToString(),
		subscription.Name,
		subscription.SelectedPlatforms,
		subscription.Enabled,
		subscription.CreatedAt,
		subscription.UpdatedAt);
}
