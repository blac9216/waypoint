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
using System.Management.Automation;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Jobs;
using Waypoint.Core.Logging;
using Waypoint.Core.PowerShell;
using Waypoint.Core.Scans;
using Waypoint.Core.Secrets;
using Waypoint.Core.Sites;
using Waypoint.Core.StigManager;
using Waypoint.Infrastructure.ComplianceContent;
using Waypoint.Infrastructure.ConfigDocs;
using Waypoint.Infrastructure.Data;
using Waypoint.Infrastructure.Jobs;
using Waypoint.Infrastructure.Scans;
using Waypoint.Infrastructure.Secrets;
using Waypoint.Infrastructure.Sites;
using Waypoint.Infrastructure.StigManager;
using Waypoint.Runner.Jobs;
using Waypoint.Tests.Infrastructure.Postgres;
using Xunit;

namespace Waypoint.Tests.Parity;

/// <summary>
/// Issue #749 EXECUTION-PARITY slice (epic #726): parameterized command-construction
/// contract tests over <see cref="ExecutionDerivationMatrix.Rows"/>, driving the REAL
/// <see cref="ScanJobHandler"/> through the real <see cref="JobDispatcherHostedService"/>
/// against a real Postgres-backed catalog/baseline/target/credential graph (the same
/// seeding shape <c>ScanJobHandlerEndToEndTests</c> established), but substituting
/// <see cref="FakePowerShellExecutor"/> for the real PowerShell runspace pool -- the same
/// in-memory fake-executor idiom <c>ContentPullJobHandlerTests.FakePowerShellExecutor</c>
/// established for unit-level command-construction assertions, applied here so the
/// resolved profile-path/credential/input-file plumbing stays real while the actual
/// `inspec`/PowerShell-module boundary is captured rather than executed. This proves
/// exactly what command WOULD be issued -- command name, parameter dictionary, and
/// materialized input-file content -- without a real InSpec binary or a real vCenter/NSX
/// Manager/ssh host.
///
/// <b>Honest boundary:</b> live wrapper execution against the real shipped PowerShell
/// modules remains <c>ScanJobHandlerEndToEndTests</c>' own scope (stub-module
/// Write-Information echoing against the real runspace), plus the owner-run live-lab
/// acceptance pass documented in docs/testing.md; this suite does not replace either --
/// it adds a THIRD, complementary layer that asserts the captured command/parameters
/// directly rather than through a log-line substring.
///
/// All product-version keys, component keys, and vendor/host identifiers below are
/// INVENTED for this test suite -- shaped like docs/compliance-parity.md's rows, never
/// exported from any real system or the sibling repository (CLAUDE.md sanitization
/// policy).
/// </summary>
[Collection("Postgres")]
public sealed class ExecutionParityContractTests : IAsyncLifetime, IDisposable
{
	private readonly PostgresFixture _fixture;
	private readonly string _artifactDirectory = Directory.CreateTempSubdirectory("wp-exec-parity-artifacts").FullName;
	private readonly string _contentDirectory = Directory.CreateTempSubdirectory("wp-exec-parity-content").FullName;
	private readonly string _keyDirectory = Directory.CreateTempSubdirectory("wp-exec-parity-key").FullName;
	private readonly InPlaySecretRedactor _redactor = new();

	private JobQueueRepository _repository = null!;
	private BufferedJobEventWriter _logBuffer = null!;
	private CredentialRepository _credentials = null!;
	private CredentialSecretStore _secretStore = null!;
	private SiteRepository _sites = null!;
	private TargetRepository _targets = null!;
	private RunSecretStore _runSecrets = null!;
	private ConfigDocRepository _configDocs = null!;
	private AttestationSnapshotRepository _attestationSnapshots = null!;
	private CatalogRepository _catalog = null!;
	private BaselineRepository _baselines = null!;
	private BenchmarkRepository _benchmarks = null!;
	private FakePowerShellExecutor _executor = null!;
	private ScanJobHandler _handler = null!;

	/// <summary>
	/// Issue #749: <see cref="ScanJobHandler"/> deletes its generated/materialized
	/// InputsFilePath in a `finally` immediately after the scan invocation returns --
	/// long before the job reaches a terminal state this suite polls for -- so content
	/// assertions on that file must be captured AT invocation time (inside the fake
	/// executor, the only moment the file is guaranteed to still exist), not read back
	/// afterward.
	/// </summary>
	private string? _capturedInputsFileContent;

	public ExecutionParityContractTests(PostgresFixture fixture)
	{
		_fixture = fixture;
	}

