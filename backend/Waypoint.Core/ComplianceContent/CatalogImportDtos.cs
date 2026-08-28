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

/// <summary>The closed <c>disposition</c> vocabulary for <c>catalog_import_report_entries</c> (migration 0051).</summary>
public static class CatalogImportEntryDispositions
{
	public const string Accepted = "accepted";
	public const string Warning = "warning";
	public const string Rejected = "rejected";

	public static readonly IReadOnlyCollection<string> All = [Accepted, Warning, Rejected];

	public static bool IsValid(string? disposition) => disposition is not null && All.Contains(disposition);
}

/// <summary>
/// A persisted <c>SemanticImportReport</c> header (<c>catalog_import_reports</c>,
/// migration 0051) -- issue #729 deliverable 5, "emit a deterministic import report
/// with source commit/digest, accepted entries, warnings, and rejected entries", now
/// durable rather than only an in-process value.
/// </summary>
public sealed record CatalogImportReport(
	Guid Id,
	string SourceCommit,
	string SourceDigest,
	int AcceptedCount,
	int WarningCount,
	int RejectedCount,
	DateTimeOffset RecordedAt);

/// <summary>
/// One persisted entry of a <see cref="CatalogImportReport"/>
/// (<c>catalog_import_report_entries</c>). <see cref="ExecutionProfileId"/> is populated
/// only for an accepted entry that candidate promotion successfully turned into a
/// <c>catalog_execution_profiles</c> row; it is always null for warning/rejected
/// entries and for an accepted entry that failed promotion (e.g. its whole-appliance
/// aggregate parent, or a validation failure promotion itself surfaces).
/// </summary>
public sealed record CatalogImportReportEntry(
	Guid Id,
	Guid ReportId,
	string Disposition,
	string ProfileKey,
	string? Reason,
	Guid? ExecutionProfileId,
	DateTimeOffset CreatedAt);

/// <summary>
/// A declared InSpec input to record for an execution profile, one row per
/// <c>InspecManifestInput</c> the semantic importer parsed off the profile's
/// <c>inspec.yml</c> (issue #728 AC "declared and consumed inputs ... queryable").
/// </summary>
public sealed record CatalogDeclaredInputUpsert(string Name, string? InputType, bool IsRequired);

/// <summary>
/// A persisted declared input (<c>catalog_declared_inputs</c>, migration 0051).
/// </summary>
public sealed record CatalogDeclaredInput(Guid Id, Guid ExecutionProfileId, string Name, string? InputType, bool IsRequired, DateTimeOffset CreatedAt);

/// <summary>
/// One accepted <see cref="Waypoint.Core.ComplianceContent.SemanticImport.SemanticCandidate"/>
/// to promote into the migration 0050 catalog identity tree. Fields not derivable from
/// the candidate alone (source revision key, product vendor/display names, content
/// release display name, report group priority, output kind) are supplied by the
/// caller (<c>ContentPullJobHandler</c>) because they come from catalog-authored
/// classification tables (docs/compliance-parity.md), not from vendor content itself --
/// promotion never invents catalog authority the importer's evidence does not carry
/// (see <see cref="Waypoint.Core.ComplianceContent.SemanticImport.SemanticCandidate"/>'s
/// own "catalog-shaped EVIDENCE, not catalog authority" doc comment).
/// </summary>
/// <param name="Vendor">
/// The <c>catalog_products.vendor</c> NATURAL-KEY value -- must be one of
/// <see cref="CatalogVendors"/>'s closed set (today only <see cref="CatalogVendors.VMware"/>),
/// never a human-readable display string. Issue #1007: this column participates in
/// <c>catalog_products_vendor_key_unique UNIQUE (vendor, product_key)</c>, the constraint
/// <see cref="Waypoint.Core.ComplianceContent.ICatalogRepository.PromoteCandidateAsync"/>'s
/// upsert relies on to attach to an existing (seeded, when present) product row instead
/// of creating a duplicate identity tree -- a display string here silently defeats that
/// upsert instead of failing loudly, so callers must pass the same literal the seed
/// migrations (0064/0067/0069) write. <see cref="ProductDisplayName"/> is the free-text
/// cosmetic counterpart and never part of any natural key.
/// </param>
public sealed record CatalogPromotionRequest(
	string SourceRevisionKey,
	string Vendor,
	string ProductDisplayName,
	string ProductVersionDisplayName,
	string ContentReleaseDisplayName,
	string ReportGroupKey,
	string ReportGroupDisplayName,
	int ReportGroupPriority,
	string OutputKind);

/// <summary>
/// The outcome of promoting one accepted candidate: the execution profile that resulted
/// (or null when promotion itself rejected the candidate, e.g. a whole-appliance
/// aggregate parent with no runnable leaf, or an unresolvable vocabulary conflict that
/// interpretation/reconciliation could not have already caught), plus an optional
/// diagnostic reason mirroring <c>SemanticImportRejected</c>'s shape for a promotion-time
/// rejection.
/// </summary>
public sealed record CatalogPromotionOutcome(Guid? ExecutionProfileId, string? RejectionReason);
