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
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>depot-enrollment</c> job handler (issue #691): the <c>generate-depot-id</c>
/// branch (no credential dependency, so it is fully fake-able) plus malformed-payload
/// guards. The <c>validate-code</c> branch resolves a real <c>CredentialRepository</c>
/// (sealed, Postgres-only) exactly like <c>ManagedToolInstallJobHandler</c>'s
/// depot-fetch path, so it is covered end to end against real Postgres by
/// <c>DepotEnrollmentValidateEndToEndTests</c>, mirroring that file's own split.
/// </summary>
public sealed class DepotEnrollmentJobHandlerTests
{
	private sealed class FakeDepotIdentityTool : IDepotIdentityTool
	{
		private readonly DepotIdentityResult _result;

		public FakeDepotIdentityTool(DepotIdentityResult result)
		{
			_result = result;
		}

		public List<string> ValidateCalls { get; } = [];

		public Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken) => Task.FromResult(_result);

		public Task SeedMachineIdentityAsync(string assetId, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");

		public Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, CancellationToken cancellationToken)
		{
			ValidateCalls.Add(activationCodePath);
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");
		}
	}

	private sealed class FakeDepotEnrollmentRepository : IDepotEnrollmentRepository
	{
		public string? RecordedDepotId { get; private set; }
		public int SetDepotIdCallCount { get; private set; }

		public Task<DepotEnrollment?> GetAsync(CancellationToken cancellationToken) =>
			Task.FromResult<DepotEnrollment?>(new DepotEnrollment(
				DepotEnrollmentStates.DepotIdUnavailable, null, null, null, null, null, null, DateTimeOffset.UtcNow));

		public Task SetDepotIdAsync(string depotId, CancellationToken cancellationToken)
		{
			RecordedDepotId = depotId;
			SetDepotIdCallCount++;
			return Task.CompletedTask;
		}

		public Task SetPairedAsync(string assetId, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");

		public Task SetValidationOutcomeAsync(bool succeeded, string? failureNote, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");

		public Task ResetAsync(CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");
	}

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "depot-enrollment", TargetId: null, TargetName: null,
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

	private static DepotEnrollmentJobHandler CreateHandler(
		IDepotIdentityTool tool, IDepotEnrollmentRepository enrollment)
	{
		// The credential-store/repository/redactor dependencies below are only ever
		// exercised by the validate-code branch, which this file never invokes -- an
		// unreachable connection string mirrors ManagedToolInstallJobHandlerTests'
		// own convention for the same reason.
		return new DepotEnrollmentJobHandler(
			tool,
			enrollment,
			new UnreachableCredentialSecretStore(),
			new CredentialRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x"),
			new InPlaySecretRedactor());
	}

	private sealed class UnreachableCredentialSecretStore : ICredentialSecretStore
	{
		public Task StoreAsync(Guid credentialId, byte[] secretValue, string actor, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");

		public Task<DecryptedSecret> DecryptAsync(Guid credentialId, string actor, Guid? jobId, Guid? runId, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");

		public Task<bool> DeleteAsync(Guid credentialId, string actor, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's generate-depot-id scenarios.");
	}

	[Fact]
	public async Task GenerateDepotId_ToolSucceeds_RecordsDepotIdAndSucceeds()
	{
		FakeDepotIdentityTool tool = new(DepotIdentityResult.Ok("WPT-0001-DEPOT-ID"));
		FakeDepotEnrollmentRepository enrollment = new();
		DepotEnrollmentJobHandler handler = CreateHandler(tool, enrollment);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor("""{"operation":"generate-depot-id"}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal("WPT-0001-DEPOT-ID", enrollment.RecordedDepotId);
		Assert.Equal(1, enrollment.SetDepotIdCallCount);
	}

	[Fact]
	public async Task GenerateDepotId_ToolFails_FailsWithoutRecordingAnyId()
	{
		FakeDepotIdentityTool tool = new(DepotIdentityResult.Failed("vcf-download-tool is not installed."));
		FakeDepotEnrollmentRepository enrollment = new();
		DepotEnrollmentJobHandler handler = CreateHandler(tool, enrollment);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(
			ContextFor("""{"operation":"generate-depot-id"}"""), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("not installed", outcome.Note);
		Assert.Equal(0, enrollment.SetDepotIdCallCount);
	}

	[Fact]
	public async Task MalformedJsonPayload_FailsCleanlyWithoutThrowing()
	{
		FakeDepotIdentityTool tool = new(DepotIdentityResult.Ok("unused"));
		FakeDepotEnrollmentRepository enrollment = new();
		DepotEnrollmentJobHandler handler = CreateHandler(tool, enrollment);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("not-json"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("Malformed", outcome.Note);
	}

	[Theory]
	[InlineData("""{"operation":"unknown-op"}""")]
	[InlineData("{}")]
	public async Task UnknownOrMissingOperation_FailsWithAnActionableMessage(string payload)
	{
		FakeDepotIdentityTool tool = new(DepotIdentityResult.Ok("unused"));
		FakeDepotEnrollmentRepository enrollment = new();
		DepotEnrollmentJobHandler handler = CreateHandler(tool, enrollment);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("generate-depot-id", outcome.Note);
		Assert.Contains("validate-code", outcome.Note);
	}

	[Fact]
	public void JobType_IsDepotEnrollment()
	{
		FakeDepotIdentityTool tool = new(DepotIdentityResult.Ok("unused"));
		FakeDepotEnrollmentRepository enrollment = new();
		DepotEnrollmentJobHandler handler = CreateHandler(tool, enrollment);

		Assert.Equal("depot-enrollment", handler.JobType);
	}
}