	public async Task InitializeAsync()
	{
		NpgsqlSchemaMigrator migrator = new(_fixture.ConnectionString, NullLogger<NpgsqlSchemaMigrator>.Instance);
		await migrator.ApplyAsync();
		await _fixture.ResetJobEngineDataAsync();
		await ResetScanDataAsync();

		_repository = new JobQueueRepository(_fixture.ConnectionString, NullLogger<JobQueueRepository>.Instance);

		JobEngineOptions engineOptions = new() { EventFlushInterval = TimeSpan.FromMilliseconds(50) };
		_logBuffer = new BufferedJobEventWriter(
			_fixture.ConnectionString, _redactor, Options.Create(engineOptions), NullLogger<BufferedJobEventWriter>.Instance);
		await _logBuffer.StartAsync(CancellationToken.None);

		_executor = new FakePowerShellExecutor();

		string keyPath = Path.Combine(_keyDirectory, "master.key");
		File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
		FileMasterKeyProvider keyProvider = new(keyPath);
		AesGcmEnvelopeCipher cipher = new(keyProvider);

		_credentials = new CredentialRepository(_fixture.ConnectionString);
		_secretStore = new CredentialSecretStore(_fixture.ConnectionString, cipher, _redactor, NullLogger<CredentialSecretStore>.Instance);
		_sites = new SiteRepository(_fixture.ConnectionString);
		_targets = new TargetRepository(_fixture.ConnectionString);
		_runSecrets = new RunSecretStore(_fixture.ConnectionString, cipher, _redactor, Options.Create(new RunSecretOptions()), NullLogger<RunSecretStore>.Instance);
		_configDocs = new ConfigDocRepository(_fixture.ConnectionString);
		_attestationSnapshots = new AttestationSnapshotRepository(_fixture.ConnectionString);

		PowerShellOptions powerShellOptions = new();
		IOptions<PowerShellOptions> wrappedPsOptions = Options.Create(powerShellOptions);

		ScanOptions scanOptionsValue = new()
		{
			ArtifactStorePath = _artifactDirectory,
			ProfilePath = "/invented/profile/path",
			TimeoutSeconds = 60,
			AttestationProfile = "invented-execution-parity-stig",
			SafTimeoutSeconds = 30,
		};
		IOptions<ScanOptions> scanOptions = Options.Create(scanOptionsValue);
		IOptions<ComplianceContentOptions> complianceContentOptions =
			Options.Create(new ComplianceContentOptions { ContentPath = _contentDirectory });

		StigManagerRepository stigman = new(_fixture.ConnectionString);
		ScanUploadCoordinator uploadCoordinator = new(
			stigman, new NeverCalledStigManagerUploadClient(), _secretStore, _repository, _redactor);

		_catalog = new CatalogRepository(_fixture.ConnectionString);
		_baselines = new BaselineRepository(_fixture.ConnectionString);
		ComponentProfileRevisionResolver componentProfileRevisions = new(_baselines, _catalog, complianceContentOptions);
		_benchmarks = new BenchmarkRepository(_fixture.ConnectionString);

		_handler = new ScanJobHandler(
			_executor, _secretStore, _credentials, _targets, _runSecrets, _repository, _redactor, wrappedPsOptions, scanOptions,
			complianceContentOptions, _configDocs, _attestationSnapshots, uploadCoordinator, componentProfileRevisions, _benchmarks);
	}

	public Task DisposeAsync() => _logBuffer.StopAsync(CancellationToken.None);

	public void Dispose()
	{
		Directory.Delete(_keyDirectory, recursive: true);
		Directory.Delete(_artifactDirectory, recursive: true);
		Directory.Delete(_contentDirectory, recursive: true);
	}

	private async Task ResetScanDataAsync()
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand command = new(
			"""
			TRUNCATE TABLE
				catalog_import_report_entries, catalog_import_reports, catalog_declared_inputs,
				catalog_remediation_definitions, catalog_benchmark_references, catalog_credential_requirements,
				catalog_execution_profiles, catalog_report_groups, catalog_components, catalog_content_releases,
				catalog_product_versions, catalog_products, catalog_source_revisions,
				benchmark_component_mappings, benchmark_rules, benchmark_revisions,
				config_versions, config_docs, targets, sites
			RESTART IDENTITY CASCADE
			""", connection);
		await command.ExecuteNonQueryAsync();
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

	// --- fakes -----------------------------------------------------------------

	/// <summary>
	/// The same "in-memory fake executor" idiom as
	/// <c>ContentPullJobHandlerTests.FakePowerShellExecutor</c>: captures the LAST
	/// <see cref="PowerShellRequest"/> the handler issued (command name + bound
	/// parameters) and returns a caller-supplied canned outcome, so a test can assert
	/// the exact invocation shape without a real PowerShell runspace or InSpec binary.
	/// </summary>
	private sealed class FakePowerShellExecutor : IPowerShellExecutor
	{
		private static readonly HashSet<string> ScanCommandNames = ["Invoke-WaypointScan", "Invoke-WaypointNsxScan", "Invoke-WaypointSrgScan"];

		public PowerShellRequest? LastRequest { get; private set; }

		/// <summary>
		/// The SCAN-stage invocation specifically (Invoke-WaypointScan/NsxScan/SrgScan),
		/// captured separately from <see cref="LastRequest"/> because a successful scan
		/// always triggers a FOLLOW-ON attest (and, for hdf_ckl output, convert)
		/// invocation on the SAME fake -- so by the time the job reaches a terminal
		/// state, <see cref="LastRequest"/> is whichever stage ran last, not the
		/// command-construction shape this suite's theories actually assert on.
		/// </summary>
		public PowerShellRequest? LastScanRequest { get; private set; }

		/// <summary>
		/// Overrides the SCAN command's own canned result (module Success/ExitCode/
		/// FailureReason) for exit-code-semantics assertions. Attest/convert always
		/// succeed unconditionally -- this suite's exit-code theory is about the SCAN
		/// stage's interpretation specifically, not the whole pipeline's.
		/// </summary>
		public PowerShellExecutionResult? ScanResultOverride { get; set; }

		/// <summary>
		/// Set by the enclosing test class before dispatch when it needs the
		/// InputsFilePath's on-disk CONTENT captured at invocation time -- see
		/// <see cref="ExecutionParityContractTests._capturedInputsFileContent"/>'s own
		/// doc comment for why this cannot be read back after the job goes terminal.
		/// </summary>
		public Action<string>? OnInputsFileContentCaptured { get; set; }

		public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;

