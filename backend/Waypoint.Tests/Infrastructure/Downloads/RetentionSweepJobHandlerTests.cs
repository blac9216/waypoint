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
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>retention-sweep</c> job handler (issue #1436): payload parsing/validation
/// for both its shapes (scheduled sweep, immediate purge) and correct delegation to a
/// fake <see cref="IRetentionSweepService"/> -- the service's own behavior (grace
/// transitions, timed auto-prune, the partial-listing gate, physical purge) is
/// covered against real Postgres by <c>RetentionSweepServiceTests</c>.
/// </summary>
public sealed class RetentionSweepJobHandlerTests
{
	private sealed class FakeSweepService : IRetentionSweepService
	{
		public RetentionSweepRequest? LastRequest { get; private set; }
		public RetentionSweepReport SweepResult { get; set; } = new(false, null, 0, 0, 0, []);

		public List<(Guid Id, string Actor, string? Reason)> PurgeCalls { get; } = [];
		public Func<Guid, RetentionPurgeOutcome>? PurgeResultFor { get; set; }

		public Task<RetentionSweepReport> RunSweepAsync(RetentionSweepRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(SweepResult);
		}

		public Task<RetentionPurgeOutcome> PurgeImmediatelyAsync(Guid retainedContentStateId, string actor, string? reason, CancellationToken cancellationToken)
		{
			PurgeCalls.Add((retainedContentStateId, actor, reason));
			RetentionPurgeOutcome outcome = PurgeResultFor?.Invoke(retainedContentStateId) ?? new RetentionPurgeOutcome(retainedContentStateId, true, null);
			return Task.FromResult(outcome);
		}
	}

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "retention-sweep", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 4, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static RetentionSweepJobHandler CreateHandler(FakeSweepService service) =>
		new(service, NullLogger<RetentionSweepJobHandler>.Instance);

	[Fact]
	public async Task ExecuteAsync_MalformedPayload_Fails()
	{
		RetentionSweepJobHandler handler = CreateHandler(new FakeSweepService());

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("{not json"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	[Fact]
	public async Task ExecuteAsync_MissingListingVerified_Fails()
	{
		RetentionSweepJobHandler handler = CreateHandler(new FakeSweepService());

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor("""{"candidate_depot_artifact_ids": []}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("listing_verified", outcome.Note);
	}

	[Fact]
	public async Task ExecuteAsync_SweepShape_PassesListingVerifiedAndCandidatesThrough()
	{
		Guid candidateId = Guid.NewGuid();
		FakeSweepService service = new()
		{
			SweepResult = new RetentionSweepReport(false, null, EnteredGrace: 1, AutoPruned: 2, UntrackedCandidatesSkipped: 0, Errors: []),
		};
		RetentionSweepJobHandler handler = CreateHandler(service);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($$"""{"candidate_depot_artifact_ids": ["{{candidateId}}"], "listing_verified": true, "scope_key": "default"}"""),
			CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.NotNull(service.LastRequest);
		Assert.True(service.LastRequest!.ListingVerified);
		Assert.Equal("default", service.LastRequest.ScopeKey);
		Assert.Equal([candidateId], service.LastRequest.SupersededOrOutOfWindowDepotArtifactIds);
	}

	[Fact]
	public async Task ExecuteAsync_SweepShape_ListingUnverifiedFalse_StillCallsServiceWhichReportsSkipped()
	{
		FakeSweepService service = new()
		{
			SweepResult = new RetentionSweepReport(true, "partial listing", 0, 0, 0, []),
		};
		RetentionSweepJobHandler handler = CreateHandler(service);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor("""{"listing_verified": false}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.False(service.LastRequest!.ListingVerified);
		Assert.Contains("partial listing", outcome.Note);
	}

	[Fact]
	public async Task ExecuteAsync_SweepShape_ReportErrors_Fails()
	{
		FakeSweepService service = new()
		{
			SweepResult = new RetentionSweepReport(false, null, 0, 0, 0, ["boom"]),
		};
		RetentionSweepJobHandler handler = CreateHandler(service);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor("""{"listing_verified": true}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("boom", outcome.Note);
	}

	[Fact]
	public async Task ExecuteAsync_PurgeNowShape_RequiresActor()
	{
		RetentionSweepJobHandler handler = CreateHandler(new FakeSweepService());

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($$"""{"purge_now_ids": ["{{Guid.NewGuid()}}"]}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("actor", outcome.Note);
	}

	[Fact]
	public async Task ExecuteAsync_PurgeNowShape_CallsPurgeImmediatelyPerIdAndIgnoresSweepFields()
	{
		Guid id1 = Guid.NewGuid();
		Guid id2 = Guid.NewGuid();
		FakeSweepService service = new();
		RetentionSweepJobHandler handler = CreateHandler(service);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($$"""
				{"purge_now_ids": ["{{id1}}", "{{id2}}"], "actor": "operator-1", "reason": "policy violation",
				 "candidate_depot_artifact_ids": ["{{Guid.NewGuid()}}"], "listing_verified": true}
				"""),
			CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(2, service.PurgeCalls.Count);
		Assert.All(service.PurgeCalls, call => Assert.Equal("operator-1", call.Actor));
		Assert.All(service.PurgeCalls, call => Assert.Equal("policy violation", call.Reason));
		Assert.Contains(service.PurgeCalls, call => call.Id == id1);
		Assert.Contains(service.PurgeCalls, call => call.Id == id2);
		Assert.Null(service.LastRequest); // the sweep path was never invoked
	}

	[Fact]
	public async Task ExecuteAsync_PurgeNowShape_AllFail_Fails()
	{
		Guid id = Guid.NewGuid();
		FakeSweepService service = new() { PurgeResultFor = _ => new RetentionPurgeOutcome(id, false, "pinned") };
		RetentionSweepJobHandler handler = CreateHandler(service);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor($$"""{"purge_now_ids": ["{{id}}"], "actor": "operator-1"}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("pinned", outcome.Note);
	}
}
