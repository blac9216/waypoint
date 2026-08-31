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

	private async Task<ClaimedJob> EnqueueBinariesDownloadJobAsync()
	{
		Guid runId = await _repository.CreateRunAsync(RunTypes.BinariesDownload, "{}", credentialId: null, "test-actor", CancellationToken.None);
		JobSpec spec = new(RunTypes.BinariesDownload, 1, TargetId: null, TargetName: "bundle-01",
			Payload: $$"""{"depot_artifact_id":"{{Guid.NewGuid()}}","external_id":"vcf-bundle-01"}""");
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(runId, [spec], "test-actor", CancellationToken.None);
		ClaimedJob? claimed = await _repository.ClaimJobAsync(
			"worker-test", TimeSpan.FromMinutes(5), new HashSet<string>(StringComparer.Ordinal) { RunTypes.BinariesDownload }, CancellationToken.None);
		Assert.NotNull(claimed);
		Assert.Equal(jobIds[0], claimed!.Id);
		return claimed;
	}

	private BinariesDownloadJobHandler CreateHandler() =>
		new(
			_enrollment, new UnreachableTool(), _secretStore, _credentials,
			new DepotArtifactRepository(_fixture.ConnectionString), new BinaryDownloadVerifier(),
			Options.Create(new ManagedToolOptions { ToolStatePath = _toolStatePath }),
			Options.Create(new CatalogOptions { DepotPath = _depotPath }));

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
}
