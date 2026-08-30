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

using Microsoft.Extensions.Logging.Abstractions;
using Waypoint.Core.Jobs;
using Waypoint.Core.Scans;
using Waypoint.Infrastructure.Runs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Runs;

/// <summary>
/// Issue #1140: <see cref="ComponentResultRecordingService.RecordCompletedAsync"/> sets
/// <see cref="ComponentResultStatuses.CompletedZeroControls"/> at the WRITE path itself
/// (migration 0081), not left to a read-time inference -- exercised against a fake
/// <see cref="IComponentResultRepository"/> (no Postgres needed: this is pure
/// classification logic over a real, invented HDF document parsed by the real
/// <see cref="HdfFindingsParser"/>). All-invented fixtures (AGENTS.md).
/// </summary>
public sealed class ComponentResultRecordingServiceTests : IDisposable
{
	private readonly string _hdfPath = Path.Combine(Path.GetTempPath(), $"wp-crrs-{Guid.NewGuid():N}.json");
	private readonly FakeComponentResultRepository _repository = new();
	private readonly ComponentResultRecordingService _service;

	public ComponentResultRecordingServiceTests()
	{
		_service = new ComponentResultRecordingService(_repository, NullLogger<ComponentResultRecordingService>.Instance);
	}

	public void Dispose()
	{
		if (File.Exists(_hdfPath))
		{
			File.Delete(_hdfPath);
		}
	}

	[Fact]
	public async Task RecordCompletedAsync_AllNotReviewedControl_WritesCompletedZeroControls()
	{
		// A control with no results array at all -- HdfControlClassifier's "never ran"
		// shape -- maps to a single Not_Reviewed finding: zero passed, zero open,
		// evaluated nothing.
		await File.WriteAllTextAsync(_hdfPath, BuildHdf("""{"id": "invented-01", "tags": {"severity": "medium"}, "results": []}"""));

		await _service.RecordCompletedAsync(BuildJob(), _hdfPath, attestedHdfPath: null, cklPath: null, CancellationToken.None);

		ComponentResultRecord record = Assert.Single(_repository.Recorded);
		Assert.Equal(ComponentResultStatuses.CompletedZeroControls, record.Status);
	}

	[Fact]
	public async Task RecordCompletedAsync_GenuinelyPassedControl_WritesPlainCompleted()
	{
		await File.WriteAllTextAsync(_hdfPath, BuildHdf(
			"""{"id": "invented-01", "tags": {"severity": "medium"}, "results": [{"status": "passed", "code_desc": "ok"}]}"""));

		await _service.RecordCompletedAsync(BuildJob(), _hdfPath, attestedHdfPath: null, cklPath: null, CancellationToken.None);

		ComponentResultRecord record = Assert.Single(_repository.Recorded);
		Assert.Equal(ComponentResultStatuses.Completed, record.Status);
	}

	[Fact]
	public async Task RecordCompletedAsync_GenuinelyAllNotApplicableControl_StaysPlainCompleted()
	{
		// impact 0.0 on an all-skipped control is the profile's own "does not apply"
		// decision (HdfControlClassifier) -- a determinate outcome, never reclassified
		// as zero-evaluated.
		await File.WriteAllTextAsync(_hdfPath, BuildHdf(
			"""{"id": "invented-01", "tags": {"severity": "medium"}, "impact": 0.0, "results": [{"status": "skipped", "code_desc": "n/a"}]}"""));

		await _service.RecordCompletedAsync(BuildJob(), _hdfPath, attestedHdfPath: null, cklPath: null, CancellationToken.None);

		ComponentResultRecord record = Assert.Single(_repository.Recorded);
		Assert.Equal(ComponentResultStatuses.Completed, record.Status);
	}

	[Fact]
	public async Task RecordCompletedAsync_UnparseableHdf_StaysExecutionErrorNotZeroControls()
	{
		// A parse FAILURE is ExecutionError regardless -- the zero-controls
		// reclassification only ever applies to a successfully-parsed attempt.
		await File.WriteAllTextAsync(_hdfPath, "{ not valid hdf ");

		await _service.RecordCompletedAsync(BuildJob(), _hdfPath, attestedHdfPath: null, cklPath: null, CancellationToken.None);

		ComponentResultRecord record = Assert.Single(_repository.Recorded);
		Assert.Equal(ComponentResultStatuses.ExecutionError, record.Status);
	}

	private static string BuildHdf(string controlJson) => $$"""{"profiles": [{"controls": [{{controlJson}}]}]}""";

	private static ClaimedJob BuildJob() => new(
		Id: Guid.NewGuid(), RunId: Guid.NewGuid(), JobType: "scan", TargetId: Guid.NewGuid(), TargetName: "invented-target",
		CredentialId: null, Priority: 0, Payload: "{}", AttemptCount: 1, MaxAttempts: 3, ScanPlanItemId: Guid.NewGuid());

	/// <summary>Captures every <see cref="ComponentResultRecord"/> the service asks to persist -- no real Postgres needed for this pure classification test.</summary>
	private sealed class FakeComponentResultRepository : IComponentResultRepository
	{
		public List<ComponentResultRecord> Recorded { get; } = [];

		public Task RecordAsync(ComponentResultRecord record, CancellationToken cancellationToken)
		{
			Recorded.Add(record);
			return Task.CompletedTask;
		}

		public Task<int> NextAttemptNumberAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(1);

		public Task<Guid?> GetComponentIdForPlanItemAsync(Guid scanPlanItemId, CancellationToken cancellationToken) =>
			Task.FromResult<Guid?>(Guid.NewGuid());

		public Task<RunResultRollup> GetRunRollupAsync(Guid runId, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by this test.");

		public Task<ComponentResultFindingsPage> GetLatestFindingsAsync(Guid jobId, int limit, int offset, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by this test.");

		public Task<ComponentResultArtifactsList> GetLatestArtifactsAsync(Guid jobId, CancellationToken cancellationToken) =>
			throw new NotSupportedException("Not exercised by this test.");
	}
}
