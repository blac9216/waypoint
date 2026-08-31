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
using Waypoint.Core.Secrets;

namespace Waypoint.Api.Contracts;

/// <summary>
/// One repo store -&gt; credential binding (issue #1517, migration 0103).
/// <see cref="CredentialId"/> is a reference only -- never secret material, same rule
/// <see cref="TargetCredentialBindingResponse.CredentialId"/> follows.
/// </summary>
public sealed record RepoCredentialBindingResponse(
	[property: JsonPropertyName("store")]
	string Store,

	[property: JsonPropertyName("credential_ref")]
	Guid CredentialId,

	[property: JsonPropertyName("credential_name")]
	string? CredentialName,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static RepoCredentialBindingResponse FromDomain(RepoCredentialBinding binding, string? credentialName = null)
	{
		ArgumentNullException.ThrowIfNull(binding);
		return new RepoCredentialBindingResponse(binding.Store, binding.CredentialId, credentialName, binding.CreatedAt, binding.UpdatedAt);
	}
}

/// <summary>Request body for <c>PUT /api/v1/repo-credentials/{store}</c> -- sets (creates or replaces) the binding for that store.</summary>
public sealed record RepoCredentialBindingSetBody(
	[property: JsonPropertyName("credential_ref")]
	Guid? CredentialId);
