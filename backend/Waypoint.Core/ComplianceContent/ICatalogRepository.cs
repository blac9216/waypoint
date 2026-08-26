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

/// <summary>
/// Storage for the normalized compliance catalog (migration 0050): the versioned
/// identity tree from source revision down through execution profile, credential
/// requirements, benchmark references, and remediation definitions. Issue #728 scope
/// only -- content acquisition/sync (#729), candidate/XCCDF pipelines (#730), and
/// baseline activation (#731) each add their own repositories on top of this read
/// surface rather than extending it.
///
/// Every row this repository writes is catalog-authored (reviewed product code
/// shipped by appliance update, ADR-0022) -- there is no "upsert from an untrusted
/// source" path here; #729/#730 own that once content acquisition exists. Write
/// methods exist so catalog seeding (a future appliance-update mechanism, not part of
/// #728) and this issue's own tests have a supported entry point.
/// </summary>
public interface ICatalogRepository
{
	Task<CatalogSourceRevision> UpsertSourceRevisionAsync(string revisionKey, string? description, CancellationToken cancellationToken);

	Task<CatalogProduct> UpsertProductAsync(Guid sourceRevisionId, string vendor, string productKey, string displayName, CancellationToken cancellationToken);

	Task<CatalogProductVersion> UpsertProductVersionAsync(Guid productId, string versionKey, string displayName, CancellationToken cancellationToken);

	Task<CatalogContentRelease> UpsertContentReleaseAsync(
		Guid sourceRevisionId, string kind, string releaseKey, string displayName, CancellationToken cancellationToken);

	Task<CatalogComponent> UpsertComponentAsync(Guid productVersionId, CatalogComponentDefinition definition, CancellationToken cancellationToken);

	Task<CatalogReportGroup> UpsertReportGroupAsync(string groupKey, string displayName, int priority, CancellationToken cancellationToken);

	/// <summary>
	/// Creates one execution profile binding a component to a content release. Throws
	/// <see cref="InvalidOperationException"/> if the (component, content release) pair
	/// already exists -- execution profiles are immutable identity, never updated in
	/// place (issue #728 AC "historical revisions referenced by plans must be protected
	/// from accidental deletion").
	/// </summary>
	Task<CatalogExecutionProfile> CreateExecutionProfileAsync(
		Guid componentId,
		Guid contentReleaseId,
		Guid reportGroupId,
		string profileVersion,
		string outputKind,
		CancellationToken cancellationToken);

	Task<CatalogCredentialRequirement> AddCredentialRequirementAsync(
		Guid executionProfileId, string purpose, bool isRequired, CancellationToken cancellationToken);

	Task<CatalogBenchmarkReference> SetBenchmarkReferenceAsync(
		Guid executionProfileId, string benchmarkKey, string benchmarkVersion, CancellationToken cancellationToken);

	Task<CatalogRemediationDefinition> SetRemediationDefinitionAsync(
		Guid executionProfileId, bool isSupported, string? mechanismNote, CancellationToken cancellationToken);

