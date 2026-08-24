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
using Xunit;

namespace Waypoint.Tests.Infrastructure.Downloads;

/// <summary>
/// The <c>tool-install</c> job handler (issue #39): local-repository and upload paths,
/// signature-gated activation, and append-only ledger recording on every outcome
/// (installed, rejected, failed), plus the depot-fetch path's disconnected-mode
/// refusal and misconfiguration guard (its full connected-mode/decrypt/HTTP behavior
/// is covered end to end against real Postgres by
/// <c>ManagedToolInstallJobHandlerDepotFetchEndToEndTests</c>, the same split
/// <c>CatalogIndexJobHandler</c>'s depot-token path already uses). No Postgres
/// dependency here -- <see cref="FakeManagedToolInstallRepository"/>,
/// <see cref="FakeManagedToolSignatureVerifier"/>, <see cref="FakeManagedToolDepotFetcher"/>,
/// and <see cref="FakeApplianceStateRepository"/> stand in for the real infrastructure.
/// The concrete <see cref="CredentialRepository"/> dependency (mirroring
/// <c>CatalogIndexJobHandler</c>'s own constructor shape) is constructed with a
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

	private sealed class FakeManagedToolInstallRepository : IManagedToolInstallRepository
	{
		public List<ManagedToolInstallAttempt> Recorded { get; } = [];

		public Task<Guid> RecordAsync(ManagedToolInstallAttempt attempt, CancellationToken cancellationToken)
		{
			Recorded.Add(attempt);
			return Task.FromResult(Guid.NewGuid());
		}

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

	private static JobExecutionContext ContextFor(string payload)
	{
		ClaimedJob job = new(
			Id: Guid.NewGuid(), RunId: null, JobType: "tool-install", TargetId: null, TargetName: null,
			CredentialId: null, Priority: 1, Payload: payload, AttemptCount: 1, MaxAttempts: 3);
		return new JobExecutionContext(
			job, "worker-test", new FakeEventPublisher(),
			new JobQueueRepository("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x", NullLogger<JobQueueRepository>.Instance),
			JobShape.Simple);
	}

	private ManagedToolInstallJobHandler CreateHandler(
		FakeManagedToolSignatureVerifier verifier, FakeManagedToolInstallRepository installs, string applianceMode = "disconnected")
	{
		ManagedToolOptions options = new()
		{
			LocalRepositoryPath = _repositoryRoot,
			UploadStagingPath = _uploadRoot,
			ToolStatePath = _toolStateRoot,
			ExecutableName = "vcf-download-tool",
		};
		return new ManagedToolInstallJobHandler(
			verifier,
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
			Options.Create(new CatalogOptions()));
	}

	[Fact]
	public void JobType_IsToolInstall()
	{
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), new FakeManagedToolInstallRepository());
		Assert.Equal("tool-install", handler.JobType);
	}

	[Fact]
	public async Task ValidSignature_LocalRepository_ActivatesTheArtifactAndRecordsInstalled()
	{
		File.WriteAllBytes(Path.Combine(_repositoryRoot, "vcf-download-tool-1.2.3"), [1, 2, 3]);
		File.WriteAllBytes(Path.Combine(_repositoryRoot, "vcf-download-tool-1.2.3.sig"), [9]);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-1.2.3","version":"1.2.3","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.True(File.Exists(Path.Combine(_toolStateRoot, "vcf-download-tool")));
		Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_toolStateRoot, "vcf-download-tool")));

		ManagedToolInstallAttempt recorded = Assert.Single(installs.Recorded);
		Assert.Equal(ManagedToolInstallOutcomes.Installed, recorded.Outcome);
		Assert.Equal(ManagedToolInstallSources.LocalRepository, recorded.Source);
		Assert.Equal("1.2.3", recorded.Version);
		Assert.Equal("tester", recorded.InitiatedBy);
		Assert.NotNull(recorded.Sha256);
	}

	[Fact]
	public async Task BadSignature_IsRejectedAndRecorded_ArtifactNeverActivated()
	{
		File.WriteAllBytes(Path.Combine(_repositoryRoot, "vcf-download-tool-bad"), [1, 2, 3]);
		File.WriteAllBytes(Path.Combine(_repositoryRoot, "vcf-download-tool-bad.sig"), [9]);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(false, "signature mismatch"), installs);

		string payload = """{"source":"local-repository","source_path":"vcf-download-tool-bad","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("signature mismatch", outcome.Note, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(_toolStateRoot, "vcf-download-tool")));

		ManagedToolInstallAttempt recorded = Assert.Single(installs.Recorded);
		Assert.Equal(ManagedToolInstallOutcomes.Rejected, recorded.Outcome);
		Assert.Equal("signature mismatch", recorded.RejectedReason);
	}

	[Fact]
	public async Task ValidSignature_UploadSource_Activates()
	{
		File.WriteAllBytes(Path.Combine(_uploadRoot, "staged-abc"), [4, 5, 6]);
		File.WriteAllBytes(Path.Combine(_uploadRoot, "staged-abc.sig"), [9]);

		FakeManagedToolInstallRepository installs = new();
		ManagedToolInstallJobHandler handler = CreateHandler(new FakeManagedToolSignatureVerifier(true), installs);

		string payload = """{"source":"upload","source_path":"staged-abc","initiated_by":"tester"}""";
		JobExecutionOutcome outcome = await handler.ExecuteAsync(ContextFor(payload), CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(ManagedToolInstallSources.Upload, Assert.Single(installs.Recorded).Source);
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

	// The "connected but no depot-token credential configured" and "connected,
	// credential configured, fetch/verify/activate" scenarios both need a real
	// CredentialRepository backed by Postgres (FindByTypeAsync opens a real
	// connection) -- covered end to end by
	// ManagedToolInstallJobHandlerDepotFetchEndToEndTests, mirroring
	// CatalogIndexJobHandlerEndToEndTests's equivalent split for the same reason.

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
}
