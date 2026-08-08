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

namespace Waypoint.Core.ConfigDocs;

/// <summary>
/// The closed <c>kind</c> set (docs/api-contract.md `/config-docs`: "Filter by kind
/// (input|attestation|remediation-input)"; docs/domain-model.md "STIG configuration
/// documents").
/// </summary>
public static class ConfigDocKinds
{
	public const string Input = "input";
	public const string Attestation = "attestation";
	public const string RemediationInput = "remediation-input";

	public static readonly IReadOnlyCollection<string> All = [Input, Attestation, RemediationInput];
}

/// <summary>
/// The closed <c>layer_type</c> set -- Global -> Site -> Target
/// (docs/domain-model.md: "All three resolve through three layers").
/// </summary>
public static class ConfigDocLayers
{
	public const string Global = "global";
	public const string Site = "site";
	public const string Target = "target";

	public static readonly IReadOnlyCollection<string> All = [Global, Site, Target];
}

/// <summary>
/// A config-doc's identity row -- the (kind, profile, layer) slot -- plus its
/// current-version pointer. The actual YAML bodies live in <see cref="ConfigDocVersion"/>
/// rows, one per save, never mutated (docs/domain-model.md: "Every save creates a
/// version with author + timestamp").
/// </summary>
public sealed record ConfigDoc(
	Guid Id,
	string Kind,
	string Profile,
	string LayerType,
	Guid? LayerRef,
	int CurrentVersion,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt);

/// <summary>One immutable version of a config-doc's body (docs/api-contract.md `/config-docs/{id}/versions`: "Full history -- the auditor answer").</summary>
public sealed record ConfigDocVersion(
	Guid Id,
	Guid DocId,
	int Version,
	string Author,
	string BodyYaml,
	DateTimeOffset CreatedAt);

/// <summary>A config-doc joined with its latest version's body -- what GET /config-docs/{id} returns.</summary>
public sealed record ConfigDocWithLatestVersion(ConfigDoc Doc, ConfigDocVersion LatestVersion);

public enum ConfigDocSaveOutcome
{
	Ok,
	InvalidKind,
	InvalidLayer,
}