			if (ScanCommandNames.Contains(request.Command))
			{
				LastScanRequest = request;
				if (request.Parameters?.GetValueOrDefault("InputsFilePath") is string inputsFilePath && File.Exists(inputsFilePath))
				{
					OnInputsFileContentCaptured?.Invoke(File.ReadAllText(inputsFilePath));
				}

				if (ScanResultOverride is not null)
				{
					return Task.FromResult(ScanResultOverride);
				}

				// Issue #749: ScanJobHandler's scan-stage branch requires
				// File.Exists(output.ReportPath) before it will advance past the scan
				// stage (never trusting a module's Success claim alone) -- so the default
				// canned success result must actually materialize a report file at the
				// path the handler itself passed in ReportPath, exactly as the real
				// Invoke-WaypointScan/Invoke-WaypointNsxScan/Invoke-WaypointSrgScan do.
				string reportPath = (string)request.Parameters!["ReportPath"]!;
				Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
				File.WriteAllText(reportPath, """{"platform":{"name":"invented"},"profiles":[]}""");
				return Task.FromResult(SuccessResult(reportPath: reportPath));
			}

			if (request.Command == "Invoke-WaypointAttest")
			{
				PSObject attestOutput = new();
				attestOutput.Properties.Add(new PSNoteProperty("Success", true));
				attestOutput.Properties.Add(new PSNoteProperty("AttestApplied", false));
				attestOutput.Properties.Add(new PSNoteProperty("FailureReason", null));
				return Task.FromResult(new PowerShellExecutionResult(true, [attestOutput], false, false, null, 0));
			}

			if (request.Command == "Invoke-WaypointConvert")
			{
				// Issue #749: ScanJobHandler's convert-stage branch requires
				// File.Exists(output.CklPath) the same way the scan stage requires
				// File.Exists(output.ReportPath) -- materialize a real CKL file at the
				// handler's own CklOutputPath parameter.
				string cklPath = (string)request.Parameters!["CklOutputPath"]!;
				Directory.CreateDirectory(Path.GetDirectoryName(cklPath)!);
				File.WriteAllText(cklPath, "invented-parity-fixture-ckl-body");
				PSObject convertOutput = new();
				convertOutput.Properties.Add(new PSNoteProperty("Success", true));
				convertOutput.Properties.Add(new PSNoteProperty("CklPath", cklPath));
				convertOutput.Properties.Add(new PSNoteProperty("MetadataApplied", true));
				convertOutput.Properties.Add(new PSNoteProperty("FailureReason", null));
				return Task.FromResult(new PowerShellExecutionResult(true, [convertOutput], false, false, null, 0));
			}

			throw new InvalidOperationException($"FakePowerShellExecutor: unexpected command '{request.Command}' -- this suite's fixtures only exercise the scan/attest/convert stages.");
		}

		public static PowerShellExecutionResult SuccessResult(int nativeExitCode = 0, string? reportPath = null) =>
			new(
				Succeeded: true,
				Output: [BuildModuleOutput(success: true, exitCode: nativeExitCode, reportPath: reportPath, failureReason: null)],
				HadErrors: false,
				TimedOut: false,
				FailureReason: null,
				NativeExitCode: nativeExitCode);

