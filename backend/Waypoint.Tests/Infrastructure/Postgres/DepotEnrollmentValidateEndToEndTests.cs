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

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #691's <c>validate-code</c> branch: <c>DepotEnrollmentJobHandler</c> takes a
/// concrete <c>CredentialRepository</c>/<c>ICredentialSecretStore</c> (Postgres-only),
/// so its Activation Code decrypt-for-one-call cannot be exercised by a pure
/// in-memory unit test -- mirrors <c>ManagedToolInstallJobHandlerDepotFetchEndToEndTests</c>'s
/// own split for the identical reason. A <see cref="FakeDepotIdentityTool"/> stands in
/// for the real process invocation (covered on its own by
/// <c>DepotEnrollmentJobHandlerTests</c>'s generate-depot-id coverage and by manual
/// verification against the real tool) so this file's focus stays on the handler's own
/// decrypt/stage/cleanup/classify decisions.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer and removes the key dir.
public sealed class DepotEnrollmentValidateEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private const string InventedCode = "invented-validate-e2e-canary-9c31"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface

	/// <summary>Invented asset_id embedded in <see cref="InventedRealShapeCode"/> -- never a real Broadcom value.</summary>
	private const string InventedAssetId = "wpt-787-e2e-asset-0002";

	/// <summary>
	/// An invented Activation Code in the real shape the codec decodes (issue #787):
	/// base64 of a JSON object carrying an <c>asset_id</c>. Structurally faithful to the
	/// sibling contract, values entirely fabricated. gitleaks:allow.
	/// </summary>
	private static readonly string InventedRealShapeCode =
		Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"asset_id":"{{InventedAssetId}}","issued":"2026-01-01"}"""));

	// Hoisted to satisfy CA1861 (constant array arguments must not be allocated per call).
	private static readonly string[] StoredPairingSeededAssetIds = ["WPT-0001-DEPOT-ID"];
	private static readonly string?[] StoredPairingValidatedAssetIds = ["WPT-0001-DEPOT-ID"];

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-enrollment-validate-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private DepotEnrollmentRepository _enrollment = null!;

	public DepotEnrollmentValidateEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetEnrollmentAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_enrollment = new DepotEnrollmentRepository(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
	}

	private async Task ResetEnrollmentAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = 'activation_code_stored', depot_id = 'WPT-0001-DEPOT-ID', depot_id_generated_at = now(),
			    paired_asset_id = 'WPT-0001-DEPOT-ID', paired_at = now(), last_validation_failure = NULL, reset_at = NULL
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// The credential-panel path the owner actually used (issue #787): a code was stored
	/// directly, with NO prior enrollment interaction -- no Depot ID generated, no
	/// pairing recorded, identity home empty. Adopt-on-validate must recover this.
	/// </summary>
	private async Task SetEmptyPairingStateAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = 'depot_id_unavailable', depot_id = NULL, depot_id_generated_at = NULL,
			    paired_asset_id = NULL, paired_at = NULL, last_validation_failure = NULL, reset_at = NULL
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private sealed class FakeDepotIdentityTool : IDepotIdentityTool
	{
		private readonly DepotValidationResult _result;

		public FakeDepotIdentityTool(DepotValidationResult result)
		{
			_result = result;
		}

		public List<string> StagedPaths { get; } = [];
		public List<string> StagedContents { get; } = [];
		public List<string> SeededAssetIds { get; } = [];
		public List<string?> ValidatedAssetIds { get; } = [];

		public Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this file's validate-code scenarios.");

		public Task SeedMachineIdentityAsync(string assetId, CancellationToken cancellationToken)
		{
			SeededAssetIds.Add(assetId);
			return Task.CompletedTask;
		}

		public Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, string? expectedAssetId, CancellationToken cancellationToken)
		{
			StagedPaths.Add(activationCodePath);
			StagedContents.Add(File.Exists(activationCodePath) ? File.ReadAllText(activationCodePath) : "<missing>");
			ValidatedAssetIds.Add(expectedAssetId);
			return Task.FromResult(_result);
		}
	}

	private async Task<Guid> SeedActivationCodeCredentialAsync(string secret)
	{
		byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
		Guid? id = await new CredentialCreationCoordinator(_fixture.ConnectionString, new AesGcmEnvelopeCipher(new FileMasterKeyProvider(
			Path.Combine(_keyDirectory, "master.key"))), NullLogger<CredentialCreationCoordinator>.Instance)
			.CreateAsync("VCF Software Depot Activation Code", "depot-activation-code", "shared", sudoEnabled: false, username: null,
				secretBytes, "test-actor", CancellationToken.None);
		Assert.NotNull(id);
		return id!.Value;
	}

	private JobExecutionContext ContextFor(ClaimedJob job) =>
		new(job, "worker-test", _events, _repository, JobShape.Simple);

	private async Task<ClaimedJob> EnqueueValidateJobAsync()
	{
		Guid runId = await _repository.CreateRunAsync("depot-enrollment", "{}", credentialId: null, "test-actor", CancellationToken.None);
		JobSpec spec = new("depot-enrollment", 4, TargetId: null, TargetName: "depot-enrollment", Payload: """{"operation":"validate-code"}""");
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, [spec], "test-actor", CancellationToken.None);
		ClaimedJob? claimed = await _repository.ClaimJobAsync(
			"worker-test", TimeSpan.FromMinutes(5), new HashSet<string>(StringComparer.Ordinal) { "depot-enrollment" }, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(jobIds[0], claimed!.Id);
		return claimed;
	}

	[Fact]
	public async Task ValidateCode_NoActivationCodeConfigured_FailsCleanly()
	{
		FakeDepotIdentityTool tool = new(DepotValidationResult.Ok());
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("No credential", outcome.Note);
	}

	[Fact]
	public async Task ValidateCode_ToolAccepts_MarksValidated_StagesAndCleansUpTheCodeFile()
	{
		await SeedActivationCodeCredentialAsync(InventedCode);
		FakeDepotIdentityTool tool = new(DepotValidationResult.Ok());
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		DepotEnrollment? enrollment = await _enrollment.GetAsync(CancellationToken.None);
		Assert.Equal(DepotEnrollmentStates.Validated, enrollment!.State);

		// The decrypted code was staged to disk for exactly the bounded tool call, and
		// the file no longer exists once the handler returns (finally-block cleanup).
		Assert.Single(tool.StagedPaths);
		Assert.Equal(InventedCode, tool.StagedContents[0]);
		Assert.False(File.Exists(tool.StagedPaths[0]), "the job-scoped activation-code staging file must be deleted in finally.");
	}

	[Fact]
	public async Task ValidateCode_ToolRejects_MarksAuthFailing_NeverLeaksTheCodeIntoTheJobNote()
	{
		await SeedActivationCodeCredentialAsync(InventedCode);
		FakeDepotIdentityTool tool = new(DepotValidationResult.AuthFailed("Activation Code rejected: expired or revoked."));
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.AuthFailed, outcome.Kind);
		Assert.DoesNotContain(InventedCode, outcome.Note ?? string.Empty, StringComparison.Ordinal);

		DepotEnrollment? enrollment = await _enrollment.GetAsync(CancellationToken.None);
		Assert.Equal(DepotEnrollmentStates.AuthFailing, enrollment!.State);
		Assert.NotNull(enrollment.LastValidationFailure);
		Assert.DoesNotContain(InventedCode, enrollment.LastValidationFailure!, StringComparison.Ordinal);
		Assert.False(File.Exists(tool.StagedPaths[0]));
	}

	[Fact]
	public async Task ValidateCode_ToolCallItselfFails_ReportsOrdinaryFailure_NeverFlipsEnrollmentToAuthFailing()
	{
		await SeedActivationCodeCredentialAsync(InventedCode);
		FakeDepotIdentityTool tool = new(DepotValidationResult.Failed("vcf-download-tool is not installed."));
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);

		// A runner/environment problem (tool missing, timeout) is never conflated with
		// a real code rejection -- the enrollment state stays exactly as it was seeded
		// (activation_code_stored), not auth_failing.
		DepotEnrollment? enrollment = await _enrollment.GetAsync(CancellationToken.None);
		Assert.Equal(DepotEnrollmentStates.ActivationCodeStored, enrollment!.State);
	}

	[Fact]
	public async Task ValidateCode_ExistingPairing_SeedsMachineIdFromStoredPairing_BeforeInvoking()
	{
		// Generate-first (or already-adopted): a pairing is already recorded. Validation
		// re-seeds the identity file from that stored pairing -- the rebuild-survivability
		// path -- and never re-derives it from the code, preserving existing semantics.
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		FakeDepotIdentityTool tool = new(DepotValidationResult.Ok());
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		// The stored pairing (WPT-0001-DEPOT-ID, seeded by ResetEnrollmentAsync) is what
		// gets seeded and validated against -- not the code's own asset_id.
		Assert.Equal(StoredPairingSeededAssetIds, tool.SeededAssetIds);
		Assert.Equal(StoredPairingValidatedAssetIds, tool.ValidatedAssetIds);
	}

	[Fact]
	public async Task ValidateCode_NoPriorPairing_AdoptsStoredCodeIdentity_RecoversAndRecordsAdoption()
	{
		// Issue #787 AC: the credential-panel path -- code stored with no prior
		// enrollment interaction, empty identity home. Adopt-on-validate decodes the
		// code's asset_id, adopts it as the managed Depot ID + pairing, seeds machine_id,
		// and reaches validated WITHOUT the operator re-entering the code.
		await SetEmptyPairingStateAsync();
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		FakeDepotIdentityTool tool = new(DepotValidationResult.Ok());
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);

		// machine_id was seeded from the decoded asset_id, and the tool validated against it.
		Assert.Equal(new[] { InventedAssetId }, tool.SeededAssetIds);
		Assert.Equal(new string?[] { InventedAssetId }, tool.ValidatedAssetIds);

		// The adoption is durable and visible: depot_id + pairing both record the asset_id.
		DepotEnrollment? enrollment = await _enrollment.GetAsync(CancellationToken.None);
		Assert.Equal(DepotEnrollmentStates.Validated, enrollment!.State);
		Assert.Equal(InventedAssetId, enrollment.DepotId);
		Assert.Equal(InventedAssetId, enrollment.PairedAssetId);
		Assert.NotNull(enrollment.DepotIdGeneratedAt);
	}

	[Fact]
	public async Task ValidateCode_AdoptedIdentity_NeverLeaksCodeValueIntoEnrollmentOrNote()
	{
		// The raw code (its base64 body) must never appear on any persistence surface,
		// even on the adopt path that decodes it. Only the non-secret asset_id is stored.
		await SetEmptyPairingStateAsync();
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		FakeDepotIdentityTool tool = new(DepotValidationResult.Ok());
		DepotEnrollmentJobHandler handler = new(tool, _enrollment, _secretStore, _credentials, _redactor);
		ClaimedJob job = await EnqueueValidateJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.DoesNotContain(InventedRealShapeCode, outcome.Note ?? string.Empty, StringComparison.Ordinal);

		DepotEnrollment? enrollment = await _enrollment.GetAsync(CancellationToken.None);
		Assert.DoesNotContain(InventedRealShapeCode, enrollment!.DepotId ?? string.Empty, StringComparison.Ordinal);
		Assert.DoesNotContain(InventedRealShapeCode, enrollment.PairedAssetId ?? string.Empty, StringComparison.Ordinal);
		Assert.False(File.Exists(tool.StagedPaths[0]));
	}
}
