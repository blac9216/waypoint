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

namespace Waypoint.Core.ComplianceContent.SemanticImport;

/// <summary>
/// One normalized candidate entry produced by <see cref="VendorHierarchyInterpreter"/>
/// from a single <see cref="VendorContentEntry"/> -- issue #729 AC "Representative
/// vSphere, VCSA, NSX, Photon, Aria, and vIDM layouts classify correctly by
/// product/version/release/kind/component". This is catalog-shaped EVIDENCE, not
/// catalog authority: <see cref="SemanticImportReconciler"/> still has to prove every
/// field against the closed <see cref="CatalogVocabulary"/> sets before anything here
/// can become a <c>catalog_*</c> row (#730/#731). Interpretation never guesses past what
/// the vendor repository hierarchy and manifest actually declare -- an ambiguous or
/// unrecognized layout is a rejection (<see cref="SemanticImportReconciler"/>), never a
/// best-effort classification.
/// </summary>
public sealed record SemanticCandidate(
	string ProfileKey,
	string VendorFamily,
	string ProductVersionKey,
	string Kind,
	string ComponentKey,
	string ReleaseKey,
	string DisplayName,
	string Transport,
	string SelectorKind,
	string? SelectorName,
	bool IsAggregate,
	string? Title,
	string? ManifestVersion,
	IReadOnlyList<InspecManifestInput> Inputs,
	IReadOnlyList<string> Supports,
	IReadOnlyList<string> Depends,
	string ContentDigest)
{
	/// <summary>
	/// Whether this candidate could ever be selected for execution (issue #729 AC
	/// "Aggregate and unsupported profiles cannot be selected for execution"). An
	/// aggregate profile groups leaves rather than executing itself; a non-aggregate
	/// candidate becomes selectable only after <see cref="SemanticImportReconciler"/>
	/// also proves its vocabulary is closed-set valid.
	/// </summary>
	public bool IsExecutableLeaf => !IsAggregate;
}
