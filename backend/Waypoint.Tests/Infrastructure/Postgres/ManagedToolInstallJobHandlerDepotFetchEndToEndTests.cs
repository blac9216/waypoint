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

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.SystemState;
using Waypoint.Runner.Jobs;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Postgres;

/// <summary>
/// Issue #39 remainder (depot-fetch install path): the REAL loop through real
/// Postgres, mirroring <see cref="CatalogIndexJobHandlerEndToEndTests"/>'s split for
/// the identical reason -- <c>ManagedToolInstallJobHandler</c> takes a concrete
/// <c>CredentialRepository</c> (sealed, Postgres-only), so its depot-token lookup
/// cannot be exercised by a pure in-memory unit test. <see cref="FakeManagedToolDepotFetcher"/>
/// stands in for the real HTTP boundary (covered on its own by
/// <c>HttpManagedToolDepotFetcherTests</c>) so this file's focus stays on the
/// handler's own decisions: mode gate, decrypt-audit attribution, verify-then-activate,
/// and ledger semantics identical to the other two install paths.
/// </summary>
[Collection("Postgres")]
#pragma warning disable CA1001 // xUnit owns the lifecycle: DisposeAsync stops the buffer/pool and removes the key dir.
public sealed class ManagedToolInstallJobHandlerDepotFetchEndToEndTests : IAsyncLifetime, IDisposable
#pragma warning restore CA1001
{
	private const string Token = "invented-depot-e2e-canary-4f2a"; // gitleaks:allow — invented test canary, asserted absent from every persistence surface

	private readonly PostgresFixture _fixture;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-tool-install-depot-key").FullName;
	private readonly string _stagingRoot = Directory.CreateTempSubdirectory("wp-tool-install-depot-staging").FullName;
	private readonly string _toolStateRoot = Directory.CreateTempSubdirectory("wp-tool-install-depot-state").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private ApplianceStateRepository _applianceState = null!;
	private ManagedToolInstallRepository _installs = null!;
	private FakeManagedToolSignatureVerifier _verifier = null!;
	private FakeManagedToolDepotFetcher _fetcher = null!;
	private ManagedToolInstallJobHandler _handler = null!;

	public ManagedToolInstallJobHandlerDepotFetchEndToEndTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await SetModeAsync("connected");

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_applianceState = new ApplianceStateRepository(_fixture.ConnectionString);
		_installs = new ManagedToolInstallRepository(_fixture.ConnectionString);
		_verifier = new FakeManagedToolSignatureVerifier(valid: true);
		_fetcher = new FakeManagedToolDepotFetcher();

		ManagedToolOptions toolOptions = new()
		{
			LocalRepositoryPath = Path.Combine(_stagingRoot, "local-repo"),
			UploadStagingPath = _stagingRoot,
			ToolStatePath = _toolStateRoot,
			ExecutableName = "vcf-download-tool",
			ExecutableRelativePath = "bin/vcf-download-tool",
			LibraryRelativePath = "lib",
			SmokeTestTimeout = TimeSpan.FromSeconds(10),
		};
		CatalogOptions catalogOptions = new() { DepotTokenCredentialType = "depot-token" };

		_handler = new ManagedToolInstallJobHandler(
			_verifier, new FakeManagedToolCatalogVerifier(), _installs, Options.Create(toolOptions), _fetcher, _secretStore, _credentials, _applianceState,
			Options.Create(catalogOptions), new ManagedToolDistributionInstaller(Options.Create(toolOptions)));
	}

	private string ActiveExecutablePath => Path.Combine(_toolStateRoot, "active", "bin", "vcf-download-tool");

	private sealed class FakeManagedToolCatalogVerifier : IManagedToolCatalogVerifier
	{
		public Task<ManagedToolCatalogVerificationResult> VerifyAsync(string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken) =>
			Task.FromResult(ManagedToolCatalogVerificationResult.Ok("fake-sha256"));
	}

	public async Task DisposeAsync()
	{
		await _logBuffer.StopAsync(CancellationToken.None);
	}

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_stagingRoot, recursive: true);
		Directory.Delete(_toolStateRoot, recursive: true);
	}

	private sealed class FakeManagedToolSignatureVerifier(bool valid, string? reason = null) : IManagedToolSignatureVerifier
	{
		public bool Valid { get; set; } = valid;

		public string? Reason { get; set; } = reason;

		public Task<ManagedToolSignatureResult> VerifyAsync(string artifactPath, string signaturePath, CancellationToken cancellationToken) =>
			Task.FromResult(Valid ? ManagedToolSignatureResult.Ok() : ManagedToolSignatureResult.Fail(Reason ?? "invalid signature"));
	}

	/// <summary>Stands in for the real HTTP boundary (see <c>HttpManagedToolDepotFetcherTests</c> for that layer's own coverage) -- lets this file script auth-failure/unreachable/success/timeout results without a real depot.</summary>
	private sealed class FakeManagedToolDepotFetcher : IManagedToolDepotFetcher
	{
		public ManagedToolDepotFetchResult? NextResult { get; set; }

		public string? LastTokenSeen { get; private set; }

		public Task<ManagedToolDepotFetchResult> FetchAsync(string depotToken, string? version, string destinationDirectory, CancellationToken cancellationToken)
		{
			LastTokenSeen = depotToken;
			if (NextResult is not null)
			{
				return Task.FromResult(NextResult);
			}

			Directory.CreateDirectory(destinationDirectory);
			string artifactPath = Path.Combine(destinationDirectory, $"{Guid.NewGuid():N}-artifact");
			string signaturePath = artifactPath + ".sig";
			Waypoint.Tests.Support.ManagedToolDistributionFixture.WriteHappyPathArchive(artifactPath);
			File.WriteAllBytes(signaturePath, [1]);
			return Task.FromResult(ManagedToolDepotFetchResult.Success(artifactPath, signaturePath));
		}
	}

	[Fact]
	public async Task DepotFetch_ConnectedWithValidCredentialAndSignature_ActivatesAndRecordsInstalled_TokenNeverLeaks()
	{
		Guid credentialId = await SeedDepotTokenCredentialAsync(Token);

		Guid jobId = await RunDepotFetchOnceAsync();

		Assert.Equal("done", await GetJobFieldAsync(jobId, "state"));
		Assert.True(File.Exists(ActiveExecutablePath));
		Assert.Equal(Token, _fetcher.LastTokenSeen);

		IReadOnlyList<ManagedToolInstall> ledger = await _installs.ListAsync(10, CancellationToken.None);
		ManagedToolInstall recorded = Assert.Single(ledger);
		Assert.Equal(ManagedToolInstallSources.Depot, recorded.Source);
		Assert.Equal(ManagedToolInstallOutcomes.Installed, recorded.Outcome);

		await AssertTokenNeverLeakedAsync(credentialId);
	}

	[Fact]
	public async Task DepotFetch_BadSignature_RejectedAndRecorded_ArtifactNeverActivated()
	{
		await SeedDepotTokenCredentialAsync(Token);
		_verifier.Valid = false;
		_verifier.Reason = "signature does not match the Broadcom release key";

		Guid jobId = await RunDepotFetchOnceAsync(expectSuccess: false);

		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
		Assert.False(File.Exists(ActiveExecutablePath));

		ManagedToolInstall recorded = Assert.Single(await _installs.ListAsync(10, CancellationToken.None));
		Assert.Equal(ManagedToolInstallOutcomes.Rejected, recorded.Outcome);
		Assert.Equal("signature does not match the Broadcom release key", recorded.RejectedReason);
	}

	[Fact]
	public async Task DepotFetch_AuthFailure_FailsCleanly_NoLedgerRow_TokenNeverInJobNote()
	{
		await SeedDepotTokenCredentialAsync(Token);
		_fetcher.NextResult = ManagedToolDepotFetchResult.Failure(
			ManagedToolDepotFetchFailureKind.AuthFailure, "The depot rejected the depot-token credential (401).");

		Guid jobId = await RunDepotFetchOnceAsync(expectSuccess: false);

		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
		Assert.Empty(await _installs.ListAsync(10, CancellationToken.None));
		string note = await GetJobNoteAsync(jobId);
		Assert.DoesNotContain(Token, note, StringComparison.Ordinal);
		Assert.Contains("rejected the depot-token credential", note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task DepotFetch_Unreachable_FailsCleanly_NoLedgerRow()
	{
		await SeedDepotTokenCredentialAsync(Token);
		_fetcher.NextResult = ManagedToolDepotFetchResult.Failure(
			ManagedToolDepotFetchFailureKind.Unreachable, "The depot-fetch request timed out.");

		Guid jobId = await RunDepotFetchOnceAsync(expectSuccess: false);

		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
		Assert.Empty(await _installs.ListAsync(10, CancellationToken.None));
		Assert.Contains("timed out", await GetJobNoteAsync(jobId), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DepotFetch_TooLarge_FailsCleanly_NoLedgerRow()
	{
		await SeedDepotTokenCredentialAsync(Token);
		_fetcher.NextResult = ManagedToolDepotFetchResult.Failure(
			ManagedToolDepotFetchFailureKind.TooLarge, "The depot response exceeded the 536870912-byte cap and was aborted.");

		Guid jobId = await RunDepotFetchOnceAsync(expectSuccess: false);

		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
		Assert.Empty(await _installs.ListAsync(10, CancellationToken.None));
		Assert.Contains("exceeded", await GetJobNoteAsync(jobId), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task DepotFetch_DisconnectedMode_RefusesCleanly_NoNetworkAttempt_NoLedgerRow()
	{
		await SeedDepotTokenCredentialAsync(Token);
		await SetModeAsync("disconnected");

		Guid jobId = await RunDepotFetchOnceAsync(expectSuccess: false);

		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
		Assert.Contains("disconnected", await GetJobNoteAsync(jobId), StringComparison.OrdinalIgnoreCase);
		Assert.Null(_fetcher.LastTokenSeen); // the fetcher was never even called
		Assert.Empty(await _installs.ListAsync(10, CancellationToken.None));
	}

	[Fact]
	public async Task DepotFetch_NoDepotTokenCredentialConfigured_FailsCleanly_NoLedgerRow()
	{
		Guid jobId = await RunDepotFetchOnceAsync(expectSuccess: false);

		Assert.Equal("failed", await GetJobFieldAsync(jobId, "state"));
		Assert.Contains("depot-token", await GetJobNoteAsync(jobId), StringComparison.Ordinal);
		Assert.Empty(await _installs.ListAsync(10, CancellationToken.None));
	}

	private async Task SetModeAsync(string mode)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new("UPDATE appliance_state SET mode = $1 WHERE id = 1", connection);
		command.Parameters.AddWithValue(mode);
		await command.ExecuteNonQueryAsync();
	}

	private async Task<Guid> SeedDepotTokenCredentialAsync(string secretValue)
	{
		Guid? credentialId = await _credentials.CreateAsync($"depot-token-{Guid.NewGuid():N}", "depot-token", "shared", sudoEnabled: false, CancellationToken.None);
		Assert.NotNull(credentialId);
		await _secretStore.StoreAsync(credentialId!.Value, System.Text.Encoding.UTF8.GetBytes(secretValue), "test", CancellationToken.None);
		return credentialId.Value;
	}

	private JobDispatcherHostedService CreateDispatcher()
	{
		JobEngineOptions options = new() { Enabled = true, PollInterval = TimeSpan.FromMilliseconds(50), MaxConcurrency = 2 };
		return new JobDispatcherHostedService(
			_repository,
			_repository,
			new JobEventPublisher(_fixture.ConnectionString, commandTimeoutSeconds: 5, _redactor, NullLogger<JobEventPublisher>.Instance),
			new JobHandlerRegistry([_handler]),
			Options.Create(options),
			NullLogger<JobDispatcherHostedService>.Instance);
	}

	private async Task<Guid> RunDepotFetchOnceAsync(bool expectSuccess = true)
	{
		Guid runId = await _repository.CreateRunAsync("tool-install", "{}", credentialId: null, "tester", CancellationToken.None);
		string payload = """{"source":"depot","initiated_by":"tester"}""";
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("tool-install", 1, Payload: payload)], "tester", CancellationToken.None);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilTerminalAsync(jobIds[0]);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		if (expectSuccess)
		{
			Assert.Equal("done", await GetJobFieldAsync(jobIds[0], "state"));
		}

		return jobIds[0];
	}

	/// <summary>security.md control 1/4: the token never reaches job_events/jobs.note, and the #8 decrypt audit row carries this job's attribution -- proven the same way <c>CatalogIndexJobHandlerEndToEndTests</c> proves it for the catalog-index path.</summary>
	private async Task AssertTokenNeverLeakedAsync(Guid credentialId)
	{
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();

		await using (NpgsqlCommand leaked = new("SELECT count(*) FROM job_events WHERE payload::text LIKE '%' || $1 || '%'", connection))
		{
			leaked.Parameters.AddWithValue(Token);
			Assert.Equal(0L, (long)(await leaked.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand notes = new("SELECT count(*) FROM jobs WHERE note LIKE '%' || $1 || '%'", connection))
		{
			notes.Parameters.AddWithValue(Token);
			Assert.Equal(0L, (long)(await notes.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand ledger = new("SELECT count(*) FROM managed_tool_installs WHERE source_path LIKE '%' || $1 || '%' OR COALESCE(rejected_reason, '') LIKE '%' || $1 || '%'", connection))
		{
			ledger.Parameters.AddWithValue(Token);
			Assert.Equal(0L, (long)(await ledger.ExecuteScalarAsync())!);
		}

		await using (NpgsqlCommand audited = new(
			"SELECT count(*) FROM audit_log WHERE event_type = 'secret.decrypted' AND credential_id = $1 AND job_id IS NOT NULL", connection))
		{
			audited.Parameters.AddWithValue(credentialId);
			Assert.True((long)(await audited.ExecuteScalarAsync())! >= 1);
		}
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT {field}::text FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task<string> GetJobNoteAsync(Guid jobId)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new("SELECT COALESCE(note, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	private async Task PollUntilTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "done" or "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s.");
	}
}
