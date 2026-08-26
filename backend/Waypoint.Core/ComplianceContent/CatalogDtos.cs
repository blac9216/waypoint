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

namespace Waypoint.Core.ComplianceContent;

/// <summary>Provenance record for a reviewed catalog revision (<c>catalog_source_revisions</c>, migration 0050).</summary>
public sealed record CatalogSourceRevision(Guid Id, string RevisionKey, string? Description, DateTimeOffset RecordedAt);

/// <summary>A catalog-authored product identity (<c>catalog_products</c>). Data-driven -- ADR-0013.</summary>
public sealed record CatalogProduct(Guid Id, Guid SourceRevisionId, string Vendor, string ProductKey, string DisplayName, DateTimeOffset CreatedAt);

/// <summary>One exact product version (<c>catalog_product_versions</c>). No ranges -- ADR-0022.</summary>
public sealed record CatalogProductVersion(Guid Id, Guid ProductId, string VersionKey, string DisplayName, DateTimeOffset CreatedAt);

/// <summary>One exact vendor content revision for a closed <see cref="CatalogKinds"/> value (<c>catalog_content_releases</c>).</summary>
public sealed record CatalogContentRelease(Guid Id, Guid SourceRevisionId, string Kind, string ReleaseKey, string DisplayName, DateTimeOffset CreatedAt);

/// <summary>
/// One executable component scoped to a product version (<c>catalog_components</c>).
/// <see cref="ParentComponentId"/> builds the named-sub-service tree (e.g. VCSA STIG's
/// EAM/Lookup/PostgreSQL/... services); <see cref="SelectorName"/> is non-null only
/// when <see cref="SelectorKind"/> is <see cref="CatalogSelectorKinds.Service"/>.
/// </summary>
public sealed record CatalogComponent(
	Guid Id,
	Guid ProductVersionId,
	Guid? ParentComponentId,
	string ComponentKey,
	string DisplayName,
	string Transport,
	string SelectorKind,
	string? SelectorName,
	DateTimeOffset CreatedAt);

/// <summary>Closed priority/report-group vocabulary row (<c>catalog_report_groups</c>). Priority 1 (NSX STIG) through 6 (every SRG).</summary>
public sealed record CatalogReportGroup(Guid Id, string GroupKey, string DisplayName, int Priority, DateTimeOffset CreatedAt);

/// <summary>
/// The row callers actually query: one component bound to one exact content release
/// (<c>catalog_execution_profiles</c>). Every row shipped by this migration is
/// vendor-derived/catalog-authored -- <see cref="IsOperatorOverride"/> is always false
/// today (ADR-0022 forbids operator catalog mappings); activation state lives one
/// layer up in #731's <c>baselines</c> table, never here.
/// </summary>
public sealed record CatalogExecutionProfile(
	Guid Id,
	Guid ComponentId,
	Guid ContentReleaseId,
	Guid ReportGroupId,
	string ProfileVersion,
	bool IsOperatorOverride,
	string OutputKind,
	DateTimeOffset CreatedAt);

/// <summary>Required credential purpose for one execution profile (<c>catalog_credential_requirements</c>).</summary>
public sealed record CatalogCredentialRequirement(Guid Id, Guid ExecutionProfileId, string Purpose, bool IsRequired, DateTimeOffset CreatedAt);

/// <summary>XCCDF/benchmark identity for a STIG execution profile (<c>catalog_benchmark_references</c>). SRG profiles have none.</summary>
public sealed record CatalogBenchmarkReference(Guid Id, Guid ExecutionProfileId, string BenchmarkKey, string BenchmarkVersion, DateTimeOffset CreatedAt);

/// <summary>Remediation-capability metadata for one execution profile (<c>catalog_remediation_definitions</c>). Execution itself is out of scope (epic #15).</summary>
public sealed record CatalogRemediationDefinition(Guid Id, Guid ExecutionProfileId, bool IsSupported, string? MechanismNote, DateTimeOffset CreatedAt);

/// <summary>
/// A fully joined, read-oriented projection of one execution profile and everything a
/// planner/UI consumer needs without a second round trip: the owning product/version/
/// component identity, content release, credential requirements, benchmark reference
/// (STIG only), remediation capability, and declared profile inputs (issue #728 AC
/// "declared and consumed inputs ... queryable", delivered by the #729 persistence
/// slice/migration 0051). This is the shape catalog read repositories/APIs return --
/// not a 1:1 mirror of any single table.
/// </summary>
public sealed record CatalogExecutionProfileDetail(
	CatalogExecutionProfile ExecutionProfile,
	CatalogComponent Component,
	CatalogProductVersion ProductVersion,
	CatalogProduct Product,
	CatalogContentRelease ContentRelease,
	CatalogReportGroup ReportGroup,
	IReadOnlyList<CatalogCredentialRequirement> CredentialRequirements,
	CatalogBenchmarkReference? BenchmarkReference,
	CatalogRemediationDefinition? RemediationDefinition,
	IReadOnlyList<CatalogDeclaredInput> DeclaredInputs);

/// <summary>
/// A candidate component definition to validate/insert -- the unit
/// <see cref="CatalogVocabularyValidator.ValidateComponent"/> checks before it ever
/// reaches storage.
/// </summary>
public sealed record CatalogComponentDefinition(
	string ComponentKey,
	string DisplayName,
	string Transport,
	string SelectorKind,
	string? SelectorName,
	Guid? ParentComponentId);
