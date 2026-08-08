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

using Waypoint.Core.Pagination;

namespace Waypoint.Core.Downloads;

/// <summary>
/// Storage for the <c>downloads</c> table (issue #10, M1 vertical slice). One
/// implementation (<c>Waypoint.Infrastructure.Downloads.DownloadRepository</c>, plain
/// Npgsql — same "no ORM for this layer" convention as
/// <c>Waypoint.Infrastructure.Catalog.DepotArtifactRepository</c>).
/// </summary>
public interface IDownloadRepository
{
	/// <summary>Creates a queued download row for one depot artifact.</summary>
	Task<Guid> CreateAsync(Guid depotArtifactId, Guid? jobId, string? requestedBy, CancellationToken cancellationToken);

	/// <summary>
	/// Stamps the job-queue row (and its owning run -- see migration 0008) that will
	/// execute this download, once the job engine has created them.
	/// </summary>
	Task SetJobAsync(Guid downloadId, Guid jobId, Guid runId, CancellationToken cancellationToken);

	Task<Download?> GetAsync(Guid downloadId, CancellationToken cancellationToken);

	Task<Download?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken);

	/// <summary>
	/// Moves <paramref name="downloadId"/> to <paramref name="state"/> and updates the
	/// progress/failure fields carried alongside the transition. Any parameter left
	/// null leaves the corresponding column unchanged, except <paramref name="state"/>
	/// itself (always required) — this lets callers report partial progress (bytes
	/// only) without clobbering <see cref="Download.FailureReason"/> or vice versa.
	/// </summary>
	Task UpdateProgressAsync(
		Guid downloadId,
		string state,
		long? bytesTotal,
		long? bytesDownloaded,
		long? downloadRateBps,
		int? etaSeconds,
		string? failureReason,
		CancellationToken cancellationToken);

	/// <summary>Increments <c>retry_count</c> by one and returns the new value.</summary>
	Task<int> IncrementRetryCountAsync(Guid downloadId, CancellationToken cancellationToken);

	Task<(IReadOnlyList<Download> Items, long TotalCount)> ListAsync(PageRequest page, CancellationToken cancellationToken);
}
