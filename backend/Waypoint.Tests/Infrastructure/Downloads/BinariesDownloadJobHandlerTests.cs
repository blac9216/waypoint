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
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>binaries-download</c> job handler (issue #1482): payload validation and the
/// enrollment gate, which are fully fake-able without a real Postgres or process
/// invocation -- mirrors <c>CatalogPullJobHandlerTests</c>'s own split (the full
/// connected success/failure path resolves a real
/// <see cref="Waypoint.Infrastructure.Secrets.CredentialRepository"/> and spawns a
/// process, out of scope for this fast unit file; see the PR description's deferred-
/// coverage note). <see cref="BinariesDownloadToolTests"/> covers the tool-invocation
/// behavior (argv shape, failure classification, identity isolation) this handler
/// delegates to once past the gates tested here.
/// </summary>
public sealed class BinariesDownloadJobHandlerTests
{
	private sealed class FakeEnrollmentRepository(DepotEnrollment? enrollment) : IDepotEnrollmentRepository
	{
		public Task<DepotEnrollment?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(enrollment);
		public Task SetDepotIdAsync(string depotId, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task SetPairedAsync(string assetId, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task SetValidationOutcomeAsync(bool succeeded, string? failureNote, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task ResetAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class UnreachableTool : IBinariesDownloadTool
	{
		public Task<BinariesDownloadResult> DownloadAsync(
			string externalId, string depotStorePath, string activationCodePath, string identityHome, string assetId,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called when the enrollment gate rejects the job first.");
	}

	private sealed class UnreachableCredentialSecretStore : ICredentialSecretStore
	{
		public Task StoreAsync(Guid credentialId, byte[] secretValue, string actor, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<DecryptedSecret> DecryptAsync(Guid credentialId, string actor, Guid? jobId, Guid? runId, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<bool> DeleteAsync(Guid credentialId, string actor, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private static JobExecutionContext ContextFor(string payload = "{\"depot_artifact_id\":\"00000000-0000-0000-0000-000000000001\",\"external_id\":\"vcf-bundle-01\"}")
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: Guid.NewGuid(), JobType: RunTypes.BinariesDownload, TargetId: null, TargetName: "vcf-bundle-01",
			CredentialId: null, Priority: 1, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private static BinariesDownloadJobHandler CreateHandler(DepotEnrollment? enrollment, IBinariesDownloadTool? tool = null) =>
		new(
			new FakeEnrollmentRepository(enrollment),
			tool ?? new UnreachableTool(),
			new UnreachableCredentialSecretStore(),
			new CredentialRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x"),
			Options.Create(new ManagedToolOptions()),
			Options.Create(new CatalogOptions()));

	[Theory]
	[InlineData(null)]
	[InlineData(DepotEnrollmentStates.ToolUnavailable)]
	[InlineData(DepotEnrollmentStates.DepotIdUnavailable)]
	[InlineData(DepotEnrollmentStates.AwaitingPortalRegistration)]
	[InlineData(DepotEnrollmentStates.ActivationCodeStored)]
	[InlineData(DepotEnrollmentStates.AuthFailing)]
	public async Task NotValidated_FailsClosedWithoutCallingTheTool(string? state)
	{
		DepotEnrollment? enrollment = state is null
			? null
			: new DepotEnrollment(state, null, null, null, null, null, null, DateTimeOffset.UtcNow);
		BinariesDownloadJobHandler handler = CreateHandler(enrollment);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("disabled", outcome.Note);
	}

	[Fact]
	public async Task MalformedPayload_FailsBeforeCheckingEnrollment()
	{
		BinariesDownloadJobHandler handler = CreateHandler(enrollment: null);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("not json"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("Malformed", outcome.Note);
	}

	[Fact]
	public async Task MissingExternalId_FailsWithActionableReason()
	{
		BinariesDownloadJobHandler handler = CreateHandler(enrollment: null);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("{\"depot_artifact_id\":\"00000000-0000-0000-0000-000000000001\"}"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("external_id", outcome.Note);
	}

	// The "Validated enrollment but no credential stored" branch and the full
	// success/tool-invocation path call through CredentialRepository.FindByTypeAsync
	// (real Postgres, sealed class) -- deferred, matching CatalogPullJobHandlerTests'
	// identical split (see its own deferred-coverage comment).

	[Fact]
	public void JobType_IsBinariesDownload()
	{
		BinariesDownloadJobHandler handler = CreateHandler(null);
		Assert.Equal("binaries-download", handler.JobType);
	}
}
