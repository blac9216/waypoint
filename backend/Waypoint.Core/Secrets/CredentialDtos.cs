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

namespace Waypoint.Core.Secrets;

/// <summary>
/// The ONLY credential shape that ever leaves the API (ADR-0005 / security.md
/// control 3: write-only, enforced at the serialization layer). There is no secret
/// field to mask because none exists -- <c>CredentialResponseHasNoSecretFieldTests</c>
/// walks this type and fails the build's test run if one ever appears.
/// </summary>
public sealed record CredentialResponse(
	Guid Id,
	string Name,
	string CredentialType,
	string Owner,
	string Health,
	bool HasSecret,
	long UsedByJobCount,
	DateTimeOffset? RotatedAt,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>Create request: metadata plus optional initial secret material (in only; UTF-8).</summary>
public sealed record CredentialCreateRequest(string? Name, string? CredentialType, string? Owner, string? Secret);

/// <summary>Update request: rename and/or rotate. A non-null <paramref name="Secret"/> replaces the stored blob and stamps <c>rotated_at</c>.</summary>
public sealed record CredentialUpdateRequest(string? Name, string? Secret);
