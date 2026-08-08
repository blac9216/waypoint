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

namespace Waypoint.Core.Sites;

/// <summary>
/// A site as stored (docs/domain-model.md "Site"): the top-level grouping, roughly "an
/// enclave's VMware estate." <see cref="StigmanOverrideJson"/> is the optional per-site
/// STIG Manager connection override (docs/api-contract.md "STIG Manager"), carried as
/// raw JSON text -- the same "JSON string, not a nested object" convention
/// <c>RunResponse.Scope</c> and <c>DepotArtifact.MetadataJson</c> already use for a
/// JSONB column on the wire.
/// </summary>
public sealed record Site(
	Guid Id,
	string Name,
	string? Description,
	string? StigmanOverrideJson,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>Create request: name is required; description/stigman_override are optional.</summary>
public sealed record SiteCreateRequest(string? Name, string? Description, string? StigmanOverrideJson);

/// <summary>
/// Update request: every field is a full replacement when present. A null
/// <see cref="Name"/> leaves the name unchanged; <see cref="Description"/> and
/// <see cref="StigmanOverrideJson"/> are likewise only replaced when supplied
/// (see <see cref="Waypoint.Api.Controllers.SitesController"/> for exact semantics).
/// </summary>
public sealed record SiteUpdateRequest(string? Name, string? Description, string? StigmanOverrideJson);

public enum SiteWriteOutcome
{
	Ok,
	NotFound,
	NameTaken,
}

public enum SiteDeleteOutcome
{
	Deleted,
	NotFound,

	/// <summary>The site still has targets; deleting it would silently orphan or cascade-delete them without confirmation.</summary>
	InUse,
}
