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
/// Storage for the compliance-content singleton config and its pull history
/// (<c>compliance_content</c>/<c>compliance_content_pulls</c>, migration 0035). Both
/// the API (config CRUD, history reads) and the compliance-runner's content-pull
/// handler (recording pull outcomes) use this interface -- ADR-0017 places
/// <c>content-pull</c>/<c>content-import</c> execution in compliance-runner, but
/// config authorship (PUT) stays API-only, the same split <c>StigManagerRepository</c>
/// establishes for its own singleton connection row.
/// </summary>
public interface IComplianceContentRepository
{
	/// <summary>Returns null when compliance content has never been configured.</summary>
	Task<ComplianceContentConfig?> GetConfigAsync(CancellationToken cancellationToken);

	/// <summary>Upserts the singleton config row (Admin-only write, via the API).</summary>
	Task<ComplianceContentConfig> PutConfigAsync(string repositoryUrl, string refType, string refValue, CancellationToken cancellationToken);

	/// <summary>
	/// Records a pull attempt's outcome and, on success, stamps
	/// <c>pulled_commit</c>/<c>pulled_by</c>/<c>pulled_at</c> on the config row in the
	/// same transaction. Called by the content-pull job handler, never by the API.
	/// </summary>
	Task RecordPullAsync(
		Guid? jobId, string refType, string refValue, string? commit, string status, string? note, string? initiatedBy, CancellationToken cancellationToken);

	/// <summary>Pull history, newest first, bounded by <paramref name="limit"/>.</summary>
	Task<IReadOnlyList<ComplianceContentPull>> ListPullsAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Storage for the profile inventory (<c>profiles</c>, migration 0035). Written only by
/// the content-pull handler (compliance-runner); read by the API for the profile
/// inventory endpoint that feeds the Benchmarks screen (#559).
/// </summary>
public interface IProfileRepository
{
	/// <summary>All installed profiles, ordered by name.</summary>
	Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken);

	/// <summary>Single profile by its surrogate id, or null when no such profile exists (issue #598: <c>GET /profiles/{id}/controls</c> 404s on an unknown id).</summary>
	Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces the inventory with exactly <paramref name="profiles"/>: upserts each by
	/// <c>profile_key</c> and deletes any existing row not present in this pull's
	/// result, mirroring <c>InventoryRepository</c>'s discover-job
	/// upsert/replace-per-target shape (migration 0011) -- a profile removed upstream
	/// must disappear from the inventory, not linger as a stale row.
	/// </summary>
	Task ReplaceAllAsync(IReadOnlyList<ProfileUpsert> profiles, CancellationToken cancellationToken);
}

/// <summary>
/// Storage for the per-profile control inventory (<c>profile_controls</c>, migration
/// 0038, issue #598). Written only by the content-pull handler (compliance-runner) in
/// the same pull that upserts the owning <see cref="Profile"/> row; read by the API for
/// <c>GET /profiles/{id}/controls</c> (feeds the Benchmarks screen's per-control panel).
/// </summary>
public interface IProfileControlRepository
{
	/// <summary>All controls for one profile, ordered by control id.</summary>
	Task<IReadOnlyList<ProfileControl>> ListByProfileAsync(Guid profileId, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces <paramref name="profileId"/>'s whole control set: upserts each by
	/// (profile_id, control_id) and deletes any existing row for that profile not
	/// present in this pull's result -- same "replace-per-parent" shape as
	/// <see cref="IProfileRepository.ReplaceAllAsync"/>, scoped to one profile so a
	/// pull that touches profile A never disturbs profile B's already-parsed controls.
	/// </summary>
	Task ReplaceForProfileAsync(Guid profileId, IReadOnlyList<ProfileControlUpsert> controls, CancellationToken cancellationToken);
}
