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

using System.Text.Json.Serialization;
using Waypoint.Core.Downloads;

namespace Waypoint.Api.Contracts;

/// <summary>Request body for <c>POST /api/v1/downloads</c>: one or more depot artifact ids to queue.</summary>
public sealed record QueueDownloadsRequest(
	[property: JsonPropertyName("depot_artifact_ids")]
	IReadOnlyList<string> DepotArtifactIds);

/// <summary>Response body for <c>POST /api/v1/downloads</c> (202 Accepted).</summary>
public sealed record DownloadsQueuedResponse(
	[property: JsonPropertyName("run_id")]
	string RunId,

	[property: JsonPropertyName("download_ids")]
	IReadOnlyList<string> DownloadIds);

/// <summary>
/// Response body for one row of <c>GET /api/v1/downloads</c>
/// (docs/api-contract.md "Queue view: rate, ETA, retries").
/// </summary>
public sealed record DownloadResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("depot_artifact_id")]
	string DepotArtifactId,

	[property: JsonPropertyName("job_id")]
	string? JobId,

	[property: JsonPropertyName("run_id")]
	string? RunId,

	[property: JsonPropertyName("state")]
	string State,

	[property: JsonPropertyName("bytes_total")]
	long? BytesTotal,

	[property: JsonPropertyName("bytes_downloaded")]
	long BytesDownloaded,

	[property: JsonPropertyName("download_rate_bps")]
	long? DownloadRateBps,

	[property: JsonPropertyName("eta_seconds")]
	int? EtaSeconds,

	[property: JsonPropertyName("retry_count")]
	int RetryCount,

	[property: JsonPropertyName("max_retries")]
	int MaxRetries,

	[property: JsonPropertyName("failure_reason")]
	string? FailureReason,

	[property: JsonPropertyName("requested_by")]
	string? RequestedBy,

	[property: JsonPropertyName("created_at")]
	DateTimeOffset CreatedAt,

	[property: JsonPropertyName("updated_at")]
	DateTimeOffset UpdatedAt,

	[property: JsonPropertyName("completed_at")]
	DateTimeOffset? CompletedAt)
{
	public static DownloadResponse FromDomain(Download download)
	{
		ArgumentNullException.ThrowIfNull(download);
		return new DownloadResponse(
			download.Id.ToString(),
			download.DepotArtifactId.ToString(),
			download.JobId?.ToString(),
			download.RunId?.ToString(),
			download.State,
			download.BytesTotal,
			download.BytesDownloaded,
			download.DownloadRateBps,
			download.EtaSeconds,
			download.RetryCount,
			download.MaxRetries,
			download.FailureReason,
			download.RequestedBy,
			download.CreatedAt,
			download.UpdatedAt,
			download.CompletedAt);
	}
}

/// <summary>Response body for <c>DELETE /api/v1/downloads/{id}</c>.</summary>
public sealed record DownloadCancelledResponse(
	[property: JsonPropertyName("id")]
	string Id,

	[property: JsonPropertyName("state")]
	string State);
