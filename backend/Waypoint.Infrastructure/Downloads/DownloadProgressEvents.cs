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

using System.Text.Json;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <summary>
/// The one <c>download.progress</c> payload shape every acquisition lane emits (issue
/// #1041) -- field names match the frontend's already-shipped
/// <c>useDownloadQueue.ts</c> <c>DownloadProgressData</c>/<c>DownloadQueueItem</c>
/// contract (<c>artifact_id</c>, <c>progress_percent</c>, <c>rate_bytes_per_sec</c>,
/// <c>eta_seconds</c>, <c>retries</c>), which the UI already renders; before this
/// issue no emitter produced those names (the legacy <c>download</c> lane emitted
/// <c>download_id</c>/<c>percent</c> with rate/ETA always null), so a live rate/ETA
/// bar never had anything on the wire to bind to. <c>download_id</c>/
/// <c>bytes_downloaded</c>/<c>bytes_total</c> are kept alongside the new names for the
/// <c>downloads</c>-table lane's own debugging/log value; a lane with no
/// <c>downloads</c> row (e.g. <c>binaries-download</c>) simply passes null.
/// </summary>
public static class DownloadProgressEvents
{
	public static async Task EmitAsync(
		JobExecutionContext context,
		string state,
		Guid artifactId,
		long bytesDownloaded,
		long? bytesTotal,
		double? downloadRateBps,
		double? etaSeconds,
		Guid? downloadId,
		int? retries,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentException.ThrowIfNullOrWhiteSpace(state);

		string payload = JsonSerializer.Serialize(new
		{
			download_id = downloadId,
			artifact_id = artifactId,
			state,
			bytes_downloaded = bytesDownloaded,
			bytes_total = bytesTotal,
			progress_percent = bytesTotal is > 0
				? Math.Clamp((int)(bytesDownloaded * 100 / bytesTotal.Value), 0, 100)
				: (int?)null,
			rate_bytes_per_sec = downloadRateBps is double rate ? (long?)Math.Round(rate) : null,
			eta_seconds = etaSeconds is double eta ? (int?)Math.Round(eta) : null,
			retries,
		});

		await context.Events
			.EmitAsync(JobEventTypes.DownloadProgress, context.Job.Id, context.Job.RunId, payload, cancellationToken)
			.ConfigureAwait(false);
	}
}
