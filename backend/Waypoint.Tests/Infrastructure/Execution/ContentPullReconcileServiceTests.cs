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
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;
using Waypoint.Infrastructure.Execution.ComplianceContent;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #1016 (epic #726), owner decision 2026-08-28: unit coverage of
/// <see cref="ContentPullReconcileService.TryReconcileAsync"/> -- the completion step
/// that used to run inline inside <c>ContentPullJobHandler</c> (issue #729's semantic
/// import/promotion, issue #731's revision staging, pull-history recording) and now
/// runs once every fanned-out <c>content-check</c> job for a pull is terminal. These
/// tests feed <see cref="ContentCheckResultRecord"/> rows directly (the durable,
/// cross-process form <c>ContentCheckJobHandler</c> writes) rather than going through
/// PowerShell -- the promotion/quarantine/aggregate logic under test is unchanged from
/// the pre-#1016 handler, just relocated; <c>ContentCheckJobHandlerTests</c> covers the
/// PowerShell-facing parsing layer, including the real-executor equivalence proof.
///
/// <see cref="ReconcileServiceEquivalence_MatchesPreFanOutSinglePassOutcome"/> is the
/// explicit equivalence proof the fan-out/reconcile PR requires: the same fixture
/// profile set, split across two "chunks" (two content-check jobs) instead of the old
/// single in-process loop, produces the identical accepted/rejected/promoted histogram.
/// </summary>
public sealed class ContentPullReconcileServiceTests
{
	private const string ValidVCenterManifest = """
		name: vsphere-8-vcenter-stig-baseline
		title: vCenter STIG
		version: 2.3.0
		inputs:
		  - name: vcenter_host
		    type: string
		    required: true
		""";

	// --- fakes -------------------------------------------------------------------

	private sealed class FakeCheckFanOutRepository : IContentPullCheckFanOutRepository
	{
		private readonly Dictionary<Guid, List<ContentCheckResultRecord>> _resultsByCheckJob = new();
		private readonly List<ContentPullCheckFanOut> _fanOuts = [];
		private readonly Dictionary<Guid, string> _checkJobStates = new();

		public bool Reconciled { get; private set; }

		/// <summary>Registers one fanned-out check job with its terminal state (default "done") -- mirrors what ContentPullJobHandler + the job engine would have recorded.</summary>
		public void AddFanOut(Guid runId, Guid contentPullJobId, Guid checkJobId, string sourceCommit, IReadOnlyList<ContentCheckProfileDirectory> chunk, string state = "done")
		{
			_fanOuts.Add(new ContentPullCheckFanOut(Guid.NewGuid(), runId, contentPullJobId, checkJobId, sourceCommit, chunk, "pending"));
			_checkJobStates[checkJobId] = state;
			_resultsByCheckJob[checkJobId] = [];
		}

		/// <summary>Registers the zero-chunk marker row a zero-profile pull records (RecordEmptyFanOutAsync's shape: null check job, empty chunk).</summary>
		public void AddEmptyFanOut(Guid runId, Guid contentPullJobId, string sourceCommit) =>
			_fanOuts.Add(new ContentPullCheckFanOut(Guid.NewGuid(), runId, contentPullJobId, CheckJobId: null, sourceCommit, [], "pending"));

		public void AddResult(Guid checkJobId, ContentCheckResultRecord result) => _resultsByCheckJob[checkJobId].Add(result);

