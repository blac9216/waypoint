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
using Waypoint.Core.ConfigDocs;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for a config-doc, latest version inlined (docs/api-contract.md
/// `/config-docs/{id}`: GET returns the doc; PUT returns the new version). <c>layer</c>
/// rides as the single <c>global|site:{id}|target:{id}</c> string the contract's list
/// filter documents, not the two split columns the row is actually stored under.
/// </summary>
public sealed record ConfigDocResponse(
	[property: JsonPropertyName("id")]
	Guid Id,

	[property: JsonPropertyName("kind")]
	string Kind,

	[property: JsonPropertyName("profile")]
	string Profile,

	[property: JsonPropertyName("layer")]
	string Layer,

	[property: JsonPropertyName("version")]
	int Version,

	[property: JsonPropertyName("author")]
	string Author,

	[property: JsonPropertyName("body")]
	string Body,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt)
{
	public static ConfigDocResponse FromDomain(ConfigDoc doc, ConfigDocVersion version)
	{
		ArgumentNullException.ThrowIfNull(doc);
		ArgumentNullException.ThrowIfNull(version);
		return new ConfigDocResponse(
			doc.Id, doc.Kind, doc.Profile, FormatLayer(doc.LayerType, doc.LayerRef),
			version.Version, version.Author, version.BodyYaml, doc.CreatedAt, doc.UpdatedAt);
	}

	internal static string FormatLayer(string layerType, Guid? layerRef) =>
		layerRef.HasValue ? $"{layerType}:{layerRef}" : layerType;
}

/// <summary>One row of <c>GET /config-docs/{id}/versions</c> -- the auditor answer: who changed this, and when.</summary>
public sealed record ConfigDocVersionResponse(
	[property: JsonPropertyName("version")]
	int Version,

	[property: JsonPropertyName("author")]
	string Author,

	[property: JsonPropertyName("body")]
	string Body,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt)
{
	public static ConfigDocVersionResponse FromDomain(ConfigDocVersion version)
	{
		ArgumentNullException.ThrowIfNull(version);
		return new ConfigDocVersionResponse(version.Version, version.Author, version.BodyYaml, version.CreatedAt);
	}
}

/// <summary>
/// Request body for <c>PUT /api/v1/config-docs/{id}</c>: the only write path
/// (docs/api-contract.md: "PUT creates a new immutable version"). When <c>id</c> names
/// an existing doc, <c>kind</c>/<c>profile</c>/<c>layer</c> are ignored (the slot is
/// fixed); when it does not, this is the write that creates the slot at @v1 and those
/// three fields are required.
/// </summary>
public sealed record ConfigDocSaveBody(
	[property: JsonPropertyName("kind")]
	string? Kind,

	[property: JsonPropertyName("profile")]
	string? Profile,

	[property: JsonPropertyName("layer")]
	string? Layer,

	[property: JsonPropertyName("body")]
	string? Body);

/// <summary>
/// One kind's entry in the <c>GET /config-docs/resolve</c> response -- the EFFECTIVE card
/// (docs/api-contract.md: "resolved value + supplying layer"). <c>layer</c>/<c>body</c>/
/// <c>doc_id</c>/<c>version</c>/<c>author</c>/<c>updated_at</c> are all null when no layer
/// has a doc for this (kind, profile) at all (prototype: input layer "not defined ...
/// inherits site value" collapses further to nothing defined anywhere). <c>attestation_expired</c>
/// is only ever true for <c>kind == "attestation"</c> -- docs/domain-model.md: "the control
/// reports Open, the run logs a WARN, and Results lists expired attestations explicitly."
/// </summary>
public sealed record ConfigDocResolutionResponse(
	[property: JsonPropertyName("kind")]
	string Kind,

	[property: JsonPropertyName("profile")]
	string Profile,

	[property: JsonPropertyName("layer")]
	string? Layer,

	[property: JsonPropertyName("body")]
	string? Body,

	[property: JsonPropertyName("doc_id")]
	Guid? DocId,

	[property: JsonPropertyName("version")]
	int? Version,

	[property: JsonPropertyName("author")]
	string? Author,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset? UpdatedAt,

	[property: JsonPropertyName("attestation_expired")]
	bool AttestationExpired,

	[property: JsonPropertyName("attestation_expires_at")]
	DateTimeOffset? AttestationExpiresAt)
{
	public static ConfigDocResolutionResponse FromDomain(Waypoint.Core.ConfigDocs.ConfigDocResolution resolution)
	{
		ArgumentNullException.ThrowIfNull(resolution);
		return new ConfigDocResolutionResponse(
			resolution.Kind, resolution.Profile, resolution.Layer, resolution.Body,
			resolution.DocId, resolution.Version, resolution.Author, resolution.UpdatedAt,
			resolution.AttestationExpired, resolution.AttestationExpiresAt);
	}
}
