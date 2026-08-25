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
using Waypoint.Core.Logging;
using Waypoint.Core.Pagination;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>catalog-pull</c> job handler (issue #687): the enrollment-gate and
/// no-credential guards, which are fully fake-able without a real Postgres or
/// process invocation. The full success/authenticate/promote/index path resolves a
/// real <see cref="Waypoint.Infrastructure.Secrets.CredentialRepository"/> and spawns
/// a process (mirroring <c>DepotEnrollmentJobHandlerTests</c>'s own split), so it is
/// out of scope for this fast unit file -- see the PR description's deferred-coverage
/// note.
/// </summary>
public sealed class CatalogPullJobHandlerTests
{
	private sealed class FakeEnrollmentRepository(DepotEnrollment? enrollment) : IDepotEnrollmentRepository
	{
		public Task<DepotEnrollment?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(enrollment);
		public Task SetDepotIdAsync(string depotId, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task SetPairedAsync(string assetId, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task SetValidationOutcomeAsync(bool succeeded, string? failureNote, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task ResetAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class UnreachableIdentityTool : IDepotIdentityTool
	{
		public Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task SeedMachineIdentityAsync(string assetId, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called when the enrollment gate rejects the job first.");
		public Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class UnreachablePuller : IManagedToolMetadataPuller
	{
		public Task<CatalogPullResult> PullAsync(string depotPath, string activationCodePath, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called when the enrollment gate rejects the job first.");
	}

	private sealed class UnreachableCatalogVerifier : IManagedToolCatalogVerifier
	{
		public Task<ManagedToolCatalogAuthenticationResult> AuthenticateCatalogAsync(string repositoryRoot, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called when the enrollment gate rejects the job first.");

		public Task<ManagedToolCatalogVerificationResult> VerifyAsync(string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called when the enrollment gate rejects the job first.");
	}

	private sealed class UnreachableArtifactRepository : IDepotArtifactRepository
	{
		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken) => throw new InvalidOperationException();
		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(DepotArtifactFilter filter, PageRequest page, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakePullStateRepository : ICatalogPullStateRepository
	{
		public bool RecordFailureCalled { get; private set; }
		public bool IsAuthFailureRecorded { get; private set; }
		public string? RecordedFailureReason { get; private set; }

		public Task<CatalogPullState?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<CatalogPullState?>(null);

		public Task RecordSuccessAsync(int itemCount, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called when the enrollment gate rejects the job first.");

		public Task RecordFailureAsync(bool isAuthFailure, string failureReason, CancellationToken cancellationToken)
		{
			RecordFailureCalled = true;
			IsAuthFailureRecorded = isAuthFailure;
			RecordedFailureReason = failureReason;
			return Task.CompletedTask;
		}
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

	private static JobExecutionContext ContextFor()
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: Guid.NewGuid(), JobType: "catalog-pull", TargetId: null, TargetName: "depot",
			CredentialId: null, Priority: 1, Payload: "{}", AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private static (CatalogPullJobHandler Handler, FakePullStateRepository PullState) CreateHandler(DepotEnrollment? enrollment)
	{
		FakePullStateRepository pullState = new();
		CatalogPullJobHandler handler = new(
			new FakeEnrollmentRepository(enrollment),
			new UnreachableIdentityTool(),
			new UnreachablePuller(),
			new UnreachableCatalogVerifier(),
			new UnreachableArtifactRepository(),
			pullState,
			new UnreachableCredentialSecretStore(),
			new CredentialRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x"),
			new InPlaySecretRedactor(),
			Options.Create(new CatalogOptions()),
			Options.Create(new ManagedToolOptions()));
		return (handler, pullState);
	}

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
		(CatalogPullJobHandler handler, FakePullStateRepository pullState) = CreateHandler(enrollment);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("disabled", outcome.Note);
		Assert.True(pullState.RecordFailureCalled);
		Assert.False(pullState.IsAuthFailureRecorded);
	}

	// The "Validated enrollment but no credential stored" branch calls through
	// CredentialRepository.FindByTypeAsync (real Postgres, sealed class) --
	// covered end to end by CatalogPullNoCredentialEndToEndTests, mirroring
	// DepotEnrollmentJobHandlerTests/DepotEnrollmentValidateEndToEndTests' own split.

	[Fact]
	public void JobType_IsCatalogPull()
	{
		(CatalogPullJobHandler handler, _) = CreateHandler(null);
		Assert.Equal("catalog-pull", handler.JobType);
	}
}