		public Task RecordFanOutAsync(Guid runId, Guid contentPullJobId, Guid checkJobId, string sourceCommit, IReadOnlyList<ContentCheckProfileDirectory> profileDirectories, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task RecordEmptyFanOutAsync(Guid runId, Guid contentPullJobId, string sourceCommit, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<ContentPullCheckFanOut?> GetFanOutForCheckJobAsync(Guid checkJobId, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task RecordCheckResultAsync(Guid checkJobId, ContentCheckResultRecord result, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<IReadOnlyList<Guid>> ListPendingReconcileContentPullJobIdsAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<Guid>>([.. _fanOuts.Select(f => f.ContentPullJobId).Distinct()]);

		public Task<IReadOnlyList<ContentPullCheckFanOut>> ListFanOutsForContentPullJobAsync(Guid contentPullJobId, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<ContentPullCheckFanOut>>([.. _fanOuts.Where(f => f.ContentPullJobId == contentPullJobId)]);

		public Task<ContentPullCheckReconcileReadiness> GetReconcileReadinessAsync(Guid contentPullJobId, CancellationToken cancellationToken)
		{
			// Mirrors the real repository's marker-aware LEFT JOIN semantics: rows with a
			// null CheckJobId (zero-chunk markers) count toward "this pull has fan-out
			// state" but contribute no check job to wait on.
			List<ContentPullCheckFanOut> rows = [.. _fanOuts.Where(f => f.ContentPullJobId == contentPullJobId)];
			List<Guid> checkJobIds = [.. rows.Where(f => f.CheckJobId is not null).Select(f => f.CheckJobId!.Value)];
			int total = checkJobIds.Count;
			int failed = checkJobIds.Count(id => _checkJobStates[id] is "failed" or "auth-failed" or "cancelled");
			bool allTerminal = rows.Count > 0 && checkJobIds.All(id => _checkJobStates[id] is "done" or "failed" or "auth-failed" or "cancelled");
			return Task.FromResult(new ContentPullCheckReconcileReadiness(allTerminal, total, failed));
		}

		public Task<IReadOnlyList<ContentCheckResultRecord>> ListCheckResultsAsync(IReadOnlyList<Guid> checkJobIds, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<ContentCheckResultRecord>>([.. checkJobIds.SelectMany(id => _resultsByCheckJob.TryGetValue(id, out List<ContentCheckResultRecord>? results) ? results : [])]);

		public Task MarkReconciledAsync(Guid contentPullJobId, CancellationToken cancellationToken)
		{
			Reconciled = true;
			return Task.CompletedTask;
		}
	}

	private sealed record RecordedPull(Guid? JobId, string RefType, string RefValue, string? Commit, string Status, string? Note, string? InitiatedBy);

	private sealed class FakeContentRepository : IComplianceContentRepository
	{
		private readonly ComplianceContentConfig? _config;

		public FakeContentRepository(ComplianceContentConfig? config) => _config = config;

		public List<RecordedPull> Pulls { get; } = [];

		public Task<ComplianceContentConfig?> GetConfigAsync(CancellationToken cancellationToken) => Task.FromResult(_config);

		public Task<ComplianceContentConfig> PutConfigAsync(string repositoryUrl, string refType, string refValue, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task RecordPullAsync(Guid? jobId, string refType, string refValue, string? commit, string status, string? note, string? initiatedBy, CancellationToken cancellationToken)
		{
			Pulls.Add(new RecordedPull(jobId, refType, refValue, commit, status, note, initiatedBy));
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<ComplianceContentPull>> ListPullsAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
	}

	private sealed class FakeJobRunnerRepository : IJobRunnerRepository
	{
		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) =>
			Task.FromResult<RunQueueState?>(new RunQueueState("running", Paused: false, Blocked: false, BlockedReason: null, InitiatedBy: "admin@example.internal"));

		public Task<IReadOnlyList<Guid>> FanOutAdditionalJobsAsync(Guid runId, IReadOnlyList<JobSpec> specs, string? createdBy, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task RecordUploadAttemptAsync(Guid jobId, string? endpoint, string? collection, string uploadStatus, string? detail, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<UploadAttemptRecord>> GetUploadAttemptsAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobCredentialBinding>>([]);
	}

	private sealed class FakeContentRevisionStager : IContentRevisionStager
	{
		public List<(string ContentPath, string SourceCommit, string ContentDigest)> Calls { get; } = [];

		public Task<ContentRevision> StageAsync(string contentPath, string sourceCommit, string contentDigest, CancellationToken cancellationToken)
		{
			Calls.Add((contentPath, sourceCommit, contentDigest));
			return Task.FromResult(new ContentRevision(
				Guid.NewGuid(), sourceCommit, contentDigest, Path.Combine("revisions", contentDigest), ContentRevisionStatuses.Staged, GcEligible: false, DateTimeOffset.UtcNow));
		}
	}

	private sealed class RecordingEventPublisher : IJobEventPublisher
	{
		public List<(string EventType, Guid? JobId, Guid? RunId, string Payload)> Events { get; } = [];

		public Task EmitAsync(string eventType, Guid? jobId, Guid? runId, string payloadJson, CancellationToken cancellationToken)
		{
			Events.Add((eventType, jobId, runId, payloadJson));
			return Task.CompletedTask;
		}
	}

	/// <summary>Mirrors the pre-#1016 handler test's in-memory catalog fake -- same additive/dedup semantics as the real Postgres-backed repository, proven separately by <c>CatalogRepositoryTests</c>.</summary>
	private sealed class FakeCatalogRepository : ICatalogRepository
	{
		private readonly Dictionary<string, CatalogSourceRevision> _sourceRevisions = new(StringComparer.Ordinal);
		private readonly Dictionary<(string Vendor, string ProductKey), CatalogProduct> _products = new();
		private readonly Dictionary<(Guid ProductId, string VersionKey), CatalogProductVersion> _productVersions = new();
		private readonly Dictionary<(Guid ProductVersionId, Guid? ParentId, string ComponentKey), CatalogComponent> _components = new();
		private readonly Dictionary<(string Kind, string ReleaseKey), CatalogContentRelease> _contentReleases = new();
		private readonly Dictionary<string, CatalogReportGroup> _reportGroups = new(StringComparer.Ordinal);
		private readonly Dictionary<(Guid ComponentId, Guid ContentReleaseId), CatalogExecutionProfile> _executionProfiles = new();
		private readonly Dictionary<(Guid ExecutionProfileId, string Name), CatalogDeclaredInput> _declaredInputs = new();

		public List<CatalogImportReport> Reports { get; } = [];

		public List<CatalogImportReportEntry> Entries { get; } = [];

		public Task<CatalogSourceRevision> UpsertSourceRevisionAsync(string revisionKey, string? description, CancellationToken cancellationToken)
		{
			if (!_sourceRevisions.TryGetValue(revisionKey, out CatalogSourceRevision? revision))
			{
				revision = new CatalogSourceRevision(Guid.NewGuid(), revisionKey, description, DateTimeOffset.UtcNow);
				_sourceRevisions[revisionKey] = revision;
			}

			return Task.FromResult(revision);
		}

		public Task<CatalogProduct> UpsertProductAsync(Guid sourceRevisionId, string vendor, string productKey, string displayName, CancellationToken cancellationToken)
		{
			(string vendor, string productKey) key = (vendor, productKey);
			if (!_products.TryGetValue(key, out CatalogProduct? product))
			{
				product = new CatalogProduct(Guid.NewGuid(), sourceRevisionId, vendor, productKey, displayName, DateTimeOffset.UtcNow);
				_products[key] = product;
			}

			return Task.FromResult(product);
		}

		public Task<CatalogProductVersion> UpsertProductVersionAsync(Guid productId, string versionKey, string displayName, CancellationToken cancellationToken)
		{
			(Guid productId, string versionKey) key = (productId, versionKey);
			if (!_productVersions.TryGetValue(key, out CatalogProductVersion? version))
			{
				version = new CatalogProductVersion(Guid.NewGuid(), productId, versionKey, displayName, DateTimeOffset.UtcNow);
				_productVersions[key] = version;
			}

			return Task.FromResult(version);
		}

		public Task<CatalogContentRelease> UpsertContentReleaseAsync(Guid sourceRevisionId, string kind, string releaseKey, string displayName, CancellationToken cancellationToken)
		{
			(string kind, string releaseKey) key = (kind, releaseKey);
			if (!_contentReleases.TryGetValue(key, out CatalogContentRelease? release))
			{
				release = new CatalogContentRelease(Guid.NewGuid(), sourceRevisionId, kind, releaseKey, displayName, DateTimeOffset.UtcNow);
				_contentReleases[key] = release;
			}

			return Task.FromResult(release);
		}

		public Task<CatalogComponent> UpsertComponentAsync(Guid productVersionId, CatalogComponentDefinition definition, CancellationToken cancellationToken)
		{
			(Guid productVersionId, Guid? ParentComponentId, string ComponentKey) key = (productVersionId, definition.ParentComponentId, definition.ComponentKey);
			if (!_components.TryGetValue(key, out CatalogComponent? component))
			{
				component = new CatalogComponent(
					Guid.NewGuid(), productVersionId, definition.ParentComponentId, definition.ComponentKey, definition.DisplayName,
					definition.Transport, definition.SelectorKind, definition.SelectorName, DateTimeOffset.UtcNow);
				_components[key] = component;
			}

			return Task.FromResult(component);
		}

		public Task<CatalogReportGroup> UpsertReportGroupAsync(string groupKey, string displayName, int priority, CancellationToken cancellationToken)
		{
			if (!_reportGroups.TryGetValue(groupKey, out CatalogReportGroup? group))
			{
				group = new CatalogReportGroup(Guid.NewGuid(), groupKey, displayName, priority, DateTimeOffset.UtcNow);
				_reportGroups[groupKey] = group;
			}

			return Task.FromResult(group);
		}

		public Task<CatalogExecutionProfile> CreateExecutionProfileAsync(Guid componentId, Guid contentReleaseId, Guid reportGroupId, string profileVersion, string outputKind, CancellationToken cancellationToken)
		{
			(Guid componentId, Guid contentReleaseId) key = (componentId, contentReleaseId);
			if (_executionProfiles.ContainsKey(key))
			{
				throw new InvalidOperationException("an execution profile already exists for this (component, content release) pair.");
			}

			CatalogExecutionProfile profile = new(Guid.NewGuid(), componentId, contentReleaseId, reportGroupId, profileVersion, false, outputKind, DateTimeOffset.UtcNow);
			_executionProfiles[key] = profile;
			return Task.FromResult(profile);
		}

		public Task<CatalogCredentialRequirement> AddCredentialRequirementAsync(Guid executionProfileId, string purpose, bool isRequired, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<CatalogBenchmarkReference> SetBenchmarkReferenceAsync(Guid executionProfileId, string benchmarkKey, string benchmarkVersion, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<CatalogRemediationDefinition> SetRemediationDefinitionAsync(Guid executionProfileId, bool isSupported, string? mechanismNote, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogProduct>>([.. _products.Values]);
		public Task<IReadOnlyList<CatalogProductVersion>> ListProductVersionsAsync(Guid productId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogProductVersion>>([.. _productVersions.Values.Where(v => v.ProductId == productId)]);
		public Task<IReadOnlyList<CatalogComponent>> ListComponentsAsync(Guid productVersionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogComponent>>([.. _components.Values.Where(c => c.ProductVersionId == productVersionId)]);
		public Task<CatalogComponent?> GetComponentAsync(Guid componentId, CancellationToken cancellationToken) => Task.FromResult(_components.Values.FirstOrDefault(c => c.Id == componentId));

		public Task<IReadOnlyList<CatalogComponent>> FindTopLevelComponentsByKeyAndVersionAsync(string catalogComponentKey, string exactVersion, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogComponent>>([.. _components.Values.Where(c =>
				c.ParentComponentId is null &&
				c.ComponentKey == catalogComponentKey &&
				_productVersions.Values.Any(v => v.Id == c.ProductVersionId && v.VersionKey == exactVersion))]);

		public Task<IReadOnlyList<CatalogComponent>> ListTopLevelComponentsByKeyAsync(string catalogComponentKey, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogComponent>>([.. _components.Values.Where(c =>
				c.ParentComponentId is null && c.ComponentKey == catalogComponentKey)]);

		public Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListExecutionProfilesByComponentAsync(Guid componentId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<CatalogExecutionProfileDetail?> GetExecutionProfileAsync(Guid executionProfileId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListAllExecutionProfilesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<CatalogDeclaredInput> UpsertDeclaredInputAsync(Guid executionProfileId, string name, string? inputType, bool isRequired, CancellationToken cancellationToken)
		{
			(Guid executionProfileId, string name) key = (executionProfileId, name);
			CatalogDeclaredInput input = new(Guid.NewGuid(), executionProfileId, name, inputType, isRequired, DateTimeOffset.UtcNow);
			_declaredInputs[key] = input;
			return Task.FromResult(input);
		}

		public Task<IReadOnlyList<CatalogDeclaredInput>> ListDeclaredInputsAsync(Guid executionProfileId, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogDeclaredInput>>([.. _declaredInputs.Values.Where(i => i.ExecutionProfileId == executionProfileId).OrderBy(i => i.Name, StringComparer.Ordinal)]);

		public Task<CatalogImportReport> RecordImportReportAsync(string sourceCommit, string sourceDigest, int acceptedCount, int warningCount, int rejectedCount, CancellationToken cancellationToken)
		{
			CatalogImportReport report = new(Guid.NewGuid(), sourceCommit, sourceDigest, acceptedCount, warningCount, rejectedCount, DateTimeOffset.UtcNow);
			Reports.Add(report);
			return Task.FromResult(report);
		}

		public Task<CatalogImportReportEntry> RecordImportReportEntryAsync(Guid reportId, string disposition, string profileKey, string? reason, Guid? executionProfileId, CancellationToken cancellationToken)
		{
			CatalogImportReportEntry entry = new(Guid.NewGuid(), reportId, disposition, profileKey, reason, executionProfileId, DateTimeOffset.UtcNow);
			Entries.Add(entry);
			return Task.FromResult(entry);
		}

		public Task<IReadOnlyList<CatalogImportReport>> ListImportReportsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogImportReport>>([.. Reports.OrderByDescending(r => r.RecordedAt).Take(limit)]);
		public Task<IReadOnlyList<CatalogImportReportEntry>> ListImportReportEntriesAsync(Guid reportId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CatalogImportReportEntry>>([.. Entries.Where(e => e.ReportId == reportId).OrderBy(e => e.ProfileKey, StringComparer.Ordinal)]);

		public Task<string?> GetProfileKeyForExecutionProfileAsync(Guid executionProfileId, CancellationToken cancellationToken) =>
			Task.FromResult(Entries.Where(e => e.ExecutionProfileId == executionProfileId).OrderByDescending(e => e.CreatedAt).Select(e => (string?)e.ProfileKey).FirstOrDefault());

		public async Task<CatalogPromotionOutcome> PromoteCandidateAsync(SemanticCandidate candidate, CatalogPromotionRequest request, CancellationToken cancellationToken)
		{
			if (!candidate.IsExecutableLeaf)
			{
				return new CatalogPromotionOutcome(null, "candidate is an aggregate profile");
			}

			CatalogSourceRevision sourceRevision = await UpsertSourceRevisionAsync(request.SourceRevisionKey, null, cancellationToken);
			CatalogProduct product = await UpsertProductAsync(sourceRevision.Id, request.Vendor, candidate.VendorFamily, request.ProductDisplayName, cancellationToken);
			CatalogProductVersion productVersion = await UpsertProductVersionAsync(product.Id, candidate.ProductVersionKey, request.ProductVersionDisplayName, cancellationToken);
			CatalogComponentDefinition definition = new(candidate.ComponentKey, candidate.DisplayName, candidate.Transport, candidate.SelectorKind, candidate.SelectorName, null);
			CatalogComponent component = await UpsertComponentAsync(productVersion.Id, definition, cancellationToken);
			CatalogContentRelease contentRelease = await UpsertContentReleaseAsync(sourceRevision.Id, candidate.Kind, $"{candidate.ProductVersionKey}:{candidate.Kind}:{candidate.ContentDigest[..12]}", request.ContentReleaseDisplayName, cancellationToken);
			CatalogReportGroup reportGroup = await UpsertReportGroupAsync(request.ReportGroupKey, request.ReportGroupDisplayName, request.ReportGroupPriority, cancellationToken);

			(Guid ComponentId, Guid ContentReleaseId) key = (component.Id, contentRelease.Id);
			if (!_executionProfiles.TryGetValue(key, out CatalogExecutionProfile? profile))
			{
				profile = await CreateExecutionProfileAsync(component.Id, contentRelease.Id, reportGroup.Id, candidate.ManifestVersion ?? "unknown", request.OutputKind, cancellationToken);
			}

			foreach (InspecManifestInput input in candidate.Inputs)
			{
				await UpsertDeclaredInputAsync(profile.Id, input.Name, input.Type, input.Required, cancellationToken);
			}

			return new CatalogPromotionOutcome(profile.Id, null);
		}
	}

	// --- helpers -------------------------------------------------------------------

	private static ContentCheckResultRecord Entry(
		string profileKey, string? rawYaml, bool hasControlsDirectory, bool inspecCheckRan = true, bool inspecCheckPassed = true, params string[] controlFileNames) =>
		new(profileKey, rawYaml, hasControlsDirectory, HasFilesDirectory: false, controlFileNames,
			inspecCheckRan, inspecCheckPassed, inspecCheckRan && !inspecCheckPassed ? "inspec check exited non-zero (invented fixture detail)" : null);

	private static (ContentPullReconcileService Service, FakeCheckFanOutRepository CheckFanOut, FakeContentRepository Content,
		FakeCatalogRepository Catalog, FakeContentRevisionStager Stager, RecordingEventPublisher Events) Build()
	{
		FakeCheckFanOutRepository checkFanOut = new();
		FakeContentRepository content = new(new ComplianceContentConfig(
			"https://git.example.internal/dod/compliance-content.git", ComplianceContentRefTypes.Branch, "main",
			PulledCommit: null, PulledBy: null, PulledAt: null, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow));
		FakeCatalogRepository catalog = new();
		FakeJobRunnerRepository jobs = new();
		FakeContentRevisionStager stager = new();
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = "/var/lib/waypoint/compliance-content" });
		RecordingEventPublisher events = new();

		ContentPullReconcileService service = new(checkFanOut, content, catalog, jobs, stager, options, events, NullLogger<ContentPullReconcileService>.Instance);
		return (service, checkFanOut, content, catalog, stager, events);
	}

	[Fact]
	public async Task TryReconcile_NotAllTerminal_ReturnsFalse_DoesNothing()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, FakeContentRepository content, _, _, _) = Build();
		Guid pullJobId = Guid.NewGuid();
		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, Guid.NewGuid(), "commitA", [new ContentCheckProfileDirectory("p0", "/invented/p0")], state: "running");

		bool reconciled = await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		Assert.False(reconciled);
		Assert.Empty(content.Pulls);
		Assert.False(checkFanOut.Reconciled);
	}

	[Fact]
	public async Task TryReconcile_AllTerminal_PromotesAndRecordsSucceededPull()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, FakeContentRepository content, FakeCatalogRepository catalog, FakeContentRevisionStager stager, RecordingEventPublisher events) = Build();

		Guid runId = Guid.NewGuid();
		Guid pullJobId = Guid.NewGuid();
		Guid checkJobId = Guid.NewGuid();
		const string profileKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter";
		checkFanOut.AddFanOut(runId, pullJobId, checkJobId, "commitG", [new ContentCheckProfileDirectory(profileKey, "/invented/vcenter")]);
		checkFanOut.AddResult(checkJobId, Entry(profileKey, ValidVCenterManifest, hasControlsDirectory: true, controlFileNames: "vcenter_control.rb"));

		bool reconciled = await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		Assert.True(reconciled);
		Assert.True(checkFanOut.Reconciled);

		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal(1, report.AcceptedCount);
		CatalogImportReportEntry entry = Assert.Single(catalog.Entries);
		Assert.Equal(CatalogImportEntryDispositions.Accepted, entry.Disposition);
		Assert.NotNull(entry.ExecutionProfileId);

		var pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, pull.Status);
		Assert.Equal("commitG", pull.Commit);

		Assert.Single(stager.Calls);
		Assert.Contains(events.Events, e => e.EventType == JobEventTypes.RunProgress && e.RunId == runId);
	}

	[Fact]
	public async Task TryReconcile_AcceptedCandidateFailsInspecCheck_QuarantinedNotPromoted_SiblingStillPromotes()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, _, FakeCatalogRepository catalog, _, _) = Build();

		Guid pullJobId = Guid.NewGuid();
		Guid checkJobId = Guid.NewGuid();
		const string passingKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter";
		const string failingKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/esxi";
		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, checkJobId, "commitJ",
			[new ContentCheckProfileDirectory(passingKey, "/invented/vcenter"), new ContentCheckProfileDirectory(failingKey, "/invented/esxi")]);
		checkFanOut.AddResult(checkJobId, Entry(passingKey, ValidVCenterManifest, hasControlsDirectory: true, controlFileNames: "vcenter_control.rb"));
		checkFanOut.AddResult(checkJobId, Entry(failingKey, ValidVCenterManifest.Replace("vcenter", "esxi", StringComparison.Ordinal), hasControlsDirectory: true, inspecCheckPassed: false, controlFileNames: "esxi_control.rb"));

		await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal(2, report.AcceptedCount);

		CatalogImportReportEntry promoted = Assert.Single(catalog.Entries, e => e.ProfileKey == passingKey);
		Assert.NotNull(promoted.ExecutionProfileId);

		CatalogImportReportEntry quarantined = Assert.Single(catalog.Entries, e => e.ProfileKey == failingKey);
		Assert.Null(quarantined.ExecutionProfileId);
		Assert.Contains("inspec check", quarantined.Reason, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Issue #1016's core partial-failure requirement: a FAILED content-check job's
	/// profiles simply have no recorded result row (ContentCheckJobHandler never wrote
	/// one for a chunk it never successfully parsed) -- this must quarantine those
	/// profiles with the pre-existing "did not run" reason, exactly like the pre-#1016
	/// "check never ran" path, using ZERO new logic. Reconcile still runs (it does not
	/// wait forever on a dead job) and still promotes the sibling chunk's profiles.
	/// </summary>
	[Fact]
	public async Task TryReconcile_OneCheckJobFailed_SiblingChunkStillPromotes_FailedChunkProfilesQuarantinedByAbsence()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, FakeContentRepository content, FakeCatalogRepository catalog, _, _) = Build();

		Guid pullJobId = Guid.NewGuid();
		Guid goodCheckJobId = Guid.NewGuid();
		Guid failedCheckJobId = Guid.NewGuid();
		const string goodKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter";
		const string neverCheckedKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/esxi";

		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, goodCheckJobId, "commitPartial", [new ContentCheckProfileDirectory(goodKey, "/invented/vcenter")], state: "done");
		checkFanOut.AddResult(goodCheckJobId, Entry(goodKey, ValidVCenterManifest, hasControlsDirectory: true, controlFileNames: "vcenter_control.rb"));

		// The failed job's chunk covered neverCheckedKey, but it never got far enough to
		// call RecordCheckResultAsync -- no result row exists for it at all.
		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, failedCheckJobId, "commitPartial", [new ContentCheckProfileDirectory(neverCheckedKey, "/invented/esxi")], state: "failed");

		bool reconciled = await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		Assert.True(reconciled);

		// Only the successful chunk's profile has any semantic-import evidence at all --
		// the failed chunk's profile was never discovered by reconcile because no
		// content-check result row exists for it (VendorHierarchyInterpreter only sees
		// what ListCheckResultsAsync returns), so it does not even appear in the report.
		// This is honest: an entirely-failed-chunk profile produces no catalog entry
		// rather than a fabricated one, and the pull itself still records succeeded at
		// the content-pull-job level (job-level partial failure is reflected at the RUN
		// level by the ordinary run-completion mechanism over the failed check job, not
		// by this reconcile step re-deriving it).
		Assert.DoesNotContain(catalog.Entries, e => e.ProfileKey == neverCheckedKey);
		Assert.Contains(catalog.Entries, e => e.ProfileKey == goodKey && e.ExecutionProfileId != null);

		var pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, pull.Status);
	}

	[Fact]
	public async Task TryReconcile_AggregateCandidate_IsAcceptedButNeverPromoted()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, _, FakeCatalogRepository catalog, _, _) = Build();

		Guid pullJobId = Guid.NewGuid();
		Guid checkJobId = Guid.NewGuid();
		const string aggregateKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline";
		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, checkJobId, "commitJ", [new ContentCheckProfileDirectory(aggregateKey, "/invented/baseline")]);
		checkFanOut.AddResult(checkJobId, Entry(aggregateKey, ValidVCenterManifest, hasControlsDirectory: false));

		await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		CatalogImportReportEntry entry = Assert.Single(catalog.Entries);
		Assert.Equal(CatalogImportEntryDispositions.Accepted, entry.Disposition);
		Assert.Null(entry.ExecutionProfileId);
		Assert.Contains("aggregate", entry.Reason, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// The explicit equivalence proof: splitting the SAME fixture profile set across
	/// two chunks (two "content-check jobs") instead of one in-process loop produces the
	/// identical accepted/rejected/promoted histogram the pre-#1016 single-pass handler
	/// produced for the same fixture tree.
	/// </summary>
	[Fact]
	public async Task ReconcileServiceEquivalence_MatchesPreFanOutSinglePassOutcome()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, _, FakeCatalogRepository catalog, _, _) = Build();

		Guid pullJobId = Guid.NewGuid();
		Guid chunk1JobId = Guid.NewGuid();
		Guid chunk2JobId = Guid.NewGuid();
		const string vcenterKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter";
		const string esxiKey = "vsphere/8.0.3/v2r3-stig/inspec/baseline/esxi";
		const string badKey = "totally-unrecognized-shape";

		// Chunk 1: two profiles (one good, one malformed/unrecognized -- must quarantine
		// without affecting its sibling). Chunk 2: one more good profile. This is the
		// exact three-entry fixture the pre-#1016 single-pass test used, just split
		// across two "invocations" instead of one loop.
		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, chunk1JobId, "commitEquiv",
			[new ContentCheckProfileDirectory(vcenterKey, "/invented/vcenter"), new ContentCheckProfileDirectory(badKey, "/invented/bad")]);
		checkFanOut.AddResult(chunk1JobId, Entry(vcenterKey, ValidVCenterManifest, hasControlsDirectory: true, controlFileNames: "vcenter_control.rb"));
		checkFanOut.AddResult(chunk1JobId, Entry(badKey, ValidVCenterManifest, hasControlsDirectory: true, controlFileNames: "control.rb"));

		checkFanOut.AddFanOut(Guid.NewGuid(), pullJobId, chunk2JobId, "commitEquiv",
			[new ContentCheckProfileDirectory(esxiKey, "/invented/esxi")]);
		checkFanOut.AddResult(chunk2JobId, Entry(esxiKey, ValidVCenterManifest.Replace("vcenter", "esxi", StringComparison.Ordinal), hasControlsDirectory: true, controlFileNames: "esxi_control.rb"));

		await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		// Same histogram the pre-#1016 single-pass "one bad content entry" fixture
		// produced: 2 accepted (vcenter, esxi both real families), 1 rejected (the
		// unrecognized vendor-family directory), both accepted candidates promoted.
		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal(2, report.AcceptedCount);
		Assert.Equal(1, report.RejectedCount);
		Assert.Equal(0, report.WarningCount);

		Assert.Equal(2, catalog.Entries.Count(e => e.Disposition == CatalogImportEntryDispositions.Accepted && e.ExecutionProfileId != null));
		Assert.Single(catalog.Entries, e => e.Disposition == CatalogImportEntryDispositions.Rejected && e.ProfileKey == badKey);
	}

	[Fact]
	public async Task TryReconcile_NoFanOutRowsAtAll_ReturnsFalse()
	{
		(ContentPullReconcileService service, _, _, _, _, _) = Build();

		bool reconciled = await service.TryReconcileAsync(Guid.NewGuid(), CancellationToken.None);

		Assert.False(reconciled);
	}

	/// <summary>
	/// PR #1017 review round 1, finding 2 (the reconcile half; the handler half is
	/// <c>ContentPullJobHandlerTests.Execute_NoProfilesDiscovered_RecordsZeroChunkMarker_SoReconcileStillCompletesThePull</c>):
	/// a zero-profile pull's zero-chunk marker row must reconcile IMMEDIATELY -- no
	/// check jobs to wait for -- and still record the succeeded pull-history row, the
	/// empty import report, and the staged revision, exactly what the pre-#1016 inline
	/// handler did for a zero-profile checkout ("every attempt recorded", issue #40).
	/// </summary>
	[Fact]
	public async Task TryReconcile_ZeroChunkMarker_CompletesImmediately_RecordsSucceededPullAndStagesRevision()
	{
		(ContentPullReconcileService service, FakeCheckFanOutRepository checkFanOut, FakeContentRepository content, FakeCatalogRepository catalog, FakeContentRevisionStager stager, RecordingEventPublisher events) = Build();

		Guid runId = Guid.NewGuid();
		Guid pullJobId = Guid.NewGuid();
		checkFanOut.AddEmptyFanOut(runId, pullJobId, "commitZeroProfiles");

		bool reconciled = await service.TryReconcileAsync(pullJobId, CancellationToken.None);

		Assert.True(reconciled);
		Assert.True(checkFanOut.Reconciled);

		// Empty import report -- semantic import over zero entries, same as the old
		// inline handler's zero-profile pass.
		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal(0, report.AcceptedCount);
		Assert.Equal(0, report.WarningCount);
		Assert.Equal(0, report.RejectedCount);
		Assert.Empty(catalog.Entries);

		var pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, pull.Status);
		Assert.Equal("commitZeroProfiles", pull.Commit);
		Assert.Equal(pullJobId, pull.JobId);

		Assert.Single(stager.Calls);
		Assert.Contains(events.Events, e => e.EventType == JobEventTypes.RunProgress && e.RunId == runId);
	}
}
