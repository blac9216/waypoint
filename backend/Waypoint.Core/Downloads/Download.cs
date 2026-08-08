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
/// One row of the M1 download vertical slice's <c>downloads</c> table (migration
/// 0001, issue #10). Tracks the fine-grained <c>queued -&gt; downloading -&gt;
/// verifying -&gt; verified|failed|cancelled</c> state machine (docs/api-contract.md)
/// for one <see cref="DepotArtifactId"/> — separate from the owning
/// <c>jobs.state</c>, which stays the coarse <c>queued -&gt; running -&gt; done|failed</c>
/// shape (<see cref="Waypoint.Core.Jobs.JobShape.Simple"/>) per
/// <see cref="Waypoint.Core.Jobs.JobShape"/>'s doc comment. A job can 1:1-own many
/// download rows across retries, so <see cref="JobId"/> is not unique.
/// </summary>
public sealed record Download(
	Guid Id,
	Guid DepotArtifactId,
	Guid? JobId,
	Guid? RunId,
	string State,
	long? BytesTotal,
	long BytesDownloaded,
	long? DownloadRateBps,
	int? EtaSeconds,
	int RetryCount,
	int MaxRetries,
	string? FailureReason,
	string? RequestedBy,
	DateTimeOffset CreatedAt,
	DateTimeOffset UpdatedAt,
	DateTimeOffset? CompletedAt);

/// <summary>The exact string values of <c>downloads.state</c>, matching <c>downloads_state_check</c> in <c>0001_initial_schema.sql</c>.</summary>
public static class DownloadStates
{
	public const string Queued = "queued";
	public const string Downloading = "downloading";
	public const string Verifying = "verifying";
	public const string Verified = "verified";
	public const string Failed = "failed";
	public const string Cancelled = "cancelled";
}
