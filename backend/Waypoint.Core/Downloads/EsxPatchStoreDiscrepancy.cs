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

namespace Waypoint.Core.Downloads;

/// <summary>
/// Which side of the diff a <see cref="EsxPatchStoreDiscrepancy"/> represents (issue
/// #1447): a bundle <see cref="IEsxPatchStoreReconciler"/> has previously indexed at
/// this store root that the most recent reconciliation no longer found
/// (<see cref="Missing"/>), or a zip file physically present under a vendor directory
/// that no successfully parsed bundle in the most recent run references
/// (<see cref="Orphan"/>). Neither implies a delete -- see this file's other doc
/// comments for the never-auto-remove invariant.
/// </summary>
public enum EsxPatchStoreDiscrepancyType
{
	Missing,
	Orphan,
}

/// <summary>
/// One discrepancy record persisted by <see cref="IEsxPatchStoreReconciler"/>
/// (migration 0091's <c>esx_patch_store_discrepancies</c>), surfaced as a first-class
/// row rather than merely logged (issue #1447 Proposed Changes). <see cref="Key"/> is
/// the parser's content key for <see cref="EsxPatchStoreDiscrepancyType.Missing"/>, or
/// <c>"{VendorCode}/{fileName}"</c> for <see cref="EsxPatchStoreDiscrepancyType.Orphan"/>.
/// <see cref="ResolvedAt"/> is set when a later reconciliation pass no longer observes
/// the condition (a missing bundle reappears, an orphan is picked up by metadata) --
/// bookkeeping on the alert's own lifecycle only; the row itself is never deleted, and
/// no orphan disk content is ever removed by the reconciler (issue #1447 AC3 -- explicit
/// removal is this issue's surfacing/rebuild sibling, #1452).
/// </summary>
public sealed record EsxPatchStoreDiscrepancy(
	Guid Id,
	string StoreRoot,
	EsxPatchStoreDiscrepancyType Type,
	string Key,
	string? VendorCode,
	string? RelativePath,
	string? Detail,
	DateTimeOffset FirstDetectedAt,
	DateTimeOffset LastDetectedAt,
	DateTimeOffset? ResolvedAt);

/// <summary>
/// Outcome of one <see cref="IEsxPatchStoreReconciler.ReconcileAsync"/> call.
/// <see cref="Succeeded"/> false mirrors <see cref="EsxPatchStoreParseResult.Failed"/>
/// (a store-root-level problem the underlying parse could not get past); every count
/// is then 0 and both warning lists empty, since no store content was ever read. On
/// success, the counts describe what THIS call changed, not this store's running
/// totals: <see cref="IndexedCount"/> is bundles upserted (new or re-seen),
/// <see cref="NewMissingCount"/>/<see cref="NewOrphanCount"/> are newly opened
/// discrepancies, and <see cref="ResolvedCount"/> is discrepancies this call closed
/// because the condition no longer holds. <see cref="ParserWarnings"/> is
/// #1446's parser's own non-fatal anomalies, passed through verbatim.
/// <see cref="ReconcilerWarnings"/> is this reconciler's own -- degraded-scope
/// notices (round-1 review finding 1: a read-failure-bearing parse must not have its
/// empty/partial bundle list mistaken for ground truth, so missing-detection is
/// skipped for the affected vendor(s)/store and that skip is surfaced here rather
/// than converted into false <c>missing</c> rows) and per-occurrence warnings for a
/// vendor directory this call could not list while orphan-scanning (finding 3).
/// </summary>
public sealed record EsxPatchStoreReconciliationReport(
	bool Succeeded,
	string? FailureReason,
	int IndexedCount,
	int NewMissingCount,
	int NewOrphanCount,
	int ResolvedCount,
	IReadOnlyList<string> ParserWarnings,
	IReadOnlyList<string> ReconcilerWarnings)
{
	public static EsxPatchStoreReconciliationReport Failed(string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		return new EsxPatchStoreReconciliationReport(false, reason, 0, 0, 0, 0, [], []);
	}
}

/// <summary>
/// Reconciles an ESX patch store's parsed metadata (<see cref="IEsxPatchStoreMetadataParser"/>,
/// issue #1446) against the database index and against the store's on-disk zip files,
/// producing a full model of what the store SHOULD contain (issue #1447 AC1) and
/// recording missing/orphaned content as first-class, never-auto-removed discrepancy
/// records (AC2/AC3). Consumed by this issue's self-healing sync sibling (#1451, which
/// decides what to repair) and surfacing/rebuild sibling (#1452, which exposes
/// discrepancies in the UI and performs any explicit orphan removal).
/// </summary>
public interface IEsxPatchStoreReconciler
{
	/// <summary>
	/// Reconciles the store at <paramref name="storeRoot"/>. <paramref name="layout"/>
	/// forces a specific on-disk layout the same way
	/// <see cref="IEsxPatchStoreMetadataParser.Parse"/> does; null probes both.
	/// </summary>
	Task<EsxPatchStoreReconciliationReport> ReconcileAsync(string storeRoot, EsxPatchStoreLayout? layout, CancellationToken cancellationToken);
}
