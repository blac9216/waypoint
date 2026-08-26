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

/// <summary>Closed lifecycle vocabulary shared by <c>content_revisions</c> and <c>baselines</c> (migration 0055).</summary>
public static class ContentRevisionStatuses
{
	public const string Staged = "staged";
	public const string Activated = "activated";
	public const string Superseded = "superseded";
	public const string Rejected = "rejected";

	public static readonly IReadOnlyList<string> All = [Staged, Activated, Superseded, Rejected];
}

/// <summary>Closed lifecycle vocabulary for a <c>baselines</c> row (migration 0055) -- an ACTIVATION unit, distinct from the revision's own status.</summary>
public static class BaselineStatuses
{
	public const string Staged = "staged";
	public const string Active = "active";
	public const string Superseded = "superseded";
	public const string Rejected = "rejected";

	public static readonly IReadOnlyList<string> All = [Staged, Active, Superseded, Rejected];
}

/// <summary>
/// One immutable, digest-addressed staged filesystem snapshot (<c>content_revisions</c>,
/// migration 0055). <see cref="StagedRelativePath"/> is relative to
/// <see cref="ComplianceContentOptions.ContentPath"/>'s <c>revisions/</c> subdirectory
/// (see <c>ContentRevisionStager</c>) -- never an absolute path, so the same row is
/// portable across a restored/relocated content volume.
/// </summary>
public sealed record ContentRevision(
	Guid Id,
	string SourceCommit,
	string ContentDigest,
	string StagedRelativePath,
	string Status,
	bool GcEligible,
	DateTimeOffset StagedAt);

/// <summary>
/// One coherent, atomically-activatable set (<c>baselines</c>, migration 0055):
/// exactly one <see cref="ContentRevisionId"/> + one <see cref="CatalogExecutionProfileId"/>
/// + an optional <see cref="BenchmarkRevisionId"/> (STIG only -- ADR-0022, SRG has no
/// XCCDF). Only <see cref="BaselineStatuses.Active"/> is scan-eligible; a compatible
/// execution profile has at most one active baseline at a time (DB-enforced by a
/// partial unique index).
/// </summary>
public sealed record Baseline(
	Guid Id,
	Guid ContentRevisionId,
	Guid CatalogExecutionProfileId,
	Guid? BenchmarkRevisionId,
	string Status,
	DateTimeOffset? ActivatedAt,
	string? ActivatedBy,
	DateTimeOffset? SupersededAt,
	DateTimeOffset CreatedAt);

/// <summary>Outcome of a <see cref="IBaselineActivationService"/> activation/rollback attempt.</summary>
public enum BaselineActivationOutcome
{
	Activated,

	/// <summary>The target baseline does not exist.</summary>
	NotFound,

	/// <summary>The target baseline's content revision was rejected/is not eligible for activation.</summary>
	RevisionNotEligible,

	/// <summary>The target baseline is already active -- a no-op, not an error, but distinct so a caller can report it precisely.</summary>
	AlreadyActive,
}

/// <summary>Impact-diff counts for one candidate baseline relative to the currently active baseline for the same execution profile (issue #731 AC "operators see a deterministic impact diff before activation").</summary>
public sealed record BaselineImpactDiff(
	int AddedProfiles,
	int ChangedProfiles,
	int RemovedProfiles,
	int UnsupportedCapabilities);