	/// <summary>All products, ordered by vendor then product key.</summary>
	Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken);

	/// <summary>All versions for one product, ordered by version key.</summary>
	Task<IReadOnlyList<CatalogProductVersion>> ListProductVersionsAsync(Guid productId, CancellationToken cancellationToken);

	/// <summary>All components for one product version (every level of the parent/child tree), ordered by component key.</summary>
	Task<IReadOnlyList<CatalogComponent>> ListComponentsAsync(Guid productVersionId, CancellationToken cancellationToken);

	/// <summary>
	/// Every execution profile for one component, fully joined for planner/UI
	/// consumption. Multiple rows mean multiple content releases target the same
	/// component (issue #728 AC "multi-release components").
	/// </summary>
	Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListExecutionProfilesByComponentAsync(Guid componentId, CancellationToken cancellationToken);

	/// <summary>Single execution profile detail by id, or null when unknown.</summary>
	Task<CatalogExecutionProfileDetail?> GetExecutionProfileAsync(Guid executionProfileId, CancellationToken cancellationToken);

	/// <summary>Every execution profile in the catalog, fully joined -- the backing read for <c>GET /catalog/products</c> (docs/api-contract.md).</summary>
	Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListAllExecutionProfilesAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Records one declared input for an execution profile (issue #728 AC "declared and
	/// consumed inputs ... queryable", migration 0051). Upserts by (execution_profile_id,
	/// name) -- a re-import of the same content re-declaring the same input is a no-op,
	/// not a duplicate row.
	/// </summary>
	Task<CatalogDeclaredInput> UpsertDeclaredInputAsync(
		Guid executionProfileId, string name, string? inputType, bool isRequired, CancellationToken cancellationToken);

	/// <summary>All declared inputs for one execution profile, ordered by name.</summary>
	Task<IReadOnlyList<CatalogDeclaredInput>> ListDeclaredInputsAsync(Guid executionProfileId, CancellationToken cancellationToken);

	/// <summary>
	/// Persists one <c>SemanticImportReport</c> header (issue #729 deliverable 5,
	/// migration 0051). Always inserts a new row -- two distinct pull attempts over
	/// byte-identical content are two distinct provenance events (ADR-0022), never
	/// deduplicated at the report-header level.
	/// </summary>
	Task<CatalogImportReport> RecordImportReportAsync(
		string sourceCommit, string sourceDigest, int acceptedCount, int warningCount, int rejectedCount, CancellationToken cancellationToken);

	/// <summary>
	/// Persists one entry (accepted/warning/rejected) of an already-recorded import
	/// report. <paramref name="executionProfileId"/> is non-null only for an accepted
	/// entry that promotion successfully turned into a catalog execution profile.
	/// </summary>
	Task<CatalogImportReportEntry> RecordImportReportEntryAsync(
		Guid reportId, string disposition, string profileKey, string? reason, Guid? executionProfileId, CancellationToken cancellationToken);

	/// <summary>Import reports, newest first, bounded by <paramref name="limit"/>.</summary>
	Task<IReadOnlyList<CatalogImportReport>> ListImportReportsAsync(int limit, CancellationToken cancellationToken);

	/// <summary>All entries of one import report, ordered by profile key.</summary>
	Task<IReadOnlyList<CatalogImportReportEntry>> ListImportReportEntriesAsync(Guid reportId, CancellationToken cancellationToken);

	/// <summary>
	/// Promotes one accepted <see cref="Waypoint.Core.ComplianceContent.SemanticImport.SemanticCandidate"/>
	/// into the migration 0050 catalog identity tree plus its declared inputs (issue
	/// #729 deliverable: "candidate promotion into the 0050 catalog tables"). Additive
	/// only (ADR-0022 "additive acquisition"): every level is upserted by natural key
	/// (source revision, product, product version, component, content release), and the
	/// terminal execution-profile row is created only if it does not already exist for
	/// this (component, content release) pair -- an identical re-import is deduplicated
	/// to the SAME execution profile id rather than creating a sibling, and this method
	/// never mutates an execution profile's already-recorded identity once created. An
	/// aggregate candidate (<see cref="Waypoint.Core.ComplianceContent.SemanticImport.SemanticCandidate.IsExecutableLeaf"/>
	/// false) is not promoted -- callers should not invoke this for aggregate candidates
	/// (see <see cref="Waypoint.Core.ComplianceContent.SemanticImport.SemanticCandidate"/>
	/// AC "aggregate and unsupported profiles cannot be selected for execution").
	/// </summary>
	Task<CatalogPromotionOutcome> PromoteCandidateAsync(
		Waypoint.Core.ComplianceContent.SemanticImport.SemanticCandidate candidate,
		CatalogPromotionRequest request,
		CancellationToken cancellationToken);
}
