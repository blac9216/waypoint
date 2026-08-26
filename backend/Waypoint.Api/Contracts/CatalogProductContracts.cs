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

using Waypoint.Core.ComplianceContent;

namespace Waypoint.Api.Contracts;

/// <summary>
/// Response body for one row of <c>GET /api/v1/catalog/products</c> and
/// <c>GET /api/v1/catalog/products/{id}</c> (docs/api-contract.md "Catalog, content
/// sources, and exact-version baselines": "the closed, versioned execution-catalog
/// vocabulary: supported products/exact versions, component selectors, transports,
/// credential purposes, priority, output semantics"). One row is one execution
/// profile -- a component bound to one exact content release -- fully joined with its
/// owning product/version identity so planner/UI consumers never need a second round
/// trip (issue #728 AC: "Component transport, selector, required purposes, inputs,
/// priority/report group, benchmark, and remediation capability are queryable").
///
/// This is deliberately a read-only reflection of catalog identity/capability data,
/// never an activation/baseline record (ADR-0022's <c>baselines</c> table, issue #731,
/// is layered on top and out of this issue's scope) -- there is no "active" flag here.
/// </summary>
public sealed record CatalogProductResponse(
	string ExecutionProfileId,
	string ProfileVersion,
	string OutputKind,
	CatalogProductSummary Product,
	CatalogProductVersionSummary ProductVersion,
	CatalogComponentSummary Component,
	CatalogContentReleaseSummary ContentRelease,
	CatalogReportGroupSummary ReportGroup,
	IReadOnlyList<CatalogCredentialRequirementSummary> CredentialRequirements,
	CatalogBenchmarkReferenceSummary? Benchmark,
	CatalogRemediationSummary? Remediation)
{
	public static CatalogProductResponse FromDomain(CatalogExecutionProfileDetail detail)
	{
		ArgumentNullException.ThrowIfNull(detail);
		return new CatalogProductResponse(
			detail.ExecutionProfile.Id.ToString(),
			detail.ExecutionProfile.ProfileVersion,
			detail.ExecutionProfile.OutputKind,
			new CatalogProductSummary(detail.Product.Id.ToString(), detail.Product.Vendor, detail.Product.ProductKey, detail.Product.DisplayName),
			new CatalogProductVersionSummary(detail.ProductVersion.Id.ToString(), detail.ProductVersion.VersionKey, detail.ProductVersion.DisplayName),
			new CatalogComponentSummary(
				detail.Component.Id.ToString(),
				detail.Component.ParentComponentId?.ToString(),
				detail.Component.ComponentKey,
				detail.Component.DisplayName,
				detail.Component.Transport,
				detail.Component.SelectorKind,
				detail.Component.SelectorName),
			new CatalogContentReleaseSummary(detail.ContentRelease.Id.ToString(), detail.ContentRelease.Kind, detail.ContentRelease.ReleaseKey, detail.ContentRelease.DisplayName),
			new CatalogReportGroupSummary(detail.ReportGroup.Id.ToString(), detail.ReportGroup.GroupKey, detail.ReportGroup.DisplayName, detail.ReportGroup.Priority),
			detail.CredentialRequirements
				.Select(requirement => new CatalogCredentialRequirementSummary(requirement.Purpose, requirement.IsRequired))
				.ToArray(),
			detail.BenchmarkReference is null
				? null
				: new CatalogBenchmarkReferenceSummary(detail.BenchmarkReference.BenchmarkKey, detail.BenchmarkReference.BenchmarkVersion),
			detail.RemediationDefinition is null
				? null
				: new CatalogRemediationSummary(detail.RemediationDefinition.IsSupported, detail.RemediationDefinition.MechanismNote));
	}
}

/// <summary>The catalog-authored product identity (vendor, product key/display name).</summary>
public sealed record CatalogProductSummary(string Id, string Vendor, string ProductKey, string DisplayName);

/// <summary>One exact product version -- no ranges (ADR-0022).</summary>
public sealed record CatalogProductVersionSummary(string Id, string VersionKey, string DisplayName);

/// <summary>
/// The executable component: <see cref="Transport"/> and <see cref="SelectorKind"/>/
/// <see cref="SelectorName"/> are the queryable-fields AC's "transport, selector".
/// <see cref="ParentComponentId"/> is non-null for a named sub-service under a parent
/// component (VCSA EAM/PostgreSQL/..., SDDC Manager nginx/...).
/// </summary>
public sealed record CatalogComponentSummary(
	string Id,
	string? ParentComponentId,
	string ComponentKey,
	string DisplayName,
	string Transport,
	string SelectorKind,
	string? SelectorName);

/// <summary>The exact vendor content revision (STIG|SRG -- distinct first-class kinds, issue #728 AC) this execution profile targets.</summary>
public sealed record CatalogContentReleaseSummary(string Id, string Kind, string ReleaseKey, string DisplayName);

/// <summary>The closed priority/report-group vocabulary row -- the queryable-fields AC's "priority/report group".</summary>
public sealed record CatalogReportGroupSummary(string Id, string GroupKey, string DisplayName, int Priority);

/// <summary>One required credential purpose -- the queryable-fields AC's "required purposes, inputs".</summary>
public sealed record CatalogCredentialRequirementSummary(string Purpose, bool IsRequired);

/// <summary>XCCDF/benchmark identity -- STIG-only, absent for SRG (the queryable-fields AC's "benchmark").</summary>
public sealed record CatalogBenchmarkReferenceSummary(string BenchmarkKey, string BenchmarkVersion);

/// <summary>Remediation-capability metadata -- the queryable-fields AC's "remediation capability". Execution itself is out of scope (epic #15).</summary>
public sealed record CatalogRemediationSummary(bool IsSupported, string? MechanismNote);
