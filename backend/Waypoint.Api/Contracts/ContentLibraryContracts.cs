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
using Waypoint.Core.ContentLibraries;

namespace Waypoint.Api.Contracts;

/// <summary>Response body for a content library (issue #1391).</summary>
public sealed record ContentLibraryResponse(
	[property: JsonPropertyName("id")]
	Guid Id,

	[property: JsonPropertyName("name")]
	string Name,

	[property: JsonPropertyName("disk_path")]
	string DiskPath,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static ContentLibraryResponse FromDomain(ContentLibrary library)
	{
		ArgumentNullException.ThrowIfNull(library);
		return new ContentLibraryResponse(library.Id, library.Name, library.DiskPath, library.CreatedAt, library.UpdatedAt);
	}
}

/// <summary>Request body for <c>POST /api/v1/content-libraries</c>.</summary>
public sealed record ContentLibraryCreateBody(
	[property: JsonPropertyName("name")]
	string? Name);
