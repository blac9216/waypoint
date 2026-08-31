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
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.Secrets;
using Waypoint.Infrastructure.Catalog;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #687's <c>catalog-pull</c> job handler, end to end against real Postgres:
/// <c>CatalogPullJobHandler</c> takes a concrete <c>CredentialRepository</c>/
/// <c>ICredentialSecretStore</c> (Postgres-only), so its Activation Code
/// decrypt-for-one-call cannot be exercised by a pure in-memory unit test -- mirrors
/// <c>DepotEnrollmentValidateEndToEndTests</c>'s own split for the identical reason.
/// A <see cref="FakeMetadataPuller"/> stands in for the real <c>vcf-download-tool</c>
/// process invocation (covered on its own elsewhere). The catalog authentication step,
/// however, is driven through the REAL <see cref="BroadcomManagedToolCatalogVerifier"/>
/// wired exactly as production DI wires it -- see <see cref="CreateRealVerifier"/> and
/// the signed-catalog fixture in <see cref="StageSignedCatalog"/> -- so a green success
/// test cannot hide a broken production authentication path (PR #763 round-1 Finding 2:
/// the prior fake-verifier E2E kept the suite green while the real path failed on every
/// pull). A <see cref="FakeCatalogVerifier"/> is retained only for the handler failure-
/// plumbing tests whose failure a real signed fixture cannot cheaply produce (a
/// mid-stream promotion IO error, a genuinely malformed-but-authenticated body); each
/// such test says so at its call site.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer and removes the state dir.
public sealed class CatalogPullEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private const string InventedCode = "invented-catalog-pull-e2e-canary-7f2a"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface

	private static readonly string SampleCatalogJson = """
		{
		  "patches": {
		    "VCENTER": [
		      {
		        "productVersion": "8.0.3.00900-25413364",
		        "releaseDate": "2026-07-13T08:35:13Z",
		        "artifacts": {
		          "bundles": [
		            {
		              "id": "bundle-1",
		              "type": "INSTALL",
		              "binaries": [
		                { "fileName": "VMware-VCSA-all-8.0.3-25413364.iso", "checksum": "aa11", "size": 1024 }
		              ]
		            }
		          ]
		        }
		      }
		    ]
		  }
		}
		""";

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-catalog-pull-key").FullName;
	private readonly string _toolStatePath = Directory.CreateTempSubdirectory("wp-catalog-pull-tool-state").FullName;
	private readonly string _depotPath = Directory.CreateTempSubdirectory("wp-catalog-pull-depot").FullName;
	private readonly string _trustCertPath = Path.Combine(Directory.CreateTempSubdirectory("wp-catalog-pull-trust").FullName, "catalog-trust.cert");
	private readonly RSA _signingKey = RSA.Create(2048);
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private DepotEnrollmentRepository _enrollment = null!;
	private CatalogPullStateRepository _pullState = null!;
	private DepotArtifactRepositoryForTest _artifacts = null!;

	public CatalogPullEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetEnrollmentAsync();
		await ResetPullStateAsync();
		await ResetArtifactsAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);
		_events = new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_enrollment = new DepotEnrollmentRepository(_fixture.ConnectionString);
		_pullState = new CatalogPullStateRepository(_fixture.ConnectionString);
		_artifacts = new DepotArtifactRepositoryForTest(_fixture.ConnectionString);
	}

	public Task DisposeAsync() => Task.CompletedTask;

	public void Dispose()
	{
		_signingKey.Dispose();
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_toolStatePath, recursive: true);
		Directory.Delete(_depotPath, recursive: true);
		Directory.Delete(Path.GetDirectoryName(_trustCertPath)!, recursive: true);
	}

	private async Task ResetEnrollmentAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE depot_enrollment
			SET state = 'validated', depot_id = 'WPT-0001-DEPOT-ID', depot_id_generated_at = now(),
			    paired_asset_id = 'WPT-0001-DEPOT-ID', paired_at = now(), last_validation_failure = NULL, reset_at = NULL
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	private async Task ResetPullStateAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			UPDATE catalog_pull_state
			SET last_attempt_at = NULL, last_outcome = NULL, last_failure_reason = NULL,
			    last_success_at = NULL, last_success_item_count = NULL
			WHERE id = 1
			""", connection);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// <see cref="PostgresFixture.ResetJobEngineDataAsync"/> deliberately does not
	/// truncate <c>depot_artifacts</c> (it is not job-engine data, and other
	/// suites -- e.g. <c>CatalogApiTests</c> -- seed/read it independently), so this
	/// file resets it itself: the item-count assertions below need a known-empty
	/// starting table, not whatever rows a prior test in this shared Postgres
	/// instance happened to leave behind.
	/// </summary>
	private async Task ResetArtifactsAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand truncate = new("TRUNCATE TABLE depot_artifacts RESTART IDENTITY CASCADE", connection);
		await truncate.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Stands in for the real <c>vcf-download-tool metadata download</c> process: writes
	/// the staged <c>productVersionCatalog.json</c> into the depot path the handler hands
	/// it. When <paramref name="signWith"/> is supplied it ALSO writes a real
	/// signature-envelope <c>.sig</c> over the catalog's exact bytes (following the
	/// <c>BroadcomManagedToolCatalogVerifierTests</c> fixture convention) so the handler's
	/// REAL verifier authenticates it -- and when <paramref name="tamperAfterSigning"/> is
	/// set, mutates the catalog after signing so the real verifier rejects it (negative
	/// proof). The signing key is invented per test run; nothing here is a real credential.
	/// </summary>
	/// <summary>
	/// Records machine_id seeding (issue #787: every consumer seeds independently from the
	/// code it decrypts, before invoking the tool) without touching the filesystem. Never
	/// invokes the real tool -- the pull itself is faked by <see cref="FakeMetadataPuller"/>.
	/// </summary>
	private sealed class RecordingIdentityTool : IDepotIdentityTool
	{
		public List<string> SeededAssetIds { get; } = [];

		public Task<DepotIdentityResult> GetDepotIdAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

		public Task SeedMachineIdentityAsync(string assetId, CancellationToken cancellationToken)
		{
			SeededAssetIds.Add(assetId);
			return Task.CompletedTask;
		}

		public Task<DepotValidationResult> ValidateActivationCodeAsync(string activationCodePath, CancellationToken cancellationToken) => throw new InvalidOperationException();
	}

	private sealed class FakeMetadataPuller(
		CatalogPullResult result,
		string catalogJsonToWrite = "",
		CatalogSigner? signWith = null,
		bool tamperAfterSigning = false) : IManagedToolMetadataPuller
	{
		public List<string> DepotPaths { get; } = [];
		public List<string> StagedContents { get; } = [];

		public Task<CatalogPullResult> PullAsync(string depotPath, string activationCodePath, CancellationToken cancellationToken)
		{
			DepotPaths.Add(depotPath);
			StagedContents.Add(File.Exists(activationCodePath) ? File.ReadAllText(activationCodePath) : "<missing>");

			if (result.Succeeded && !string.IsNullOrEmpty(catalogJsonToWrite))
			{
				string metadataDir = Path.Combine(depotPath, "PROD", "metadata", "productVersionCatalog", "v1");
				Directory.CreateDirectory(metadataDir);
				string catalogFilePath = Path.Combine(metadataDir, "productVersionCatalog.json");
				byte[] catalogBytes = Encoding.UTF8.GetBytes(catalogJsonToWrite);
				File.WriteAllBytes(catalogFilePath, catalogBytes);

				if (signWith is not null)
				{
					File.WriteAllText(Path.Combine(metadataDir, "productVersionCatalog.sig"), signWith.EnvelopeFor(catalogBytes));

					if (tamperAfterSigning)
					{
						File.AppendAllText(catalogFilePath, " ");
					}
				}
			}

			return Task.FromResult(result);
		}
	}

	/// <summary>
	/// Invented trust chain: one self-signed certificate over a per-run key. The SAME
	/// certificate bytes are provisioned as the verifier's trust anchor (via
	/// <see cref="ProvisionTrustCert"/>) and embedded in every signature envelope, so the
	/// real verifier's fixed-time cert-equality check matches -- exactly the
	/// <c>BroadcomManagedToolCatalogVerifierTests</c> fixture shape. Nothing real.
	/// </summary>
	private sealed class CatalogSigner
	{
		private readonly RSA _key;
		private readonly string _certificatePem;

		public CatalogSigner(RSA key)
		{
			_key = key;
			CertificateRequest request = new("CN=Invented Catalog Signer", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
			_certificatePem = certificate.ExportCertificatePem();
		}

		public string CertificatePem => _certificatePem;

		public string EnvelopeFor(byte[] catalogBytes)
		{
			byte[] signature = _key.SignData(catalogBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
			return $"SHA256(2f431d2654aeecbc058dd054d0dbb7ce)= {Convert.ToHexString(signature).ToLowerInvariant()}\n{_certificatePem}";
		}
	}

	/// <summary>
	/// Retained ONLY for the handler failure-plumbing tests whose failure the real
	/// verifier cannot cheaply produce from a signed fixture -- e.g. asserting the
	/// handler classifies an authentication failure and preserves the prior-good
	/// catalog. The success and zero-item paths use the REAL verifier
	/// (<see cref="CreateRealVerifier"/>). This fake canned an <c>Ok</c> for the
	/// authenticate step; PR #763 Finding 2 was that no test drove the real one.
	/// </summary>
	private sealed class FakeCatalogVerifier(ManagedToolCatalogAuthenticationResult authResult) : IManagedToolCatalogVerifier
	{
		public Task<ManagedToolCatalogAuthenticationResult> AuthenticateCatalogAsync(string repositoryRoot, CancellationToken cancellationToken) =>
			Task.FromResult(authResult);

		public Task<ManagedToolCatalogVerificationResult> VerifyAsync(string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken) =>
			throw new NotSupportedException("catalog-pull only uses AuthenticateCatalogAsync.");
	}

	/// <summary>Plain wrapper so this file does not need a full DepotArtifactRepository re-implementation -- delegates to the real Postgres-backed one.</summary>
	private sealed class DepotArtifactRepositoryForTest(string connectionString) : IDepotArtifactRepository
	{
		private readonly Waypoint.Infrastructure.Catalog.DepotArtifactRepository _inner = new(connectionString);

		public Task<Guid> UpsertAsync(DepotArtifactUpsert artifact, CancellationToken cancellationToken) => _inner.UpsertAsync(artifact, cancellationToken);

		public Task<DepotArtifact?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => _inner.GetByIdAsync(id, cancellationToken);

		public Task<(IReadOnlyList<DepotArtifact> Items, long TotalCount)> ListAsync(DepotArtifactFilter filter, Waypoint.Core.Pagination.PageRequest page, CancellationToken cancellationToken) =>
			_inner.ListAsync(filter, page, cancellationToken);
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

	private async Task<ClaimedJob> EnqueuePullJobAsync()
	{
		Guid runId = await _repository.CreateRunAsync("catalog-pull", "{}", credentialId: null, "test-actor", CancellationToken.None);
		JobSpec spec = new("catalog-pull", 1, TargetId: null, TargetName: "depot", Payload: "{}");
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, [spec], "test-actor", CancellationToken.None);
		ClaimedJob? claimed = await _repository.ClaimJobAsync(
			"worker-test", TimeSpan.FromMinutes(5), new HashSet<string>(StringComparer.Ordinal) { "catalog-pull" }, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(jobIds[0], claimed!.Id);
		return claimed;
	}

	private CatalogPullJobHandler CreateHandler(IManagedToolMetadataPuller puller, IManagedToolCatalogVerifier verifier)
	{
		ManagedToolOptions toolOptions = new() { ToolStatePath = _toolStatePath, CatalogTrustCertificatePath = _trustCertPath };
		CatalogOptions catalogOptions = new() { DepotPath = _depotPath };
		return new CatalogPullJobHandler(
			_enrollment, new RecordingIdentityTool(), puller, verifier, _artifacts, _pullState, _secretStore, _credentials, _redactor,
			Options.Create(catalogOptions), Options.Create(toolOptions));
	}

	/// <summary>
	/// The REAL production verifier (the exact concrete type production DI binds
	/// <c>IManagedToolCatalogVerifier</c> to), pointed at the invented trust certificate
	/// that <see cref="ProvisionTrustCertFor"/> derives from the per-run signing key.
	/// </summary>
	private BroadcomManagedToolCatalogVerifier CreateRealVerifier() =>
		new(Options.Create(new ManagedToolOptions
		{
			ToolStatePath = _toolStatePath,
			CatalogTrustCertificatePath = _trustCertPath,
		}));

	/// <summary>
	/// Provisions the independently trusted certificate (the signer's own cert bytes) as
	/// the verifier's trust anchor, so the real verifier's fixed-time cert-equality check
	/// matches the cert the puller embeds in the <c>.sig</c> envelope.
	/// </summary>
	private void ProvisionTrustCert(CatalogSigner signer) =>
		File.WriteAllText(_trustCertPath, signer.CertificatePem);

	[Fact]
	public async Task NoActivationCodeConfigured_FailsCleanly_NeverCallsThePuller()
	{
		// Fails at the credential gate before the verifier is ever consulted, so the fake
		// verifier here is inert (its AuthenticateCatalogAsync is never called).
		FakeMetadataPuller puller = new(CatalogPullResult.Ok());
		CatalogPullJobHandler handler = CreateHandler(puller, new FakeCatalogVerifier(ManagedToolCatalogAuthenticationResult.Ok()));
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("No credential", outcome.Note);
		Assert.Empty(puller.DepotPaths);
	}

	[Fact]
	public async Task ToolSucceeds_CatalogAuthenticates_PromotesAndIndexes_CleansUpStaging()
	{
		// Drives the handler through the REAL BroadcomManagedToolCatalogVerifier wired as
		// production DI wires it, against an invented signed catalog fixture (Finding 2).
		await SeedActivationCodeCredentialAsync(InventedCode);
		CatalogSigner signer = new(_signingKey);
		ProvisionTrustCert(signer);
		FakeMetadataPuller puller = new(CatalogPullResult.Ok(), SampleCatalogJson, signWith: signer);
		CatalogPullJobHandler handler = CreateHandler(puller, CreateRealVerifier());
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Contains("1 artifact", outcome.Note);

		// The Activation Code was staged to disk for exactly the bounded tool call and
		// never leaked into the job outcome/note.
		Assert.Single(puller.StagedContents);
		Assert.Equal(InventedCode, puller.StagedContents[0]);
		Assert.DoesNotContain(InventedCode, outcome.Note ?? string.Empty, StringComparison.Ordinal);

		// The authenticated catalog was atomically promoted to the active depot path.
		string activeCatalogPath = Path.Combine(_depotPath, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.json");
		Assert.True(File.Exists(activeCatalogPath));

		// The parsed binary was upserted into depot_artifacts with product/version
		// populated (unlike the local re-index, which always leaves both null).
		(IReadOnlyList<DepotArtifact> items, long total) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new Waypoint.Core.Pagination.PageRequest(), CancellationToken.None);
		Assert.Equal(1, total);
		Assert.Equal("VCENTER", items[0].Product);
		Assert.Equal("8.0.3.00900-25413364", items[0].Version);

		// catalog_pull_state records a genuine success with the real item count.
		CatalogPullState? state = await _pullState.GetAsync(CancellationToken.None);
		Assert.Equal(CatalogPullOutcomes.Succeeded, state!.LastOutcome);
		Assert.Equal(1, state.LastSuccessItemCount);
		Assert.NotNull(state.LastSuccessAt);

		// Job-scoped staging directory is fully cleaned up.
		Assert.False(Directory.Exists(Path.Combine(_toolStatePath, "catalog-pull-staging", $"job-{job.Id:N}")));
	}

	[Fact]
	public async Task ZeroItemAuthenticatedCatalog_IsRecordedAsAGenuineSuccess()
	{
		// Real verifier + invented signed empty catalog (Finding 2): proves the zero-item
		// "genuine success" path is reachable only AFTER real authentication passes.
		await SeedActivationCodeCredentialAsync(InventedCode);
		const string emptyCatalog = """{"patches": {"VCENTER": []}}""";
		CatalogSigner signer = new(_signingKey);
		ProvisionTrustCert(signer);
		FakeMetadataPuller puller = new(CatalogPullResult.Ok(), emptyCatalog, signWith: signer);
		CatalogPullJobHandler handler = CreateHandler(puller, CreateRealVerifier());
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		CatalogPullState? state = await _pullState.GetAsync(CancellationToken.None);
		Assert.Equal(CatalogPullOutcomes.Succeeded, state!.LastOutcome);
		Assert.Equal(0, state.LastSuccessItemCount);
	}

	[Fact]
	public async Task ToolRejectsActivationCode_RecordsAuthFailure_NeverPromotesAnything_NeverLeaksTheCode()
	{
		// The metadata-download (tool) call fails before authentication, so the verifier is
		// never consulted -- the fake is inert here.
		await SeedActivationCodeCredentialAsync(InventedCode);
		FakeMetadataPuller puller = new(CatalogPullResult.AuthFailed("Activation Code rejected: expired or revoked."));
		CatalogPullJobHandler handler = CreateHandler(puller, new FakeCatalogVerifier(ManagedToolCatalogAuthenticationResult.Ok()));
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.AuthFailed, outcome.Kind);
		Assert.DoesNotContain(InventedCode, outcome.Note ?? string.Empty, StringComparison.Ordinal);

		CatalogPullState? state = await _pullState.GetAsync(CancellationToken.None);
		Assert.Equal(CatalogPullOutcomes.AuthFailed, state!.LastOutcome);
		Assert.Null(state.LastSuccessAt);

		string activeCatalogPath = Path.Combine(_depotPath, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.json");
		Assert.False(File.Exists(activeCatalogPath));
	}

	[Fact]
	public async Task TamperedCatalog_FailsAuthenticationThroughTheRealVerifier_FailsClosed_PriorGoodCatalogUntouched()
	{
		// NEGATIVE PROOF (Finding 1): a pull whose staged catalog is mutated AFTER signing
		// is rejected by the REAL BroadcomManagedToolCatalogVerifier (signature no longer
		// matches the bytes), so nothing is promoted or indexed -- the same real-verifier
		// path the success test exercises, driven to a genuine authentication failure.
		await SeedActivationCodeCredentialAsync(InventedCode);
		CatalogSigner signer = new(_signingKey);
		ProvisionTrustCert(signer);

		// Seed a prior-good catalog on the active depot path.
		string activeCatalogPath = Path.Combine(_depotPath, "PROD", "metadata", "productVersionCatalog", "v1", "productVersionCatalog.json");
		Directory.CreateDirectory(Path.GetDirectoryName(activeCatalogPath)!);
		File.WriteAllText(activeCatalogPath, "prior-good-catalog-contents");

		FakeMetadataPuller puller = new(CatalogPullResult.Ok(), SampleCatalogJson, signWith: signer, tamperAfterSigning: true);
		CatalogPullJobHandler handler = CreateHandler(puller, CreateRealVerifier());
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("authentication", outcome.Note);
		Assert.Contains("signature is invalid", outcome.Note, StringComparison.OrdinalIgnoreCase);

		// Prior-good catalog on disk is untouched.
		Assert.Equal("prior-good-catalog-contents", File.ReadAllText(activeCatalogPath));

		(IReadOnlyList<DepotArtifact> items, long total) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new Waypoint.Core.Pagination.PageRequest(), CancellationToken.None);
		Assert.Equal(0, total);
	}

	[Fact]
	public async Task UnsignedCatalog_IsRejectedByTheRealVerifier_FailsClosed()
	{
		// NEGATIVE PROOF (Finding 1), companion to the tampered case: a pull whose staged
		// catalog has no signature envelope at all (signWith: null) is rejected by the real
		// verifier for a missing signature, never promoted or indexed.
		await SeedActivationCodeCredentialAsync(InventedCode);
		CatalogSigner signer = new(_signingKey);
		ProvisionTrustCert(signer);
		FakeMetadataPuller puller = new(CatalogPullResult.Ok(), SampleCatalogJson);
		CatalogPullJobHandler handler = CreateHandler(puller, CreateRealVerifier());
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("authentication", outcome.Note);

		(IReadOnlyList<DepotArtifact> items, long total) = await _artifacts.ListAsync(
			new DepotArtifactFilter(null, null, null), new Waypoint.Core.Pagination.PageRequest(), CancellationToken.None);
		Assert.Equal(0, total);
	}

	[Fact]
	public async Task MalformedAuthenticatedCatalog_FailsClosed()
	{
		// Real verifier: the catalog IS validly signed (authenticates), but its body is not
		// valid JSON, so the parser -- downstream of authentication -- fails it closed.
		await SeedActivationCodeCredentialAsync(InventedCode);
		CatalogSigner signer = new(_signingKey);
		ProvisionTrustCert(signer);
		FakeMetadataPuller puller = new(CatalogPullResult.Ok(), "{not-valid-json", signWith: signer);
		CatalogPullJobHandler handler = CreateHandler(puller, CreateRealVerifier());
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("malformed", outcome.Note, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ToolCallItselfFails_ReportsOrdinaryFailure_NotAuthFailure()
	{
		// The tool call itself fails before authentication; the verifier is never consulted.
		await SeedActivationCodeCredentialAsync(InventedCode);
		FakeMetadataPuller puller = new(CatalogPullResult.Failed("vcf-download-tool is not installed."));
		CatalogPullJobHandler handler = CreateHandler(puller, new FakeCatalogVerifier(ManagedToolCatalogAuthenticationResult.Ok()));
		ClaimedJob job = await EnqueuePullJobAsync();

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		CatalogPullState? state = await _pullState.GetAsync(CancellationToken.None);
		Assert.Equal(CatalogPullOutcomes.Failed, state!.LastOutcome);
	}
}
