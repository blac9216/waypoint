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

using Waypoint.Core.Components;

namespace Waypoint.Core.Jobs;

/// <summary>
/// One immutable, append-only-by-convention record of a scan run's requested versus
/// resolved scope (migration 0056, issue #733). Written exactly once, at run creation,
/// by <see cref="Waypoint.Infrastructure.Runs.RunCreationService"/> -- nothing in this
/// slice updates a row after it is written, matching the "immutable plan" freeze
/// ADR-0023 describes; later component/catalog changes never rewrite what a historical
/// run recorded here.
/// </summary>
public sealed record RunScopeSnapshot(
	Guid Id,
	Guid RunId,
	string RequestedMode,
	string RequestedScopeJson,
	IReadOnlyList<Guid> ResolvedComponentIds,
	IReadOnlyList<ScopeOmission> Omissions,
	DateTimeOffset CreatedAt);

/// <summary>Storage for <see cref="RunScopeSnapshot"/> (migration 0056).</summary>
public interface IRunScopeSnapshotRepository
{
	/// <summary>
	/// Persists the frozen requested/resolved scope for a just-created run.
	/// <paramref name="requestedScopeJson"/> is the raw <c>target_scope</c> request body
	/// (round-tripped verbatim for history, same convention as <c>runs.scope</c>).
	/// </summary>
	Task RecordAsync(
		Guid runId,
		string requestedMode,
		string requestedScopeJson,
		IReadOnlyList<Guid> resolvedComponentIds,
		IReadOnlyList<ScopeOmission> omissions,
		CancellationToken cancellationToken);

	/// <summary>The recorded snapshot for a run, or null when this run predates #733 or was not a component-scoped scan (e.g. the legacy target-granular request shape).</summary>
	Task<RunScopeSnapshot?> GetForRunAsync(Guid runId, CancellationToken cancellationToken);
}
