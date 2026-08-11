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
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// Issue #441's ADR-0015 acceptance criterion: "Download clearly reports tool absence
/// and succeeds when an operator-installed tool is present." No Postgres/PowerShell
/// dependency -- the gate's own decision is what is under test, not
/// <c>DownloadJobHandler</c>'s transfer/verification behavior (already covered by
/// <c>DownloadJobHandlerEndToEndTests</c>).
/// </summary>
public sealed class ToolGatedDownloadJobHandlerTests
{
	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static JobExecutionContext ContextFor()
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "download", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: "{}", AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	[Fact]
	public void JobType_IsDownload()
	{
		ToolGatedDownloadJobHandler handler = new(
			new FakeJobHandler("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded())),
			new FakeManagedToolPresenceChecker(present: true));

		Assert.Equal("download", handler.JobType);
	}

	[Fact]
	public async Task ToolAbsent_FailsClearlyWithoutInvokingTheInnerHandler()
	{
		bool innerInvoked = false;
		FakeJobHandler inner = new("download", (_, _) =>
		{
			innerInvoked = true;
			return Task.FromResult(JobExecutionOutcome.Succeeded());
		});

		ToolGatedDownloadJobHandler handler = new(
			inner, new FakeManagedToolPresenceChecker(present: false, expectedLocation: "/var/lib/waypoint/managed-tool/vcf-download-tool"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("vcf-download-tool is not installed", outcome.Note, StringComparison.Ordinal);
		Assert.Contains("/var/lib/waypoint/managed-tool/vcf-download-tool", outcome.Note, StringComparison.Ordinal);
		Assert.False(innerInvoked);
	}

	[Fact]
	public async Task ToolPresent_DelegatesToTheInnerHandlerAndReturnsItsOutcome()
	{
		FakeJobHandler inner = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Succeeded("Downloaded and verified.")));
		ToolGatedDownloadJobHandler handler = new(inner, new FakeManagedToolPresenceChecker(present: true));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal("Downloaded and verified.", outcome.Note);
	}

	[Fact]
	public async Task ToolPresent_PropagatesAnInnerFailureUnchanged()
	{
		FakeJobHandler inner = new("download", (_, _) => Task.FromResult(JobExecutionOutcome.Failed("checksum mismatch")));
		ToolGatedDownloadJobHandler handler = new(inner, new FakeManagedToolPresenceChecker(present: true));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Equal("checksum mismatch", outcome.Note);
	}
}
