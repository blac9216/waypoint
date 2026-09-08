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

/// <summary>Request body for <c>POST /api/v1/consumer-views</c> (issue #1464).</summary>
public sealed record CreateConsumerViewRequest(
	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("platforms")]
	IReadOnlyList<string> Platforms,

	[property: JsonPropertyName("is_default")]
	bool? IsDefault);

/// <summary>
/// Request body for <c>PUT /api/v1/consumer-views/{id}</c>. Every field is optional --
/// an omitted field leaves that column unchanged, same convention as
/// <see cref="UpdateEsxAcquisitionSubscriptionRequest"/>.
/// </summary>
public sealed record UpdateConsumerViewRequest(
	[property: JsonPropertyName("name")]
	string? Name,

	[property: JsonPropertyName("platforms")]
	IReadOnlyList<string>? Platforms,

	[property: JsonPropertyName("is_default")]
	bool? IsDefault);

/// <summary>Response body for the consumer view CRUD endpoints.</summary>
public sealed record ConsumerViewResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("platforms")]
	IReadOnlyList<string> Platforms,

	[property: JsonPropertyName("is_default")]
	bool IsDefault,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static ConsumerViewResponse FromDomain(ConsumerView view) => new(
		view.Id.ToString(),
		view.Name,
		view.Platforms,
		view.IsDefault,
		view.CreatedAt,
		view.UpdatedAt);
}
