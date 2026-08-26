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
}
