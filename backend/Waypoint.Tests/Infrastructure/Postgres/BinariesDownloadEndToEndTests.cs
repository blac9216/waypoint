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
/// PR #1648 review (round 1, Finding 3): <c>BinariesDownloadJobHandler</c> takes a
/// concrete <c>CredentialRepository</c>/<c>ICredentialSecretStore</c> (Postgres-only)
/// to reach the Activation Code, so the staging-file failure-cleanup path cannot be
/// exercised by <c>BinariesDownloadJobHandlerTests</c>'s pure in-memory unit style --
/// mirrors <c>CatalogPullEndToEndTests</c>'s own identical split.
///
/// The prior handler shape caught only <see cref="CredentialSecretNotFoundException"/>
/// and <see cref="MasterKeyUnavailableException"/> around the decrypt-then-write
/// sequence; an <see cref="IOException"/> thrown by the write itself (e.g. the staging
/// file momentarily locked by another handle -- the real-world shape of a concurrent
/// reader, a slow antivirus/backup scanner, or a second job racing the same job id)
/// propagated straight out of the method, skipping the staging-root cleanup below it
/// and leaving the decrypted Activation Code secret on disk indefinitely. The fix wraps
/// the whole staged-file lifecycle in one outer try/finally that always removes the
/// staging root, exception or not.
/// </summary>
[Collection("Postgres")]
public sealed class BinariesDownloadEndToEndTests : IAsyncLifetime, IDisposable
{
	private const string InventedCode = "invented-binaries-download-e2e-canary-9c3f"; // gitleaks:allow — invented test canary, never a real credential

	/// <summary>Invented asset_id embedded in <see cref="InventedRealShapeCode"/> -- never a real Broadcom value.</summary>
	private const string InventedAssetId = "wpt-1486-e2e-asset-0001";

