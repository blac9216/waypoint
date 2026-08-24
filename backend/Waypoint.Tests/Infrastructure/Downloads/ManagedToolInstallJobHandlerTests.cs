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
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.Downloads;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Tests.Support;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>tool-install</c> job handler (issue #39): local-repository and upload paths,
/// source-appropriate verification, activation, and append-only ledger recording
/// (installed, rejected, failed), plus the depot-fetch path's disconnected-mode
/// refusal and misconfiguration guard (its full connected-mode/decrypt/HTTP behavior
/// is covered end to end against real Postgres by
/// <c>ManagedToolInstallJobHandlerDepotFetchEndToEndTests</c>, the same split
/// <c>ManagedToolInstallJobHandlerDepotFetchEndToEndTests</c>'s own Postgres-only split needs). No Postgres
/// dependency here -- <see cref="FakeManagedToolInstallRepository"/>,
/// <see cref="FakeManagedToolSignatureVerifier"/>, <see cref="FakeManagedToolDepotFetcher"/>,
/// and <see cref="FakeApplianceStateRepository"/> stand in for the real infrastructure.
/// The concrete <see cref="CredentialRepository"/> dependency is constructed with a
/// syntactically valid but unreachable connection string -- every test in this file
/// exercises only branches that never call it.
/// </summary>
public sealed class ManagedToolInstallJobHandlerTests : IDisposable
{
	private readonly string _repositoryRoot = Directory.CreateTempSubdirectory("waypoint-tool-install-repo-").FullName;
	private readonly string _uploadRoot = Directory.CreateTempSubdirectory("waypoint-tool-install-upload-").FullName;
	private readonly string _toolStateRoot = Directory.CreateTempSubdirectory("waypoint-tool-install-state-").FullName;

	public void Dispose()
	{
		foreach (string directory in new[] { _repositoryRoot, _uploadRoot, _toolStateRoot })
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}

	private sealed class FakeManagedToolSignatureVerifier(bool valid, string? reason = null) : IManagedToolSignatureVerifier
	{
		public List<(string Artifact, string Signature)> Calls { get; } = [];

		public Task<ManagedToolSignatureResult> VerifyAsync(string artifactPath, string signaturePath, CancellationToken cancellationToken)
		{
			Calls.Add((artifactPath, signaturePath));
			return Task.FromResult(valid ? ManagedToolSignatureResult.Ok() : ManagedToolSignatureResult.Fail(reason ?? "invalid signature"));
		}
	}

	private sealed class FakeManagedToolCatalogVerifier(bool valid = true, string? reason = null) : IManagedToolCatalogVerifier
	{
		public Task<ManagedToolCatalogVerificationResult> VerifyAsync(string repositoryRoot, string artifactPath, string? version, CancellationToken cancellationToken) =>
			Task.FromResult(valid ? ManagedToolCatalogVerificationResult.Ok("fake-sha256") : ManagedToolCatalogVerificationResult.Fail(reason ?? "invalid catalog"));
	}

	private sealed class FakeManagedToolInstallRepository : IManagedToolInstallRepository
	{
		public List<ManagedToolInstallAttempt> Recorded { get; } = [];

		private readonly Dictionary<Guid, ManagedToolInstall> _byJobId = [];

		public Task<Guid> RecordAsync(ManagedToolInstallAttempt attempt, CancellationToken cancellationToken)
		{
			Recorded.Add(attempt);
			Guid id = Guid.NewGuid();
			if (attempt.JobId is { } jobId)
			{
				_byJobId[jobId] = new ManagedToolInstall(
					id, attempt.Source, attempt.SourcePath, attempt.Version, attempt.Sha256,
					attempt.Outcome, attempt.RejectedReason, attempt.InitiatedBy, attempt.JobId, DateTimeOffset.UtcNow);
			}

			return Task.FromResult(id);
		}

		public Task<ManagedToolInstall?> FindByJobIdAsync(Guid jobId, CancellationToken cancellationToken) =>
			Task.FromResult(_byJobId.TryGetValue(jobId, out ManagedToolInstall? install) ? install : null);

		public Task<IReadOnlyList<ManagedToolInstall>> ListAsync(int limit, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<ManagedToolInstall>>([]);

		public Task<ManagedToolInstall?> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<ManagedToolInstall?>(null);
	}

	private sealed class FakeEventPublisher : IJobEventPublisher
	{
		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	/// <summary>Never actually invoked by any test in this file -- the disconnected-mode and no-URL-configured guards both return before <see cref="ManagedToolInstallJobHandler"/> would call this.</summary>
	private sealed class FakeManagedToolDepotFetcher : IManagedToolDepotFetcher
	{
		public Task<ManagedToolDepotFetchResult> FetchAsync(string depotToken, string? version, string destinationDirectory, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called by this test file's scenarios.");
	}

	private sealed class FakeApplianceStateRepository(string mode) : IApplianceStateRepository
	{
		public Task<ApplianceState?> GetAsync(CancellationToken cancellationToken) =>
			Task.FromResult<ApplianceState?>(new ApplianceState("1.0.0", null, mode, "idle", null));
	}

	private static JobExecutionContext ContextFor(string payload) => ContextFor(payload, Guid.NewGuid(), attemptCount: 1);

	/// <summary>
	/// Issue #647's requeue simulation: a second call with the SAME <paramref name="jobId"/>
	/// and a higher <paramref name="attemptCount"/> is exactly what a genuine
	/// crash-recovery requeue (or the lease-recovery sweep) produces -- the same
	/// <c>jobs.id</c> row re-claimed, <c>attempt_count</c> incremented, id unchanged.
	/// </summary>
	private static JobExecutionContext ContextFor(string payload, Guid jobId, int attemptCount)
	{
		ClaimedJob job = new(
			Id: jobId, RunId: null, JobType: "tool-install", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: payload, AttemptCount: attemptCount, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private ManagedToolInstallJobHandler CreateHandler(
		FakeManagedToolSignatureVerifier verifier, FakeManagedToolInstallRepository installs, string applianceMode = "disconnected",
		FakeManagedToolCatalogVerifier? catalogVerifier = null)
	{
		ManagedToolOptions options = new()
		{
			LocalRepositoryPath = _repositoryRoot,
			UploadStagingPath = _uploadRoot,
			ToolStatePath = _toolStateRoot,
			ExecutableName = "vcf-download-tool",
			ExecutableRelativePath = "bin/vcf-download-tool",
			LibraryRelativePath = "lib",
			SmokeTestTimeout = TimeSpan.FromSeconds(10),
		};
		return new ManagedToolInstallJobHandler(
			verifier,
			catalogVerifier ?? new FakeManagedToolCatalogVerifier(),
			installs,
			Options.Create(options),
			new FakeManagedToolDepotFetcher(),
			// ICredentialSecretStore is never called by any scenario in this file --
			// the depot-fetch tests here only exercise the pre-decrypt guards
			// (disconnected mode, no configured credential), which is what
			// FakeApplianceStateRepository/the below no-credential path drives.
			new CredentialSecretStore(
				"Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",
				new Waypoint.Infrastructure.Secrets.AesGcmEnvelopeCipher(new Waypoint.Infrastructure.Secrets.FileMasterKeyProvider(Path.Combine(_toolStateRoot, "unused.key"))),
				new Waypoint.Core.Logging.InPlaySecretRedactor(),
				NullLogger<CredentialSecretStore>.Instance),
			new CredentialRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x"),
			new FakeApplianceStateRepository(applianceMode),
			Options.Create(new CatalogOptions()),
			new ManagedToolDistributionInstaller(Options.Create(options)));
	}

	private string ActiveExecutablePath => Path.Combine(_toolStateRoot, "active", "bin", "vcf-download-tool");

	[Fact]
	public void JobType_IsToolInstall()
	{
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), new FakeManagedToolInstallRepository());
		Assert.Equal("tool-install", handler.JobType);
	}

	[Fact]
	public async Task ValidCatalog_LocalRepository_ExtractsAndActivatesTheDistributionAndRecordsInstalled()
	{
		string archivePath = Path.Combine(_repositoryRoot, "vcf-download-tool-1.2.3");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-1.2.3","version":"1.2.3","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.True(File.Exists(ActiveExecutablePath));
		Assert.True(Directory.Exists(Path.Combine(_toolStateRoot, "active", "lib")));

		ManagedToolInstallAttempt recorded = Assert.Single(installs.Recorded);
		Assert.Equal(ManagedToolInstallOutcomes.Installed, recorded.Outcome);
		Assert.Equal(ManagedToolInstallSources.LocalRepository, recorded.Source);
		Assert.Equal("1.2.3", recorded.Version);
		Assert.Equal("tester", recorded.InitiatedBy);
		Assert.NotNull(recorded.Sha256);
	}

	[Fact]
	public async Task BadCatalog_IsRejectedAndRecorded_ArtifactNeverActivated()
	{
		string archivePath = Path.Combine(_repositoryRoot, "vcf-download-tool-bad");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs,
			catalogVerifier: new FakeManagedToolCatalogVerifier(false, "catalog checksum mismatch"));

		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-bad","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("catalog checksum mismatch", outcome.Note, StringComparison.Ordinal);
		Assert.False(File.Exists(ActiveExecutablePath));

		ManagedToolInstallAttempt recorded = Assert.Single(installs.Recorded);
		Assert.Equal(ManagedToolInstallOutcomes.Rejected, recorded.Outcome);
		Assert.Equal("catalog checksum mismatch", recorded.RejectedReason);
	}

	[Fact]
	public async Task ValidPublishedSha256_UploadSource_ExtractsAndActivates()
	{
		string archivePath = Path.Combine(_uploadRoot, "staged-abc");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);
		string expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = $$"""{"source":"upload","source_path":"staged-abc","expected_sha256":"{{expectedSha256}}","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.True(File.Exists(ActiveExecutablePath));
		Assert.Equal(ManagedToolInstallSources.Upload, Assert.Single(installs.Recorded).Source);
	}

	[Fact]
	public async Task WrongUploadChecksum_IsRejectedWithoutActivation()
	{
		string archivePath = Path.Combine(_uploadRoot, "staged-bad");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);
		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);
		string payload = """{"source":"upload","source_path":"staged-bad","expected_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","initiated_by":"tester"}""";

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("SHA-256 mismatch", outcome.Note, StringComparison.Ordinal);
		Assert.False(File.Exists(ActiveExecutablePath));
		Assert.Equal(ManagedToolInstallOutcomes.Rejected, Assert.Single(installs.Recorded).Outcome);
	}

	[Fact]
	public async Task PublishedLegacyMd5_UploadSource_ExtractsAndActivates()
	{
		string archivePath = Path.Combine(_uploadRoot, "staged-md5");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);
#pragma warning disable CA5351
		string expectedMd5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
#pragma warning restore CA5351
		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);
		string payload = $$"""{"source":"upload","source_path":"staged-md5","expected_md5":"{{expectedMd5}}","initiated_by":"tester"}""";

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(ManagedToolInstallOutcomes.Installed, Assert.Single(installs.Recorded).Outcome);
	}

	[Fact]
	public async Task ArchiveAsExecutable_Regression_IsRejectedAndRecorded_NeverActivated()
	{
		string archivePath = Path.Combine(_uploadRoot, "staged-archive-as-exe");
		ManagedToolDistributionFixture.WriteArchiveAsExecutableArchive(archivePath);
		string expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = $$"""{"source":"upload","source_path":"staged-archive-as-exe","expected_sha256":"{{expectedSha256}}","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("SmokeTestFailed", outcome.Note, StringComparison.Ordinal);
		Assert.False(File.Exists(ActiveExecutablePath));
		Assert.Equal(ManagedToolInstallOutcomes.Rejected, Assert.Single(installs.Recorded).Outcome);
	}

	[Fact]
	public async Task PathTraversalInSourcePath_IsRejectedWithoutTouchingTheFilesystem()
	{
		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = """{"source":"local-repository","source_path":"../../etc/passwd","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Empty(installs.Recorded);
	}

	[Fact]
	public async Task UnknownSource_FailsCleanly_NoLedgerRow()
	{
		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = """{"source":"floppy-disk","source_path":"whatever","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("not implemented", outcome.Note, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(installs.Recorded);
	}

	/// <summary>
	/// The depot-fetch path's connected-mode-only guard (issue #39 remainder): a
	/// disconnected appliance refuses before any network attempt and before recording
	/// any ledger row -- the full connected-mode decrypt/HTTP/verify/activate flow is
	/// covered end to end against real Postgres by
	/// <c>ManagedToolInstallJobHandlerDepotFetchEndToEndTests</c>.
	/// </summary>
	[Fact]
	public async Task DepotSource_DisconnectedMode_RefusesCleanlyWithoutAnyLedgerRow()
	{
		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs, applianceMode: "disconnected");

		string payload = """{"source":"depot","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("disconnected", outcome.Note, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(installs.Recorded);
	}

	// The "connected but no depot-activation-code credential configured" and
	// "connected, credential configured, fetch/verify/activate" scenarios both need
	// a real CredentialRepository backed by Postgres (FindByTypeAsync opens a real
	// connection) -- covered end to end by ManagedToolInstallJobHandlerDepotFetchEndToEndTests.

	[Fact]
	public async Task MissingArtifact_FailsWithoutRecordingALedgerRow()
	{
		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = """{"source":"local-repository","source_path":"does-not-exist","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Empty(installs.Recorded);
	}

	[Fact]
	public async Task MalformedPayload_FailsCleanly()
	{
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), new FakeManagedToolInstallRepository());

		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor("not json"), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
	}

	/// <summary>
	/// Issue #647: a genuine crash-recovery requeue re-runs the SAME <c>jobs.id</c> --
	/// the runner died after the first execution had already recorded 'installed' and
	/// activated the distribution, and lease-recovery put the job back on the queue.
	/// The re-run must not write a second ledger row, and (interaction with #715's
	/// installer) must not re-extract/re-smoke-test/re-activate the already-active
	/// distribution.
	/// </summary>
	[Fact]
	public async Task RequeueAfterSuccessfulInstall_DoesNotDuplicateLedgerOrReactivate()
	{
		string archivePath = Path.Combine(_repositoryRoot, "vcf-download-tool-1.2.3");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);
		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-1.2.3","version":"1.2.3","initiated_by":"tester"}""";
		Guid jobId = Guid.NewGuid();

		JobExecutionOutcome first = await handler.ExecuteAsync(ContextFor(payload, jobId, attemptCount: 1), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Succeeded, first.Kind);
		Assert.Single(installs.Recorded);

		DateTime activeWriteTimeAfterFirstRun = File.GetLastWriteTimeUtc(ActiveExecutablePath);

		// Simulate the crash-recovery requeue: same job id, incremented attempt_count,
		// re-claimed and re-executed exactly as the dispatcher would after lease
		// recovery puts the row back on the queue.
		JobExecutionOutcome requeued = await handler.ExecuteAsync(ContextFor(payload, jobId, attemptCount: 2), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, requeued.Kind);
		Assert.Single(installs.Recorded); // still exactly one ledger row -- no duplicate
		Assert.Equal(ManagedToolInstallOutcomes.Installed, installs.Recorded[0].Outcome);

		// The active install was not touched a second time -- no re-extraction/re-activation.
		Assert.Equal(activeWriteTimeAfterFirstRun, File.GetLastWriteTimeUtc(ActiveExecutablePath));
	}

	/// <summary>Issue #647: the same dedup guard applies to a rejected/failed terminal outcome, not just 'installed'.</summary>
	[Fact]
	public async Task RequeueAfterRejectedOutcome_DoesNotDuplicateLedgerRow()
	{
		string archivePath = Path.Combine(_repositoryRoot, "vcf-download-tool-bad");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs,
			catalogVerifier: new FakeManagedToolCatalogVerifier(false, "catalog checksum mismatch"));
		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-bad","initiated_by":"tester"}""";
		Guid jobId = Guid.NewGuid();

		JobExecutionOutcome first = await handler.ExecuteAsync(ContextFor(payload, jobId, attemptCount: 1), CancellationToken.None);
		Assert.Equal(JobOutcomeKind.Failed, first.Kind);
		Assert.Single(installs.Recorded);

		JobExecutionOutcome requeued = await handler.ExecuteAsync(ContextFor(payload, jobId, attemptCount: 2), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, requeued.Kind);
		Assert.Single(installs.Recorded); // still exactly one ledger row -- no duplicate
		Assert.Equal(ManagedToolInstallOutcomes.Rejected, installs.Recorded[0].Outcome);
	}

	/// <summary>A different job id is a genuinely new install attempt, not a requeue -- the dedup guard must not conflate the two.</summary>
	[Fact]
	public async Task DifferentJobId_IsNotTreatedAsARequeue_RecordsItsOwnLedgerRow()
	{
		string archivePath = Path.Combine(_repositoryRoot, "vcf-download-tool-1.2.3");
		ManagedToolDistributionFixture.WriteHappyPathArchive(archivePath);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);
		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-1.2.3","version":"1.2.3","initiated_by":"tester"}""";

		await handler.ExecuteAsync(ContextFor(payload, Guid.NewGuid(), attemptCount: 1), CancellationToken.None);
		await handler.ExecuteAsync(ContextFor(payload, Guid.NewGuid(), attemptCount: 1), CancellationToken.None);

		Assert.Equal(2, installs.Recorded.Count);
	}
}