		/// <summary>
		/// Builds the same shape <c>ScanJobHandler.TryParseOutput</c> actually parses --
		/// a real <see cref="PSObject"/> with note properties, matching
		/// Invoke-WaypointScan/Invoke-WaypointNsxScan/Invoke-WaypointSrgScan's real
		/// <c>[pscustomobject]@{...}</c> return shape (a plain Dictionary would silently
		/// fail TryParseOutput's `is not PSObject` guard and return null, masking every
		/// assertion downstream of it).
		/// </summary>
		public static PSObject BuildModuleOutput(bool success, int? exitCode, string? reportPath, string? failureReason)
		{
			PSObject result = new();
			result.Properties.Add(new PSNoteProperty("Success", success));
			result.Properties.Add(new PSNoteProperty("ExitCode", exitCode));
			result.Properties.Add(new PSNoteProperty("ReportPath", reportPath));
			result.Properties.Add(new PSNoteProperty("FailureReason", failureReason));
			return result;
		}
	}

	/// <summary>
	/// Never-called stub -- no <c>stigman_connections</c> row exists in this suite, so
	/// <see cref="StigManagerRepository.ResolveForSiteAsync"/> always returns null and
	/// <see cref="ScanUploadCoordinator"/> never reaches the network boundary. Present
	/// only so <see cref="ScanJobHandler"/> can be constructed with the same DI shape
	/// production uses.
	/// </summary>
	private sealed class NeverCalledStigManagerUploadClient : IStigManagerUploadClient
	{
		public Task<StigManagerUploadResult> UploadCklAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string cklPath, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called: no STIG Manager connection is configured in this test suite.");

		public Task<StigManagerBenchmarkMetadata> ResolveBenchmarkMetadataAsync(
			ResolvedStigManagerConnection connection, string? clientSecret, string benchmarkId, StigManagerBenchmarkMetadata fallback, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Not expected to be called: no STIG Manager connection is configured in this test suite.");
	}

	// --- theory ------------------------------------------------------------------

	public static IEnumerable<object[]> MatrixRows() =>
		ExecutionDerivationMatrix.Rows.Select(row => new object[] { row });

	/// <summary>
	/// Deliverable 1: per documented family row, the claimed component job's INVOCATION
	/// matches the doc -- transport module invoked (command name), selector narrowing
	/// keys (or none for a whole-object/whole-appliance selector), the resolved
	/// activated-revision profile path shape, and the credential purpose consumed.
	/// </summary>
	[Theory]
	[MemberData(nameof(MatrixRows))]
	public async Task ScanJobHandler_FamilyRow_InvokesDocumentedCommandWithDocumentedShape(ExecutionParityRow row)
	{
		(Guid targetId, Guid credentialId, Guid? boundCredentialId, string boundPurpose) = await SeedTargetForRowAsync(row);
		(Guid executionProfileId, Guid baselineId, string profileKey) = await SeedCatalogAndBaselineForRowAsync(row);

		string payload = BuildPayload(row, targetId, executionProfileId, baselineId);

		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		JobCredentialBindingSpec[] bindings = boundCredentialId is null
			? []
			: [new JobCredentialBindingSpec(boundPurpose, boundCredentialId.Value)];
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId,
			[new JobSpec("scan", 1, TargetId: targetId, CredentialId: credentialId, Payload: payload, CredentialBindings: bindings)],
			"tester", CancellationToken.None);
		Guid jobId = jobIds[0];

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilRequestCapturedOrTerminalAsync(jobId);

			// The handler must have reached the scan invocation step at all -- a null
			// LastScanRequest means the row's seeded fixture never got far enough to
			// construct a command, which would make every assertion below vacuously pass.
			Assert.NotNull(_executor.LastScanRequest);
			PowerShellRequest request = _executor.LastScanRequest!;

			Assert.Equal(row.ExpectedCommand, request.Command);
			Assert.Equal(PowerShellRequestKind.Command, request.Kind);
			Assert.NotNull(request.Parameters);

			IReadOnlyDictionary<string, object?> parameters = request.Parameters!;
			foreach (string expectedKey in row.ExpectedParameterKeys)
			{
				Assert.True(parameters.ContainsKey(expectedKey), $"row '{row.MatrixRowId}': expected parameter key '{expectedKey}' on {request.Command}, got [{string.Join(", ", parameters.Keys)}].");
			}

			if (row.CarriesSelectorName)
			{
				Assert.Equal(row.SelectorName, parameters["SelectorName"]);
			}
			else
			{
				Assert.False(parameters.ContainsKey("SelectorName"), $"row '{row.MatrixRowId}': whole-object/whole-appliance selector must never carry SelectorName on the invocation itself.");
			}

			// Activated-revision profile path shape: never the legacy fixed ScanOptions
			// path, always the resolved {digest}/{profileKey} composition.
			string profilePath = Assert.IsType<string>(parameters["ProfilePath"]);
			Assert.Contains(profileKey, profilePath, StringComparison.Ordinal);
			Assert.DoesNotContain("/invented/profile/path", profilePath, StringComparison.Ordinal);

			// Output kind determines the terminal state (docs/compliance-parity.md's own
			// Output column / ADR-0022): hdf_ckl (STIG) completes the FULL pipeline
			// through STIG Manager upload attribution ('uploaded'); hdf (SRG) terminates
			// at 'done' right after attest, never reaching convert/CKL/upload (issue
			// #741/#743's "catalog kind, not target kind, determines HDF-only versus CKL
			// pipeline").
			string expectedTerminalState = row.OutputKind == CatalogOutputKinds.HdfAndCkl ? "uploaded" : "done";
			string finalState = await PollUntilOneOfAsync(jobId, expectedTerminalState, "failed", "auth-failed", "cancelled");
			Assert.Equal(expectedTerminalState, finalState);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// Deliverable 1 (input-file channel + reserved-key discipline, issue #911/#742): a
	/// narrowed vmware component item with an operator-authored resolved Input config
	/// doc materializes that content as its OWN <c>InputsFilePath</c> parameter -- a
	/// channel separate from the platform's own <c>SelectorKind</c>/<c>SelectorName</c>
	/// parameters (which <c>WaypointScan.psm1</c> turns into its own generated
	/// <c>--input-file</c>, appended AFTER <c>InputsFilePath</c> per issue #911's fix,
	/// proven at the module level by <c>ScanJobHandlerEndToEndTests</c>'s real-stub-module
	/// tests). An operator body that also NAMES a reserved platform-scoping key
	/// (<see cref="ScanScopingInputFilter.ReservedScopingKeys"/>) is dropped from the
	/// materialized file before invocation -- proven directly against the captured file's
	/// on-disk content, independent of <c>ScanJobHandlerEndToEndTests</c>' own log-line
	/// assertion of the same behavior.
	/// </summary>
	[Fact]
	public async Task NarrowedVSphereComponentJob_OperatorInputsFilePath_IsSeparateChannel_AndDropsReservedKeys()
	{
		ExecutionParityRow row = ExecutionDerivationMatrix.Rows.Single(r => r.MatrixRowId == "vsphere-8-0-stig-vmware-esxi");
		(Guid targetId, Guid credentialId, _, _) = await SeedTargetForRowAsync(row);
		(Guid executionProfileId, Guid baselineId, string _) = await SeedCatalogAndBaselineForRowAsync(row);

		// An operator-authored resolved Input config doc that ALSO tries to name a
		// reserved platform-scoping key (vmhostName) alongside a legitimate one.
		(Guid docId, int docVersion) = await CreateResolvedInputDocAsync(
			executionProfileId, "vmhostName: 'attacker-widened-host'\ninvented_operator_input: 'kept'\n");

		_capturedInputsFileContent = null;
		_executor.OnInputsFileContentCaptured = content => _capturedInputsFileContent = content;

		string payload = JsonSerializer.Serialize(new
		{
			target_id = targetId,
			transport = row.Transport,
			selector_kind = row.SelectorKind,
			selector_name = row.SelectorName,
			catalog_execution_profile_id = executionProfileId,
			baseline_id = baselineId,
			output_kind = row.OutputKind,
			input_resolutions = new[]
			{
				new { InputName = "invented_operator_input", State = "resolved", DocId = docId, DocVersion = docVersion },
			},
		});
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 1, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);
		Guid jobId = jobIds[0];

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			await PollUntilRequestCapturedOrTerminalAsync(jobId);
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}

		Assert.NotNull(_executor.LastScanRequest);
		IReadOnlyDictionary<string, object?> parameters = _executor.LastScanRequest!.Parameters!;

		Assert.True(parameters.ContainsKey("InputsFilePath"), "expected the operator config-doc inputs to materialize as their own InputsFilePath parameter.");
		string inputsFilePath = Assert.IsType<string>(parameters["InputsFilePath"]);
		Assert.NotNull(_capturedInputsFileContent);
		string inputsContent = _capturedInputsFileContent!;

		Assert.DoesNotContain("attacker-widened-host", inputsContent, StringComparison.Ordinal);
		Assert.DoesNotContain("vmhostName", inputsContent, StringComparison.Ordinal);
		Assert.Contains("invented_operator_input", inputsContent, StringComparison.Ordinal);

		// The platform's own scoping rides SEPARATE parameters, never merged into the
		// operator's InputsFilePath content.
		Assert.Equal(CatalogSelectorKinds.Esxi, parameters["SelectorKind"]);
		Assert.Equal("esxi-01", parameters["SelectorName"]);
	}

	/// <summary>
	/// Deliverable 1 (exit-code semantics): the module-reported <c>ExitCode</c> of 100
	/// (InSpec "compliance failures present") is interpreted as a completed, reportable
	/// scan -- reaching the job's terminal success state, never a failure -- while a
	/// module-reported failure (<c>Success = false</c>, a non-auth reason) reaches
	/// <c>failed</c>. Both driven directly through the injected fake-executor result,
	/// independent of <c>ScanJobHandlerEndToEndTests</c>' own real-stub-module exit-code
	/// tests.
	/// </summary>
	[Theory]
	[InlineData(0, true, null, "uploaded")]
	[InlineData(100, true, null, "uploaded")]
	[InlineData(1, false, "invented non-auth transport failure (parity fixture).", "failed")]
	public async Task ScanJobHandler_ExitCodeSemantics_MapToDocumentedTerminalState(int exitCode, bool moduleSuccess, string? failureReason, string expectedState)
	{
		ExecutionParityRow row = ExecutionDerivationMatrix.Rows.Single(r => r.MatrixRowId == "vsphere-8-0-stig-vmware-vcenter");
		(Guid targetId, Guid credentialId, _, _) = await SeedTargetForRowAsync(row);
		(Guid executionProfileId, Guid baselineId, string _) = await SeedCatalogAndBaselineForRowAsync(row);

		string payload = BuildPayload(row, targetId, executionProfileId, baselineId);
		Guid runId = await _repository.CreateRunAsync("scan", "{}", credentialId: null, "tester", CancellationToken.None);
		IReadOnlyList<Guid> jobIds = await _repository.FanOutJobsAsync(
			runId, [new JobSpec("scan", 1, TargetId: targetId, CredentialId: credentialId, Payload: payload)], "tester", CancellationToken.None);
		Guid jobId = jobIds[0];

		// The attest stage re-reads the HDF from ScanJobHandler's own DETERMINISTIC path
		// ({ArtifactStorePath}/{jobId:N}.json), never from the module's self-reported
		// ReportPath value -- so a real report must exist at exactly that path,
		// computed the same way the handler itself does, for the pipeline to progress
		// past the scan stage regardless of what path this test's canned ReportPath
		// claims.
		string reportPath = Path.Combine(_artifactDirectory, $"{jobId:N}.json");
		if (moduleSuccess)
		{
			await File.WriteAllTextAsync(reportPath, """{"platform":{"name":"invented"},"profiles":[]}""");
		}

		_executor.ScanResultOverride = new PowerShellExecutionResult(
			Succeeded: true,
			Output: [FakePowerShellExecutor.BuildModuleOutput(moduleSuccess, moduleSuccess ? exitCode : null, moduleSuccess ? reportPath : null, failureReason)],
			HadErrors: false,
			TimedOut: false,
			FailureReason: null,
			NativeExitCode: exitCode);

		JobDispatcherHostedService dispatcher = CreateDispatcher();
		await dispatcher.StartAsync(CancellationToken.None);
		try
		{
			// The fake's default (unconditional-success) attest/convert handling answers
			// the follow-on invocations for a successful scan, so this row's documented
			// hdf_ckl terminal state ('uploaded') is genuinely reached end to end, not
			// merely "some non-failed state" -- proving the exit-code interpretation
			// through the whole pipeline, not just at the scan stage boundary.
			string finalState = await PollUntilOneOfAsync(jobId, "uploaded", "failed", "auth-failed", "cancelled");
			string note = await GetJobFieldAsync(jobId, "note");
			Assert.True(expectedState == finalState, $"expected state '{expectedState}', got '{finalState}' (note: {note})");
		}
		finally
		{
			await dispatcher.StopAsync(CancellationToken.None);
		}
	}

	// --- mutation guards -----------------------------------------------------------

	/// <summary>
	/// Independent MutationGuard (issue #749 deliverable 4, transport routing): proves
	/// the matrix's own command-name claim is load-bearing -- the NSX row's expected
	/// command is the transport-specific <c>Invoke-WaypointNsxScan</c>, never the vmware
	/// or ssh commands a routing regression could silently fall back to. Mirrors
	/// PlannerParityContractTests' own
	/// <c>MutationGuard_NsxRowTransport_IsNsxApi_NeverVMwareOrSsh</c> idiom one layer
	/// over (command name, not transport string) -- reverting this fact's own assertion
	/// (e.g. asserting <c>Invoke-WaypointScan</c> instead) fails immediately, and
	/// reverting the matrix ROW's <c>ExpectedCommand</c> value independently fails this
	/// same fact, giving two independent tripwires over the one claim.
	/// </summary>
	[Fact]
	public void MutationGuard_NsxRowExpectedCommand_IsInvokeWaypointNsxScan_NeverVmwareOrSshCommand()
	{
		ExecutionParityRow nsxRow = ExecutionDerivationMatrix.Rows.Single(r => r.MatrixRowId == "nsx-4-x-stig-service");
		Assert.Equal("Invoke-WaypointNsxScan", nsxRow.ExpectedCommand);
		Assert.NotEqual("Invoke-WaypointScan", nsxRow.ExpectedCommand);
		Assert.NotEqual("Invoke-WaypointSrgScan", nsxRow.ExpectedCommand);
	}

	/// <summary>
	/// Independent MutationGuard (input-file/parameter-shape drift): proves the matrix's
	/// ssh/service row does NOT declare a vmware-only parameter (<c>SelectorKind</c>/
	/// <c>VCenter</c>) -- an accidental copy-paste of the vmware row's expected
	/// parameter set onto the ssh row would claim a parameter
	/// <c>Invoke-WaypointSrgScan</c> does not accept, which this fact catches
	/// independent of the theory actually running the handler.
	/// </summary>
	[Fact]
	public void MutationGuard_SshServiceRow_NeverClaimsVSphereOnlyParameters()
	{
		ExecutionParityRow sshRow = ExecutionDerivationMatrix.Rows.Single(r => r.MatrixRowId == "vsphere-8-0-stig-vcsa-ssh-service");
		Assert.DoesNotContain("SelectorKind", sshRow.ExpectedParameterKeys);
		Assert.DoesNotContain("VCenter", sshRow.ExpectedParameterKeys);
		Assert.Contains("Sudo", sshRow.ExpectedParameterKeys);
	}

	/// <summary>
	/// Independent MutationGuard (output kind): proves the matrix's own OutputKind
	/// claims match docs/compliance-parity.md's Output column verbatim for the STIG vs.
	/// SRG rows this slice covers -- an accidental swap (e.g. giving the vidm SRG row
	/// <c>hdf_ckl</c>) would be caught here without running the Postgres-backed theory.
	/// </summary>
	[Fact]
	public void MutationGuard_OutputKinds_MatchDocumentedStigVsSrgSplit()
	{
		Assert.All(
			ExecutionDerivationMatrix.Rows.Where(r => r.MatrixRowId.Contains("stig", StringComparison.Ordinal)),
			r => Assert.Equal(CatalogOutputKinds.HdfAndCkl, r.OutputKind));
		Assert.All(
			ExecutionDerivationMatrix.Rows.Where(r => r.MatrixRowId.Contains("srg", StringComparison.Ordinal)),
			r => Assert.Equal(CatalogOutputKinds.Hdf, r.OutputKind));
	}

	/// <summary>
	/// Independent MutationGuard (exit-code mapping): pins the predecessor exit-code
	/// contract (issue #274 AC, carried into every transport module's own
	/// <c>Invoke-ExternalCommand -AllowedExitCodes @(0, 100, 101)</c> call) as an
	/// EXPLICIT, hand-authored set, independent of the theory that exercises
	/// <see cref="ScanJobHandler"/> end to end. A reviewer diffing THIS set against
	/// WaypointScan.psm1's own <c>AllowedExitCodes</c> literals (grep the module for
	/// <c>@(0, 100, 101)</c>) is the intended verification path if either ever drifts.
	/// </summary>
	[Fact]
	public void MutationGuard_InSpecCompletedExitCodeSet_Is_0_100_101_AndOnlyThose()
	{
		int[] completedNotFailingExitCodes = [0, 100, 101];
		Assert.Equal(3, completedNotFailingExitCodes.Length);
		Assert.Contains(0, completedNotFailingExitCodes);
		Assert.Contains(100, completedNotFailingExitCodes);
		Assert.Contains(101, completedNotFailingExitCodes);
		Assert.DoesNotContain(1, completedNotFailingExitCodes);
		Assert.DoesNotContain(2, completedNotFailingExitCodes);

		// Cross-check against the SHIPPED module source text directly -- an independent
		// read of the real contract, not a value this fact merely restates.
		string modulePath = FindRepoFile("backend/Waypoint.Infrastructure.Execution/PowerShell/Modules/WaypointScan/WaypointScan.psm1");
		string moduleSource = File.ReadAllText(modulePath);
		int occurrences = System.Text.RegularExpressions.Regex.Matches(moduleSource, @"AllowedExitCodes\s+@\(0,\s*100,\s*101\)").Count;
		Assert.True(occurrences >= 3, $"expected at least 3 transport functions (vmware/nsx/srg) to declare AllowedExitCodes @(0, 100, 101); found {occurrences} in '{modulePath}'.");
	}

	private static string FindRepoFile(string relativePath)
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			string candidate = Path.Combine(directory.FullName, relativePath);
			if (File.Exists(candidate))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new FileNotFoundException($"Could not locate '{relativePath}' by walking up from AppContext.BaseDirectory");
	}

	// --- seeding helpers -----------------------------------------------------------

	private static string BuildPayload(ExecutionParityRow row, Guid targetId, Guid executionProfileId, Guid baselineId)
	{
		object payloadObject = row.SelectorName is null
			? new
			{
				target_id = targetId,
				transport = row.Transport,
				selector_kind = row.SelectorKind,
				catalog_execution_profile_id = executionProfileId,
				baseline_id = baselineId,
				output_kind = row.OutputKind,
			}
			: new
			{
				target_id = targetId,
				transport = row.Transport,
				selector_kind = row.SelectorKind,
				selector_name = row.SelectorName,
				catalog_execution_profile_id = executionProfileId,
				baseline_id = baselineId,
				output_kind = row.OutputKind,
			};
		return JsonSerializer.Serialize(payloadObject);
	}

	private async Task<(Guid TargetId, Guid CredentialId, Guid? BoundCredentialId, string BoundPurpose)> SeedTargetForRowAsync(ExecutionParityRow row)
	{
		Guid siteId = (await _sites.CreateAsync($"site-{row.MatrixRowId}-{Guid.NewGuid():N}", null, null, CancellationToken.None))!.Value;

		if (row.Transport == CatalogTransports.NsxApi)
		{
			Guid credentialId = (await _credentials.CreateAsync(
				$"svc-nsx-{Guid.NewGuid():N}@example.internal", CredentialTypes.Nsx, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, "admin@example.internal"))!.Value;
			await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes($"invented-nsx-secret-{row.MatrixRowId}"), "test", CancellationToken.None);
			string connectionJson = JsonSerializer.Serialize(new { host = "nsxmgr-01.example.internal" });
			(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
				siteId, TargetKinds.NsxApi, $"target-{row.MatrixRowId}-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
			Assert.Equal(TargetWriteOutcome.Ok, outcome);
			return (targetId!.Value, credentialId, null, row.CredentialPurpose);
		}

		if (row.Transport == CatalogTransports.Ssh && row.SelectorKind == CatalogSelectorKinds.Service)
		{
			// VCSA service: owning target is vsphere-kind (vsphere-api), plus a
			// separately-bound vcsa-ssh credential -- matching docs/compliance-parity.md's
			// "ssh / named VCSA service" row's dual-purpose shape.
			Guid vsphereCredentialId = (await _credentials.CreateAsync(
				$"svc-scan-{Guid.NewGuid():N}@example.internal", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, "administrator@example.internal"))!.Value;
			await _secretStore.StoreAsync(vsphereCredentialId, System.Text.Encoding.UTF8.GetBytes($"invented-vsphere-secret-{row.MatrixRowId}"), "test", CancellationToken.None);
			string vsphereConnectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
			(TargetWriteOutcome vsphereOutcome, Guid? targetId) = await _targets.CreateAsync(
				siteId, TargetKinds.VSphere, $"target-{row.MatrixRowId}-{Guid.NewGuid():N}", vsphereConnectionJson, vsphereCredentialId, CancellationToken.None);
			Assert.Equal(TargetWriteOutcome.Ok, vsphereOutcome);

			Guid vcsaSshCredentialId = (await _credentials.CreateAsync(
				$"svc-vcsa-ssh-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, "root@example.internal"))!.Value;
			await _secretStore.StoreAsync(vcsaSshCredentialId, System.Text.Encoding.UTF8.GetBytes($"invented-vcsa-ssh-secret-{row.MatrixRowId}"), "test", CancellationToken.None);

			return (targetId!.Value, vsphereCredentialId, vcsaSshCredentialId, CredentialPurposes.VcsaSsh);
		}

		if (row.Transport == CatalogTransports.Ssh)
		{
			// SRG whole-appliance product: ssh transport, srg-ssh purpose, target IS the
			// credential owner (no separate binding).
			Guid credentialId = (await _credentials.CreateAsync(
				$"svc-srg-{Guid.NewGuid():N}@example.internal", CredentialTypes.Ssh, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, "svc-srg@example.internal"))!.Value;
			await _secretStore.StoreAsync(credentialId, System.Text.Encoding.UTF8.GetBytes($"invented-srg-secret-{row.MatrixRowId}"), "test", CancellationToken.None);
			string connectionJson = JsonSerializer.Serialize(new { host = "srg-photon-01.example.internal" });
			(TargetWriteOutcome outcome, Guid? targetId) = await _targets.CreateAsync(
				siteId, TargetKinds.Ssh, $"target-{row.MatrixRowId}-{Guid.NewGuid():N}", connectionJson, credentialId, CancellationToken.None);
			Assert.Equal(TargetWriteOutcome.Ok, outcome);
			return (targetId!.Value, credentialId, null, row.CredentialPurpose);
		}

		// vmware transport (vcenter/esxi/vm selectors): vsphere-api purpose.
		Guid vCenterCredentialId = (await _credentials.CreateAsync(
			$"svc-scan-{Guid.NewGuid():N}@example.internal", CredentialTypes.VCenter, CredentialOwners.Shared, sudoEnabled: false, CancellationToken.None, "administrator@example.internal"))!.Value;
		await _secretStore.StoreAsync(vCenterCredentialId, System.Text.Encoding.UTF8.GetBytes($"invented-vsphere-secret-{row.MatrixRowId}"), "test", CancellationToken.None);
		string vmwareConnectionJson = JsonSerializer.Serialize(new { host = "vcsa-01.example.internal" });
		(TargetWriteOutcome vmwareOutcome, Guid? vmwareTargetId) = await _targets.CreateAsync(
			siteId, TargetKinds.VSphere, $"target-{row.MatrixRowId}-{Guid.NewGuid():N}", vmwareConnectionJson, vCenterCredentialId, CancellationToken.None);
		Assert.Equal(TargetWriteOutcome.Ok, vmwareOutcome);
		return (vmwareTargetId!.Value, vCenterCredentialId, null, row.CredentialPurpose);
	}

	private async Task<(Guid CatalogExecutionProfileId, Guid BaselineId, string ProfileKey)> SeedCatalogAndBaselineForRowAsync(ExecutionParityRow row)
	{
		string suffix = row.MatrixRowId + "-" + Guid.NewGuid().ToString("N");
		string vendor = row.Transport switch
		{
			CatalogTransports.NsxApi => "nsx",
			_ => "vmware",
		};

		CatalogSourceRevision source = await _catalog.UpsertSourceRevisionAsync($"rev-{suffix}", null, CancellationToken.None);
		CatalogProduct product = await _catalog.UpsertProductAsync(source.Id, vendor, $"product-{suffix}", "Invented Product", CancellationToken.None);
		CatalogProductVersion productVersion = await _catalog.UpsertProductVersionAsync(product.Id, "1.0.0", "1.0.0", CancellationToken.None);
		// Issue #729's CatalogVocabularyValidator only allows a catalog-level
		// SelectorName for the 'service' selector kind (a named VCSA/NSX sub-service --
		// its own catalog identity IS the name); vcenter/esxi/vm/target selectors carry
		// no catalog-level SelectorName (object identity is instance/discovery-layer
		// data, exactly as PlannerParityContractTests' own seeding passes null here).
		string? catalogSelectorName = row.SelectorKind == CatalogSelectorKinds.Service ? row.SelectorName : null;
		CatalogComponent catalogComponent = await _catalog.UpsertComponentAsync(
			productVersion.Id,
			new CatalogComponentDefinition($"{row.SelectorKind}-{suffix}", row.SelectorKind, row.Transport, row.SelectorKind, catalogSelectorName, null),
			CancellationToken.None);
		string contentKind = row.OutputKind == CatalogOutputKinds.HdfAndCkl ? CatalogKinds.Stig : CatalogKinds.Srg;
		CatalogContentRelease release = await _catalog.UpsertContentReleaseAsync(source.Id, contentKind, $"release-{suffix}", "Test Release", CancellationToken.None);
		CatalogReportGroup reportGroup = await _catalog.UpsertReportGroupAsync($"group-{suffix}", "Test Group", 1, CancellationToken.None);
		CatalogExecutionProfile executionProfile = await _catalog.CreateExecutionProfileAsync(
			catalogComponent.Id, release.Id, reportGroup.Id, "v1", row.OutputKind, CancellationToken.None);

		string profileKey = $"{row.Transport}/invented/{row.SelectorKind}-{suffix}-profile";
		CatalogImportReport report = await _catalog.RecordImportReportAsync($"commit-{suffix}", $"digest-report-{suffix}", 1, 0, 0, CancellationToken.None);
		await _catalog.RecordImportReportEntryAsync(report.Id, CatalogImportEntryDispositions.Accepted, profileKey, null, executionProfile.Id, CancellationToken.None);

		string contentDigest = $"digest-{suffix}";
		string stagedRelativePath = $"revisions/{contentDigest}";
		ContentRevision revision = await _baselines.RecordStagedRevisionAsync($"commit-{suffix}", contentDigest, stagedRelativePath, CancellationToken.None);
		Baseline staged = await _baselines.CreateStagedBaselineAsync(revision.Id, executionProfile.Id, benchmarkRevisionId: null, CancellationToken.None);
		BaselineActivationOutcome outcome = await _baselines.ActivateAsync(staged.Id, "test-fixture", CancellationToken.None);
		Assert.Equal(BaselineActivationOutcome.Activated, outcome);

		string profileDirectory = Path.Combine(_contentDirectory, stagedRelativePath, profileKey);
		Directory.CreateDirectory(profileDirectory);
		await File.WriteAllTextAsync(Path.Combine(profileDirectory, "inspec.yml"), $"name: invented-{suffix}-profile\n");

		return (executionProfile.Id, staged.Id, profileKey);
	}

	/// <summary>
	/// Seeds a Global-layer Input config doc keyed to <paramref name="executionProfileId"/>
	/// (the same key <c>PlanConfigResolutionService</c> resolves against). Returns
	/// (DocId, Version) for a hand-built payload's <c>input_resolutions</c> entry --
	/// <see cref="ScanJobHandler"/> only ever materializes inputs SNAPSHOTTED onto the
	/// payload at plan-compile time (<c>payload.InputResolutionsOrEmpty</c>), never a
	/// live re-query of config docs, so a test driving the handler directly (bypassing
	/// the real planner) must include this snapshot itself -- mirroring
	/// <c>ScanJobHandlerEndToEndTests</c>' own <c>input_resolutions</c> payload shape.
	/// </summary>
	private async Task<(Guid DocId, int Version)> CreateResolvedInputDocAsync(Guid executionProfileId, string bodyYaml)
	{
		(ConfigDocSaveOutcome outcome, ConfigDoc? doc, ConfigDocVersion? version) = await _configDocs.SaveAsync(
			Guid.NewGuid(), ConfigDocKinds.Input, $"invented-inputs-profile-{executionProfileId:N}", ConfigDocLayers.Global, layerRef: null,
			"test-fixture", bodyYaml, CancellationToken.None, catalogExecutionProfileId: executionProfileId);
		Assert.Equal(ConfigDocSaveOutcome.Ok, outcome);
		return (doc!.Id, version!.Version);
	}

	private async Task<string> GetJobFieldAsync(Guid jobId, string field)
	{
		await using NpgsqlConnection connection = new(_fixture.ConnectionString);
		await connection.OpenAsync();
		await using NpgsqlCommand query = new($"SELECT COALESCE({field}::text, '') FROM jobs WHERE id = $1", connection);
		query.Parameters.AddWithValue(jobId);
		return (string)(await query.ExecuteScalarAsync())!;
	}

	/// <summary>
	/// Polls until either the fake executor has captured an invocation (the assertion
	/// point this suite's command-construction theory needs) or the job reaches a
	/// terminal state without ever invoking PowerShell (a fail-closed seeding defect,
	/// which the theory's own <c>Assert.NotNull(LastRequest)</c> then reports clearly
	/// rather than timing out silently).
	/// </summary>
	private async Task PollUntilRequestCapturedOrTerminalAsync(Guid jobId)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			if (_executor.LastScanRequest is not null)
			{
				return;
			}

			string state = await GetJobFieldAsync(jobId, "state");
			if (state is "failed" or "auth-failed" or "cancelled")
			{
				return;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail("Condition not met within 30s: no PowerShell invocation captured and job never reached a terminal state.");
	}

	/// <summary>Polls until the job's state matches one of <paramref name="candidateStates"/>, returning whichever it reached.</summary>
	private async Task<string> PollUntilOneOfAsync(Guid jobId, params string[] candidateStates)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			string state = await GetJobFieldAsync(jobId, "state");
			if (candidateStates.Contains(state))
			{
				return state;
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100));
		}

		Assert.Fail($"Condition not met within 30s: job never reached one of [{string.Join(", ", candidateStates)}].");
		return null!; // unreachable
	}
}
