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
using Microsoft.Extensions.Logging.Abstractions;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #1041: the emitted <c>download.progress</c> payload's field names must match
/// the frontend's already-shipped <c>useDownloadQueue.ts</c>
/// <c>DownloadProgressData</c>/<c>DownloadQueueItem</c> contract (<c>artifact_id</c>,
/// <c>progress_percent</c>, <c>rate_bytes_per_sec</c>, <c>eta_seconds</c>,
/// <c>retries</c>) -- before this issue no backend emitter produced those names, so a
/// live rate/ETA bar had nothing on the wire to bind to even though the UI already
/// rendered it.
/// </summary>
public sealed class DownloadProgressEventsTests
{
	private sealed class CapturingEventPublisher : IJobEventPublisher
	{
		public string? LastPayload { get; private set; }

		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{
			LastPayload = payloadJson;
			return Task.CompletedTask;
		}
	}

	private static JobExecutionContext ContextFor(CapturingEventPublisher publisher)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: Guid.NewGuid(), JobType: "download", TargetId: null, TargetName: "art",
			CredentialId: null, Priority: 1, Payload: "{}", AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", publisher,
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	[Fact]
	public async Task EmitAsync_UsesFrontendFieldNames_WithComputedRateAndEta()
	{
		CapturingEventPublisher publisher = new();
		JobExecutionContext context = ContextFor(publisher);
		Guid artifactId = Guid.NewGuid();
		Guid downloadId = Guid.NewGuid();

		await DownloadProgressEvents.EmitAsync(
			context, "downloading", artifactId, bytesDownloaded: 250, bytesTotal: 1000,
			downloadRateBps: 12_345.6, etaSeconds: 60.4, downloadId, retries: 2, CancellationToken.None);

		Assert.NotNull(publisher.LastPayload);
		using JsonDocument document = JsonDocument.Parse(publisher.LastPayload!);
		JsonElement root = document.RootElement;

		Assert.Equal(artifactId, root.GetProperty("artifact_id").GetGuid());
		Assert.Equal(downloadId, root.GetProperty("download_id").GetGuid());
		Assert.Equal("downloading", root.GetProperty("state").GetString());
		Assert.Equal(250, root.GetProperty("bytes_downloaded").GetInt64());
		Assert.Equal(1000, root.GetProperty("bytes_total").GetInt64());
		Assert.Equal(25, root.GetProperty("progress_percent").GetInt32());
		Assert.Equal(12346, root.GetProperty("rate_bytes_per_sec").GetInt64());
		Assert.Equal(60, root.GetProperty("eta_seconds").GetInt32());
		Assert.Equal(2, root.GetProperty("retries").GetInt32());
	}

	[Fact]
	public async Task EmitAsync_NullRateAndEta_SerializeAsJsonNull_NeverThrows()
	{
		CapturingEventPublisher publisher = new();
		JobExecutionContext context = ContextFor(publisher);

		await DownloadProgressEvents.EmitAsync(
			context, "downloading", Guid.NewGuid(), bytesDownloaded: 0, bytesTotal: null,
			downloadRateBps: null, etaSeconds: null, downloadId: null, retries: null, CancellationToken.None);

		using JsonDocument document = JsonDocument.Parse(publisher.LastPayload!);
		JsonElement root = document.RootElement;

		Assert.Equal(JsonValueKind.Null, root.GetProperty("bytes_total").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("progress_percent").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("rate_bytes_per_sec").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("eta_seconds").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("retries").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("download_id").ValueKind);
	}
}