	/// <summary>
	/// An invented Activation Code in the real shape the codec decodes (issue #787,
	/// mirroring <c>DepotEnrollmentValidateEndToEndTests</c>'s identical fixture):
	/// base64 of a JSON object carrying an <c>asset_id</c> -- unlike the bare
	/// <see cref="InventedCode"/> canary above (which only needs to reach the
	/// activation-code decrypt step before <c>WriteRestrictedFileThrowsIOException</c>
	/// throws), the finding-2 coverage tests below run the handler all the way to the
	/// real tool invocation, which requires a code that actually decodes an asset_id
	/// to seed identity from. Values entirely fabricated. gitleaks:allow.
	/// </summary>
	private static readonly string InventedRealShapeCode =
		Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"asset_id":"{{InventedAssetId}}","issued":"2026-01-01"}"""));

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-binaries-download-key").FullName;
	private readonly string _toolStatePath = Directory.CreateTempSubdirectory("wp-binaries-download-tool-state").FullName;
	private readonly string _depotPath = Directory.CreateTempSubdirectory("wp-binaries-download-depot").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private JobEventPublisher _events = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private DepotEnrollmentRepository _enrollment = null!;

	public BinariesDownloadEndToEndTests(PostgresFixture fixture)
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
		Directory.Delete(_toolStatePath, recursive: true);
		Directory.Delete(_depotPath, recursive: true);
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

	/// <summary>Never expected to be called: the IOException below aborts before the tool would be invoked.</summary>
	private sealed class UnreachableTool : IBinariesDownloadTool
	{
		public Task<BinariesDownloadResult> DownloadAsync(
			string externalId, string depotStorePath, string activationCodePath, string identityHome, string assetId,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called: the staged-file write fails before the tool would run.");
	}

	/// <summary>
	/// Issue #1486 review (round 1, finding 2): the real gap the prior <see cref="UnreachableTool"/>
	/// left uncovered -- a fake that actually WRITES bytes to <c>&lt;depotStorePath&gt;/&lt;externalId&gt;</c>
	/// and reports success, the way the real <c>vcf-download-tool</c> writes to
	/// <c>--depot-store</c>, so control genuinely reaches <c>VerifyAndRecordAsync</c>
	/// against real Postgres. Optionally runs <paramref name="sideEffect"/> after
	/// writing (before returning success) to model a catalog row vanishing "mid-flight",
	/// between the tool writing the file and the handler looking the row back up.
	/// </summary>
	private sealed class WritingTool : IBinariesDownloadTool
	{
		private readonly byte[] _bytes;
		private readonly Func<Task>? _sideEffect;

		public WritingTool(byte[] bytes, Func<Task>? sideEffect = null)
		{
			_bytes = bytes;
			_sideEffect = sideEffect;
		}

		public async Task<BinariesDownloadResult> DownloadAsync(
			string externalId, string depotStorePath, string activationCodePath, string identityHome, string assetId,
			CancellationToken cancellationToken)
		{
			string destination = Path.Combine(depotStorePath, externalId);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			await File.WriteAllBytesAsync(destination, _bytes, cancellationToken).ConfigureAwait(false);

			if (_sideEffect is not null)
			{
				await _sideEffect().ConfigureAwait(false);
			}

			return BinariesDownloadResult.Ok("binaries download completed.");
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

	private async Task<ClaimedJob> EnqueueBinariesDownloadJobAsync(Guid? depotArtifactId = null, string externalId = "vcf-bundle-01")
	{
		Guid runId = await _repository.CreateRunAsync(RunTypes.BinariesDownload, "{}", credentialId: null, "test-actor", CancellationToken.None);
		JobSpec spec = new(RunTypes.BinariesDownload, 1, TargetId: null, TargetName: "bundle-01",
			Payload: $$"""{"depot_artifact_id":"{{depotArtifactId ?? Guid.NewGuid()}}","external_id":"{{externalId}}"}""");
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, [spec], "test-actor", CancellationToken.None);
		ClaimedJob? claimed = await _repository.ClaimJobAsync(
			"worker-test", TimeSpan.FromMinutes(5), new HashSet<string>(StringComparer.Ordinal) { RunTypes.BinariesDownload }, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(jobIds[0], claimed!.Id);
		return claimed;
	}

	private BinariesDownloadJobHandler CreateHandler(IBinariesDownloadTool? tool = null) =>
		new(
			_enrollment, tool ?? new UnreachableTool(), _secretStore, _credentials,
			new DepotArtifactRepository(_fixture.ConnectionString), new BinaryDownloadVerifier(),
			Options.Create(new ManagedToolOptions { ToolStatePath = _toolStatePath }),
			Options.Create(new CatalogOptions { DepotPath = _depotPath }));

	private Task<Guid> SeedArtifactAsync(string externalId, string? sha256, long? sizeBytes, string status = "indexed") =>
		new DepotArtifactRepository(_fixture.ConnectionString)
			.UpsertAsync(new DepotArtifactUpsert(externalId, sha256, status, "{}", sizeBytes), CancellationToken.None);

	private async Task<DepotArtifact?> GetArtifactAsync(Guid id) =>
		await new DepotArtifactRepository(_fixture.ConnectionString).GetByIdAsync(id, CancellationToken.None);

	private async Task DeleteArtifactAsync(Guid id)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("DELETE FROM depot_artifacts WHERE id = $1", connection);
		command.Parameters.AddWithValue(id);
		await command.ExecuteNonQueryAsync();
	}

	/// <summary>
	/// Mirrors, in miniature, what <c>WaypointCatalogIndex.psm1</c>'s unfiltered
	/// <c>Get-FileManifest</c> disk walk would emit for exactly one relative path: the
	/// SHA-256 of whatever bytes currently sit there, or null if nothing does (issue
	/// #1486 review round 1, finding 1's "index-style upsert" probe). Used to prove the
	/// quarantine step keeps a failed file from ever being seen by that walk -- a
	/// present file at this path would launder its own hash back into the catalog via
	/// the upsert's <c>COALESCE(EXCLUDED.sha256, depot_artifacts.sha256)</c>.
	/// </summary>
	private static async Task<string?> IndexWalkHashOrNullAsync(string depotStorePath, string relativePath)
	{
		string path = Path.Combine(depotStorePath, relativePath);
		if (!File.Exists(path))
		{
			return null;
		}

		await using FileStream stream = File.OpenRead(path);
		byte[] hash = await SHA256.HashDataAsync(stream, CancellationToken.None);
		return Convert.ToHexString(hash).ToLowerInvariant();
	}

	/// <summary>
	/// Finding 3's class-killer: the staged Activation Code file's own path is held open
	/// (elsewhere, exclusively) BEFORE the handler ever runs, so the handler's own
	/// <c>WriteRestrictedFileAsync</c> is guaranteed to fail with a genuine
	/// <see cref="IOException"/> (".. because it is being used by another process") the
	/// instant it tries to create that exact path -- proving the fix's single
	/// try/finally, not a scenario invented to merely resemble one. Without the round-1
	/// fix, the staging directory (and the never-cleaned decrypted secret it would have
	/// held) survives this failure; with it, cleanup runs regardless.
	/// </summary>
	[Fact]
	public async Task WriteRestrictedFileThrowsIOException_StagingRootIsStillRemoved()
	{
		await SeedActivationCodeCredentialAsync(InventedCode);
		BinariesDownloadJobHandler handler = CreateHandler();
		ClaimedJob job = await EnqueueBinariesDownloadJobAsync();

		string stagingRoot = Path.Combine(_toolStatePath, "binaries-download-staging", $"job-{job.Id:N}");
		string activationCodePath = Path.Combine(stagingRoot, "activation-code.txt");
		Directory.CreateDirectory(stagingRoot);

		FileStreamOptions holdOptions = new() { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
		await using (FileStream held = new(activationCodePath, holdOptions))
		{
			// The handler's own write into this exact path is forced to fail with a
			// genuine IOException -- the file is already open, exclusively, elsewhere.
			IOException thrown = await Assert.ThrowsAsync<IOException>(
				() => handler.ExecuteAsync(ContextFor(job), CancellationToken.None));
			Assert.Contains("used by another process", thrown.Message, StringComparison.OrdinalIgnoreCase);
		}

		// The lock is released once `held` is disposed above; the staging root (and the
		// secret file it would otherwise have kept) must be gone -- the finally's
		// cleanup ran despite the exception, not only on the success/classified-failure
		// paths.
		Assert.False(Directory.Exists(stagingRoot));
		Assert.False(File.Exists(activationCodePath));
	}

	/// <summary>
	/// Issue #1486 review (round 1, finding 2): the code that enforces the issue's
	/// headline AC -- "a verification failure never results in the artifact being
	/// reported present" -- had zero coverage that actually reached
	/// <c>VerifyAndRecordAsync</c>. This is the happy path through the real verifier
	/// against real Postgres: a matching catalog row, a tool that WRITES the correct
	/// bytes, and a job that must land <c>present</c> with the freshly computed
	/// self-hash (grill decision Q8). Flipping the handler's success branch to
	/// anything else fails this test.
	/// </summary>
	[Fact]
	public async Task VerifiedDownload_RecordsPresent_WithComputedSelfHash()
	{
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		byte[] bytes = "genuine-binaries-download-e2e-bytes"u8.ToArray();
		string expectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		string externalId = "vcf-bundle-" + Guid.NewGuid().ToString("N");
		Guid artifactId = await SeedArtifactAsync(externalId, expectedSha256, bytes.Length);

		BinariesDownloadJobHandler handler = CreateHandler(new WritingTool(bytes));
		ClaimedJob job = await EnqueueBinariesDownloadJobAsync(artifactId, externalId);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		DepotArtifact? artifact = await GetArtifactAsync(artifactId);
		Assert.Equal("present", artifact!.Status);
		Assert.Equal(expectedSha256, artifact.Sha256);
	}

	/// <summary>
	/// Issue #1486 review (round 1, finding 2): grill decision Q8's whole point --
	/// a size-only catalog row (no vendor-published hash) picks one up on its first
	/// verified download, proven against real Postgres rather than resting only on
	/// the COALESCE direction being right in isolation.
	/// </summary>
	[Fact]
	public async Task SizeOnlyCatalogRow_VerifiedDownload_AcquiresComputedHash()
	{
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		byte[] bytes = "size-only-catalog-row-bytes"u8.ToArray();
		string expectedSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		string externalId = "vcf-bundle-" + Guid.NewGuid().ToString("N");
		Guid artifactId = await SeedArtifactAsync(externalId, sha256: null, bytes.Length);

		BinariesDownloadJobHandler handler = CreateHandler(new WritingTool(bytes));
		ClaimedJob job = await EnqueueBinariesDownloadJobAsync(artifactId, externalId);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		DepotArtifact? artifact = await GetArtifactAsync(artifactId);
		Assert.Equal("present", artifact!.Status);
		Assert.Equal(expectedSha256, artifact.Sha256);
	}

	/// <summary>
	/// Issue #1486 review (round 1, finding 1 + 2): a size mismatch must fail, never
	/// report present, and -- the finding-1 fix -- quarantine the corrupt file out of
	/// the depot path it was written to, so the next catalog-index-style disk walk
	/// finds nothing there and the catalog's authenticated hash is never overwritten.
	/// Mutation bar: flipping the failure branch's status literal to "present" fails
	/// this test's first assertion; reverting the quarantine step fails the rest.
	/// </summary>
	[Fact]
	public async Task SizeMismatch_FailsAndQuarantines_CatalogHashSurvivesSubsequentIndexUpsert()
	{
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		byte[] bytes = "wrong-size-bytes"u8.ToArray();
		string authenticatedSha256 = Convert.ToHexString(SHA256.HashData("authenticated-catalog-bytes"u8.ToArray())).ToLowerInvariant();
		string externalId = "vcf-bundle-" + Guid.NewGuid().ToString("N");
		Guid artifactId = await SeedArtifactAsync(externalId, authenticatedSha256, bytes.Length + 1);

		BinariesDownloadJobHandler handler = CreateHandler(new WritingTool(bytes));
		ClaimedJob job = await EnqueueBinariesDownloadJobAsync(artifactId, externalId);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		DepotArtifact? artifact = await GetArtifactAsync(artifactId);
		Assert.Equal("failed", artifact!.Status);
		Assert.Equal(authenticatedSha256, artifact.Sha256);

		// Finding 1: the corrupt file must be gone from the depot path...
		string liveTargetPath = Path.Combine(_depotPath, externalId);
		Assert.False(File.Exists(liveTargetPath), "Failed-verification file must not remain at the live depot path.");

		// ...so a subsequent catalog-index-style walk of that same relative path finds
		// nothing to upsert, and the authenticated hash is untouched by the COALESCE.
		string? indexWalkHash = await IndexWalkHashOrNullAsync(_depotPath, externalId);
		Assert.Null(indexWalkHash);
		await new DepotArtifactRepository(_fixture.ConnectionString).UpsertAsync(
			new DepotArtifactUpsert(externalId, indexWalkHash, "indexed", "{}"), CancellationToken.None);
		DepotArtifact? afterIndexWalk = await GetArtifactAsync(artifactId);
		Assert.Equal(authenticatedSha256, afterIndexWalk!.Sha256);
	}

	/// <summary>
	/// Issue #1486 review (round 1, finding 1 + 2): a SHA-256 mismatch must fail the
	/// same way -- and the mismatched (corrupt) hash must never overwrite the
	/// catalog's authenticated one, proving the handler passes back
	/// <c>artifact.Sha256</c> (never <see cref="BinaryDownloadVerificationResult.Sha256"/>)
	/// on the failure path.
	/// </summary>
	[Fact]
	public async Task HashMismatch_FailsAndQuarantines_CorruptHashNeverOverwritesCatalog()
	{
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		byte[] bytes = "corrupted-download-bytes"u8.ToArray();
		string corruptHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
		string authenticatedSha256 = Convert.ToHexString(SHA256.HashData("authenticated-catalog-bytes"u8.ToArray())).ToLowerInvariant();
		Assert.NotEqual(authenticatedSha256, corruptHash);
		string externalId = "vcf-bundle-" + Guid.NewGuid().ToString("N");
		Guid artifactId = await SeedArtifactAsync(externalId, authenticatedSha256, bytes.Length);

		BinariesDownloadJobHandler handler = CreateHandler(new WritingTool(bytes));
		ClaimedJob job = await EnqueueBinariesDownloadJobAsync(artifactId, externalId);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		DepotArtifact? artifact = await GetArtifactAsync(artifactId);
		Assert.Equal("failed", artifact!.Status);
		Assert.Equal(authenticatedSha256, artifact.Sha256);
		Assert.NotEqual(corruptHash, artifact.Sha256);

		string liveTargetPath = Path.Combine(_depotPath, externalId);
		Assert.False(File.Exists(liveTargetPath), "Failed-verification file must not remain at the live depot path.");
	}

	/// <summary>
	/// Issue #1486 review (round 1, finding 2): the catalog row vanishing between the
	/// tool writing the file and the handler looking it back up must fail the job and
	/// upsert nothing at all -- there is no row left to upsert against.
	/// </summary>
	[Fact]
	public async Task CatalogRowDeletedMidFlight_JobFails_NothingUpserted()
	{
		await SeedActivationCodeCredentialAsync(InventedRealShapeCode);
		byte[] bytes = "vanishing-catalog-row-bytes"u8.ToArray();
		string externalId = "vcf-bundle-" + Guid.NewGuid().ToString("N");
		Guid artifactId = await SeedArtifactAsync(externalId, sha256: null, bytes.Length);

		BinariesDownloadJobHandler handler = CreateHandler(new WritingTool(bytes, sideEffect: () => DeleteArtifactAsync(artifactId)));
		ClaimedJob job = await EnqueueBinariesDownloadJobAsync(artifactId, externalId);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(job), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Null(await GetArtifactAsync(artifactId));
	}
}
