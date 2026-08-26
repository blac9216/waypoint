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

using System.Management.Automation;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.Execution.ComplianceContent;
using Xunit;

namespace Waypoint.Tests.Infrastructure.Execution;

/// <summary>
/// Issue #40: unit coverage of <see cref="ContentPullJobHandler.ExecuteAsync"/> in
/// isolation, driving the handler with an in-memory fake <see cref="IPowerShellExecutor"/>
/// (the precedent <c>DownloadJobHandlerEndToEndTests</c> exercises the real executor with
/// a stub module through the whole job engine; this class instead pins the handler's own
/// branch logic without Postgres). Proves the AC's central promise -- a failed pull STILL
/// lands in pull history -- across the config-missing, invocation-failure, no-commit,
/// success, and tag-vs-branch state paths, plus the <c>ParseOutput</c>/<c>TryParseProfile</c>
/// parsing (including malformed rows that must not fail the whole pull).
/// </summary>
public sealed class ContentPullJobHandlerTests
{
	private const string RepositoryUrl = "https://git.example.internal/dod/compliance-content.git";
	private const string ContentPath = "/var/lib/waypoint/compliance-content";

	// --- fakes -----------------------------------------------------------------

	private sealed class FakePowerShellExecutor : IPowerShellExecutor
	{
		private readonly PowerShellExecutionResult _result;

		public FakePowerShellExecutor(PowerShellExecutionResult result) => _result = result;

		public PowerShellRequest? LastRequest { get; private set; }

		public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellRequest request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(_result);
		}
	}

	private sealed record RecordedPull(
		Guid? JobId, string RefType, string RefValue, string? Commit, string Status, string? Note, string? InitiatedBy);

	private sealed class FakeContentRepository : IComplianceContentRepository
	{
		private readonly ComplianceContentConfig? _config;

		public FakeContentRepository(ComplianceContentConfig? config) => _config = config;

		public List<RecordedPull> Pulls { get; } = [];

		public Task<ComplianceContentConfig?> GetConfigAsync(CancellationToken cancellationToken) =>
			Task.FromResult(_config);

		public Task<ComplianceContentConfig> PutConfigAsync(string repositoryUrl, string refType, string refValue, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task RecordPullAsync(
			Guid? jobId, string refType, string refValue, string? commit, string status, string? note, string? initiatedBy, CancellationToken cancellationToken)
		{
			Pulls.Add(new RecordedPull(jobId, refType, refValue, commit, status, note, initiatedBy));
			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<ComplianceContentPull>> ListPullsAsync(int limit, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	/// <summary>
	/// <see cref="ListAsync"/> synthesizes a stable id per <see cref="ProfileUpsert.ProfileKey"/>
	/// (a dictionary keyed on the string, not a fresh guid per call) so the handler's
	/// post-replace "re-read profiles, look up each by key" control-persistence step
	/// (issue #598) sees the SAME id across the ReplaceAllAsync call and the subsequent
	/// ListAsync call within one ExecuteAsync -- exactly what the real Postgres-backed
	/// repository guarantees (a profile_key's row keeps its id across an upsert).
	/// </summary>
	private sealed class FakeProfileRepository : IProfileRepository
	{
		private readonly Dictionary<string, Guid> _idsByKey = new(StringComparer.Ordinal);

		public IReadOnlyList<ProfileUpsert>? Replaced { get; private set; }

		public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken)
		{
			if (Replaced is null)
			{
				return Task.FromResult<IReadOnlyList<Profile>>([]);
			}

			IReadOnlyList<Profile> profiles = [.. Replaced.Select(p => new Profile(IdFor(p.ProfileKey), p.ProfileKey, p.Name, p.Version, p.Commit, p.State, DateTimeOffset.UtcNow))];
			return Task.FromResult(profiles);
		}

		public Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task ReplaceAllAsync(IReadOnlyList<ProfileUpsert> profiles, CancellationToken cancellationToken)
		{
			Replaced = profiles;
			return Task.CompletedTask;
		}

		private Guid IdFor(string profileKey)
		{
			if (!_idsByKey.TryGetValue(profileKey, out Guid id))
			{
				id = Guid.NewGuid();
				_idsByKey[profileKey] = id;
			}

			return id;
		}
	}

	private sealed class FakeProfileControlRepository : IProfileControlRepository
	{
		public Dictionary<Guid, IReadOnlyList<ProfileControlUpsert>> ReplacedByProfileId { get; } = [];

		public Task<IReadOnlyList<ProfileControl>> ListByProfileAsync(Guid profileId, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task ReplaceForProfileAsync(Guid profileId, IReadOnlyList<ProfileControlUpsert> controls, CancellationToken cancellationToken)
		{
			ReplacedByProfileId[profileId] = controls;
			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// An in-memory <see cref="ICatalogRepository"/> covering exactly the surface
	/// <see cref="ContentPullJobHandler"/>'s issue #729 semantic-import pass exercises
	/// (record report/entries, promote candidates, upsert declared inputs) -- the real
	/// Postgres-backed repository's contract is proven separately by
	/// <c>CatalogRepositoryTests</c>. Every upsert-by-natural-key method here mirrors the
	/// real repository's additive/dedup semantics closely enough to prove the handler's
	/// own promotion-count and report-shape behavior in isolation, without Postgres.
	/// </summary>
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

		public Task<CatalogCredentialRequirement> AddCredentialRequirementAsync(Guid executionProfileId, string purpose, bool isRequired, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<CatalogBenchmarkReference> SetBenchmarkReferenceAsync(Guid executionProfileId, string benchmarkKey, string benchmarkVersion, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<CatalogRemediationDefinition> SetRemediationDefinitionAsync(Guid executionProfileId, bool isSupported, string? mechanismNote, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogProduct>>([.. _products.Values]);

		public Task<IReadOnlyList<CatalogProductVersion>> ListProductVersionsAsync(Guid productId, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogProductVersion>>([.. _productVersions.Values.Where(v => v.ProductId == productId)]);

		public Task<IReadOnlyList<CatalogComponent>> ListComponentsAsync(Guid productVersionId, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogComponent>>([.. _components.Values.Where(c => c.ProductVersionId == productVersionId)]);

		public Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListExecutionProfilesByComponentAsync(Guid componentId, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<CatalogExecutionProfileDetail?> GetExecutionProfileAsync(Guid executionProfileId, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListAllExecutionProfilesAsync(CancellationToken cancellationToken) =>
			throw new NotSupportedException();

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

		public Task<IReadOnlyList<CatalogImportReport>> ListImportReportsAsync(int limit, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogImportReport>>([.. Reports.OrderByDescending(r => r.RecordedAt).Take(limit)]);

		public Task<IReadOnlyList<CatalogImportReportEntry>> ListImportReportEntriesAsync(Guid reportId, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<CatalogImportReportEntry>>([.. Entries.Where(e => e.ReportId == reportId).OrderBy(e => e.ProfileKey, StringComparer.Ordinal)]);

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

	/// <summary>Only <see cref="GetRunQueueStateAsync"/> is exercised by the handler (via ResolveActorAsync).</summary>
	private sealed class FakeJobRunnerRepository : IJobRunnerRepository
	{
		private readonly string? _initiatedBy;

		public FakeJobRunnerRepository(string? initiatedBy) => _initiatedBy = initiatedBy;

		public Task<RunQueueState?> GetRunQueueStateAsync(Guid runId, CancellationToken cancellationToken) =>
			Task.FromResult<RunQueueState?>(new RunQueueState("running", Paused: false, Blocked: false, BlockedReason: null, InitiatedBy: _initiatedBy));

		public Task<ClaimedJob?> ClaimJobAsync(string workerId, TimeSpan leaseDuration, IReadOnlySet<string> allowedJobTypes, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RenewLeaseAsync(Guid jobId, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> IsCancelRequestedAsync(Guid jobId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> AdvanceStateAsync(Guid jobId, string workerId, string expectedFromState, string toState, string? note, bool clearLease, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> RequeueAtStageAsync(Guid jobId, string workerId, string expectedFromState, string stage, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<IReadOnlyList<RecoveredJob>> RecoverExpiredLeasesAsync(int batchSize, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<bool> ReleaseClaimAsync(Guid jobId, string workerId, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<AuthFailureHaltResult> CheckConsecutiveAuthFailuresAsync(Guid credentialId, int threshold, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task SetUploadStatusAsync(Guid jobId, string uploadStatus, string? detail, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<IReadOnlyList<JobCredentialBinding>> GetJobCredentialBindingsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<JobCredentialBinding>>([]);
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

	// --- helpers ---------------------------------------------------------------

	private static ComplianceContentConfig Config(string refType, string refValue) =>
		new(RepositoryUrl, refType, refValue, PulledCommit: null, PulledBy: null, PulledAt: null,
			CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

	private static (ContentPullJobHandler Handler, JobExecutionContext Context, RecordingEventPublisher Events,
		FakeContentRepository Content, FakeProfileRepository Profiles, FakeProfileControlRepository ProfileControls, FakeCatalogRepository Catalog) Build(
			PowerShellExecutionResult psResult,
			ComplianceContentConfig? config,
			string? initiatedBy = "admin@example.internal")
	{
		FakePowerShellExecutor executor = new(psResult);
		FakeContentRepository content = new(config);
		FakeProfileRepository profiles = new();
		FakeProfileControlRepository profileControls = new();
		FakeCatalogRepository catalog = new();
		FakeJobRunnerRepository jobs = new(initiatedBy);
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });

		ContentPullJobHandler handler = new(executor, content, profiles, profileControls, catalog, jobs, options);

		Guid jobId = Guid.NewGuid();
		Guid runId = Guid.NewGuid();
		ClaimedJob job = new(jobId, runId, "content-pull", TargetId: null, TargetName: null, CredentialId: null,
			Priority: 5, Payload: "{}", AttemptCount: 0, MaxAttempts: 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		return (handler, context, events, content, profiles, profileControls, catalog);
	}

	private static PSObject Success(string commit, params PSObject[] profiles)
	{
		PSObject root = new();
		root.Properties.Add(new PSNoteProperty("Commit", commit));
		root.Properties.Add(new PSNoteProperty("Profiles", profiles));
		return root;
	}

	/// <summary>
	/// Issue #729: same shape as <see cref="Success"/> plus the module's
	/// <c>ContentEntries</c> array -- the raw-manifest/structural-fact rows the
	/// semantic-import pass consumes, independent of the pre-existing
	/// <c>Profiles</c>/<c>Controls</c> shape.
	/// </summary>
	private static PSObject SuccessWithContentEntries(string commit, PSObject[] profiles, params PSObject[] contentEntries)
	{
		PSObject root = Success(commit, profiles);
		root.Properties.Add(new PSNoteProperty("ContentEntries", contentEntries));
		return root;
	}

	private static PSObject ContentEntryObject(string profileKey, string? rawYaml, bool hasControlsDirectory, params string[] controlFileNames)
	{
		PSObject entry = new();
		entry.Properties.Add(new PSNoteProperty("ProfileKey", profileKey));
		entry.Properties.Add(new PSNoteProperty("RawYaml", rawYaml));
		entry.Properties.Add(new PSNoteProperty("HasControlsDirectory", hasControlsDirectory));
		entry.Properties.Add(new PSNoteProperty("HasFilesDirectory", false));
		entry.Properties.Add(new PSNoteProperty("ControlFileNames", controlFileNames));
		return entry;
	}

	private const string ValidVCenterManifest = """
		name: vsphere-8-vcenter-stig-baseline
		title: vCenter STIG
		version: 2.3.0
		inputs:
		  - name: vcenter_host
		    type: string
		    required: true
		""";

	private static PSObject ProfileObject(string? profileKey, string? name, string? version)
	{
		PSObject profile = new();
		profile.Properties.Add(new PSNoteProperty("ProfileKey", profileKey));
		profile.Properties.Add(new PSNoteProperty("Name", name));
		profile.Properties.Add(new PSNoteProperty("Version", version));
		return profile;
	}

	private static PSObject ProfileObjectWithControls(string profileKey, string name, params PSObject[] controls)
	{
		PSObject profile = ProfileObject(profileKey, name, version: null);
		profile.Properties.Add(new PSNoteProperty("Controls", controls));
		return profile;
	}

	private static PSObject ControlObject(string? controlId, string? title, string? severity)
	{
		PSObject control = new();
		control.Properties.Add(new PSNoteProperty("ControlId", controlId));
		control.Properties.Add(new PSNoteProperty("Title", title));
		control.Properties.Add(new PSNoteProperty("Severity", severity));
		return control;
	}

	private static PowerShellExecutionResult Ok(params object?[] output) =>
		new(Succeeded: true, output, HadErrors: false, TimedOut: false, FailureReason: null, NativeExitCode: null);

	private static PowerShellExecutionResult Fail(string? reason) =>
		new(Succeeded: false, [], HadErrors: true, TimedOut: false, FailureReason: reason, NativeExitCode: 1);

	// --- tests -----------------------------------------------------------------

	[Fact]
	public async Task Execute_Success_ReplacesProfiles_RecordsSucceededPull_EmitsProgress()
	{
		PSObject output = Success(
			"deadbeefcafe",
			ProfileObject("dod-vsphere-8-esxi-stig", "vSphere 8 ESXi STIG", "1.2"),
			ProfileObject("dod-vsphere-8-vcsa-stig", "vSphere 8 vCSA STIG", "1.0"));
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles, _, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);

		Assert.NotNull(profiles.Replaced);
		Assert.Equal(2, profiles.Replaced!.Count);
		Assert.Collection(profiles.Replaced,
			p => Assert.Equal("dod-vsphere-8-esxi-stig", p.ProfileKey),
			p => Assert.Equal("dod-vsphere-8-vcsa-stig", p.ProfileKey));
		Assert.All(profiles.Replaced, p => Assert.Equal("deadbeefcafe", p.Commit));

		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, pull.Status);
		Assert.Equal("deadbeefcafe", pull.Commit);
		Assert.Null(pull.Note);
		Assert.Equal("admin@example.internal", pull.InitiatedBy);

		(string EventType, Guid? JobId, Guid? RunId, string Payload) progress =
			Assert.Single(events.Events, e => e.EventType == JobEventTypes.RunProgress);
		Assert.Contains("deadbeefcafe", progress.Payload, StringComparison.Ordinal);
		Assert.Contains("\"profile_count\":2", progress.Payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_BranchConfig_LabelsProfilesCurrent()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _) =
		Build(Ok(Success("c1", ProfileObject("p1", "P1", null))), Config(ComplianceContentRefTypes.Branch, "main"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(ProfileStates.Current, Assert.Single(profiles.Replaced!).State);
	}

	[Fact]
	public async Task Execute_TagConfig_LabelsProfilesPinned()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _) =
		Build(Ok(Success("c1", ProfileObject("p1", "P1", null))), Config(ComplianceContentRefTypes.Tag, "v1.2.3"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(ProfileStates.Pinned, Assert.Single(profiles.Replaced!).State);
	}

	/// <summary>Issue #598: a successful pull persists each profile's parsed controls, keyed by that profile's (fake-repository-assigned) id.</summary>
	[Fact]
	public async Task Execute_Success_PersistsControlsPerProfile()
	{
		PSObject output = Success(
			"commitC",
			ProfileObjectWithControls("profile-a", "Profile A",
				ControlObject("V-1001", "First control", "medium"),
				ControlObject("V-1002", "Second control", "high")),
			ProfileObjectWithControls("profile-b", "Profile B",
				ControlObject("V-2001", "Other profile's control", "low")));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, FakeProfileControlRepository profileControls, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal(2, profileControls.ReplacedByProfileId.Count);

		IReadOnlyList<Profile> stored = await profiles.ListAsync(CancellationToken.None);
		Profile profileA = Assert.Single(stored, p => p.ProfileKey == "profile-a");
		Profile profileB = Assert.Single(stored, p => p.ProfileKey == "profile-b");

		IReadOnlyList<ProfileControlUpsert> controlsA = profileControls.ReplacedByProfileId[profileA.Id];
		Assert.Equal(2, controlsA.Count);
		Assert.Contains(controlsA, c => c.ControlId == "V-1001" && c.Title == "First control" && c.Severity == "medium");
		Assert.Contains(controlsA, c => c.ControlId == "V-1002" && c.Title == "Second control" && c.Severity == "high");

		ProfileControlUpsert controlB = Assert.Single(profileControls.ReplacedByProfileId[profileB.Id]);
		Assert.Equal("V-2001", controlB.ControlId);
	}

	/// <summary>Issue #598 AC: a profile with no Controls property (older module output, or genuinely zero controls/*.rb files) persists an empty control set rather than failing the pull.</summary>
	[Fact]
	public async Task Execute_ProfileWithNoControlsProperty_PersistsEmptyControlSet()
	{
		PSObject output = Success("commitD", ProfileObject("no-controls-profile", "No Controls", version: null));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, FakeProfileControlRepository profileControls, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Profile stored = Assert.Single(await profiles.ListAsync(CancellationToken.None));
		Assert.Empty(profileControls.ReplacedByProfileId[stored.Id]);
	}

	/// <summary>Issue #598 AC: a malformed control row (missing ControlId, or a non-PSObject entry) is dropped, not fatal to the pull or to its sibling controls.</summary>
	[Fact]
	public async Task Execute_MalformedControlRows_AreSkipped_WithoutFailingThePull()
	{
		PSObject goodControl = ControlObject("V-3001", "Kept", "critical");
		PSObject blankIdControl = ControlObject(controlId: null, "Dropped: no id", "low");

		PSObject profile = ProfileObject("profile-c", "Profile C", version: null);
		profile.Properties.Add(new PSNoteProperty("Controls", new object?[] { goodControl, blankIdControl, "not-a-psobject" }));

		PSObject output = Success("commitE", profile);

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, FakeProfileControlRepository profileControls, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Profile stored = Assert.Single(await profiles.ListAsync(CancellationToken.None));
		ProfileControlUpsert survivor = Assert.Single(profileControls.ReplacedByProfileId[stored.Id]);
		Assert.Equal("V-3001", survivor.ControlId);
	}

	/// <summary>A control with an empty/blank Title or Severity normalizes to null rather than storing whitespace (mirrors TryParseProfile's Name fallback discipline).</summary>
	[Fact]
	public async Task Execute_ControlWithBlankTitleAndSeverity_NormalizesToNull()
	{
		PSObject profile = ProfileObjectWithControls("profile-d", "Profile D", ControlObject("V-4001", "   ", ""));
		PSObject output = Success("commitF", profile);

		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository _, FakeProfileRepository profiles, FakeProfileControlRepository profileControls, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		await handler.ExecuteAsync(context, CancellationToken.None);

		Profile stored = Assert.Single(await profiles.ListAsync(CancellationToken.None));
		ProfileControlUpsert control = Assert.Single(profileControls.ReplacedByProfileId[stored.Id]);
		Assert.Null(control.Title);
		Assert.Null(control.Severity);
	}

	[Fact]
	public async Task Execute_MissingConfig_FailsWithoutInvokingExecutor_AndRecordsNoPull()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles, _, _) =
		Build(Ok(), config: null);

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Contains("No compliance-content repository is configured", outcome.Note, StringComparison.Ordinal);
		// No config -> no pull history row and no profile mutation (nothing was even attempted).
		Assert.Empty(content.Pulls);
		Assert.Null(profiles.Replaced);
		Assert.Empty(events.Events);
	}

	[Fact]
	public async Task Execute_ExecutorFailure_StillRecordsFailedPull_WithReason()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles, _, _) =
		Build(Fail("git checkout failed: ref not found"), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		// The AC's central promise: a failed pull STILL lands in history.
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Null(pull.Commit);
		Assert.Equal("git checkout failed: ref not found", pull.Note);
		Assert.Null(profiles.Replaced);
		Assert.DoesNotContain(events.Events, e => e.EventType == JobEventTypes.RunProgress);
	}

	[Fact]
	public async Task Execute_ExecutorFailure_WithNoReason_RecordsFallbackNote()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _, _, _) =
		Build(Fail(reason: null), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Contains("no failure reason", pull.Note, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Execute_NoCommitInOutput_RecordsFailedPull_AndSkipsProfileReplace()
	{
		// Executor "succeeded" but produced no PSObject carrying a Commit -> unchanged/no-commit path.
		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events,
			FakeContentRepository content, FakeProfileRepository profiles, _, _) =
		Build(Ok("just a string, not a PSObject"), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		RecordedPull pull = Assert.Single(content.Pulls);
		Assert.Equal(ComplianceContentPullStatuses.Failed, pull.Status);
		Assert.Contains("no commit", pull.Note, StringComparison.Ordinal);
		Assert.Null(profiles.Replaced);
		Assert.DoesNotContain(events.Events, e => e.EventType == JobEventTypes.RunProgress);
	}

	[Fact]
	public async Task Execute_BlankCommit_TreatedAsNoCommit()
	{
		PSObject output = new();
		output.Properties.Add(new PSNoteProperty("Commit", "   "));
		output.Properties.Add(new PSNoteProperty("Profiles", Array.Empty<PSObject>()));

		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, FakeProfileRepository profiles, _, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Failed, outcome.Kind);
		Assert.Equal(ComplianceContentPullStatuses.Failed, Assert.Single(content.Pulls).Status);
		Assert.Null(profiles.Replaced);
	}

	[Fact]
	public async Task Execute_MalformedProfileRows_AreSkipped_WithoutFailingThePull()
	{
		// A missing-key profile and a non-PSObject row must both be dropped; the valid one survives.
		PSObject output = Success(
			"commit9",
			ProfileObject(profileKey: null, "no key", "1.0"), // dropped: blank ProfileKey
			ProfileObject("good-profile", name: null, version: null)); // name defaults to key

		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, FakeProfileRepository profiles, _, _) =
		Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		ProfileUpsert survivor = Assert.Single(profiles.Replaced!);
		Assert.Equal("good-profile", survivor.ProfileKey);
		Assert.Equal("good-profile", survivor.Name); // blank Name falls back to the key
		Assert.Null(survivor.Version);
		Assert.Equal(ComplianceContentPullStatuses.Succeeded, Assert.Single(content.Pulls).Status);
	}

	[Fact]
	public async Task Execute_MalformedProfileInEnumerable_NonPsObjectRowIsSkipped()
	{
		PSObject root = new();
		root.Properties.Add(new PSNoteProperty("Commit", "commitX"));
		root.Properties.Add(new PSNoteProperty("Profiles", new object?[] { "not-a-psobject", ProfileObject("kept", "Kept", "9") }));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, FakeProfileRepository profiles, _, _) =
		Build(Ok(root), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		Assert.Equal("kept", Assert.Single(profiles.Replaced!).ProfileKey);
	}

	[Fact]
	public async Task Execute_ForwardsConfiguredParametersToExecutor()
	{
		FakePowerShellExecutor executor = new(Ok(Success("c1")));
		FakeContentRepository content = new(Config(ComplianceContentRefTypes.Tag, "v2"));
		FakeProfileRepository profiles = new();
		FakeProfileControlRepository profileControls = new();
		FakeCatalogRepository catalog = new();
		FakeJobRunnerRepository jobs = new("admin@example.internal");
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });
		ContentPullJobHandler handler = new(executor, content, profiles, profileControls, catalog, jobs, options);

		ClaimedJob job = new(Guid.NewGuid(), Guid.NewGuid(), "content-pull", null, null, null, 5, "{}", 0, 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.NotNull(executor.LastRequest);
		Assert.Equal("Invoke-WaypointComplianceContentPull", executor.LastRequest!.Command);
		Assert.Equal(PowerShellRequestKind.Command, executor.LastRequest.Kind);
		Assert.Equal(RepositoryUrl, executor.LastRequest.Parameters!["RepositoryUrl"]);
		Assert.Equal(ComplianceContentRefTypes.Tag, executor.LastRequest.Parameters!["RefType"]);
		Assert.Equal("v2", executor.LastRequest.Parameters!["RefValue"]);
		Assert.Equal(ContentPath, executor.LastRequest.Parameters!["ContentPath"]);
	}

	[Fact]
	public async Task Execute_NullInitiatedBy_ResolvesActorToSystem()
	{
		(ContentPullJobHandler handler, JobExecutionContext context, _, FakeContentRepository content, _, _, _) =
		Build(Ok(Success("c1", ProfileObject("p1", "P1", null))), Config(ComplianceContentRefTypes.Branch, "main"), initiatedBy: null);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal("system", Assert.Single(content.Pulls).InitiatedBy);
	}

	[Fact]
	public async Task Execute_NullRunId_ResolvesActorToSystem_WithoutQueryingRunState()
	{
		FakePowerShellExecutor executor = new(Ok(Success("c1", ProfileObject("p1", "P1", null))));
		FakeContentRepository content = new(Config(ComplianceContentRefTypes.Branch, "main"));
		FakeProfileRepository profiles = new();
		FakeProfileControlRepository profileControls = new();
		FakeCatalogRepository catalog = new();
		FakeJobRunnerRepository jobs = new("admin@example.internal");
		IOptions<ComplianceContentOptions> options = Options.Create(new ComplianceContentOptions { ContentPath = ContentPath });
		ContentPullJobHandler handler = new(executor, content, profiles, profileControls, catalog, jobs, options);

		// A job with no run id: ResolveActorAsync short-circuits to "system" without a run-state read.
		ClaimedJob job = new(Guid.NewGuid(), RunId: null, "content-pull", null, null, null, 5, "{}", 0, 1);
		RecordingEventPublisher events = new();
		JobExecutionContext context = new(job, "worker-1", events, jobs, JobShape.Simple);

		await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal("system", Assert.Single(content.Pulls).InitiatedBy);
	}

	// --- issue #729: semantic-import wiring (job claim -> import -> report persisted) ---

	[Fact]
	public async Task Execute_Success_RunsSemanticImport_PersistsReportAndPromotesAcceptedLeaf()
	{
		PSObject profile = ProfileObject("vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", "vCenter STIG", "2.3.0");
		PSObject contentEntry = ContentEntryObject(
			"vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", ValidVCenterManifest, hasControlsDirectory: true, "vcenter_control.rb");
		PSObject output = SuccessWithContentEntries("commitG", [profile], contentEntry);

		(ContentPullJobHandler handler, JobExecutionContext context, RecordingEventPublisher events, _, _, _, FakeCatalogRepository catalog) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);

		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal("commitG", report.SourceCommit);
		Assert.Equal(1, report.AcceptedCount);
		Assert.Equal(0, report.RejectedCount);

		CatalogImportReportEntry entry = Assert.Single(catalog.Entries);
		Assert.Equal(CatalogImportEntryDispositions.Accepted, entry.Disposition);
		Assert.Equal("vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", entry.ProfileKey);
		Assert.NotNull(entry.ExecutionProfileId);

		IReadOnlyList<CatalogDeclaredInput> declaredInputs = await catalog.ListDeclaredInputsAsync(entry.ExecutionProfileId!.Value, CancellationToken.None);
		Assert.Single(declaredInputs, i => i.Name == "vcenter_host");

		(string EventType, Guid? JobId, Guid? RunId, string Payload) progress =
			Assert.Single(events.Events, e => e.EventType == JobEventTypes.RunProgress);
		Assert.Contains("\"catalog_promoted_count\":1", progress.Payload, StringComparison.Ordinal);
		Assert.Contains("1 catalog execution profile(s) promoted", outcome.Note, StringComparison.Ordinal);
	}

	/// <summary>
	/// Issue #729 deliverable: "unknown/new layouts are quarantined with actionable
	/// diagnostics rather than guessed" -- one malformed content entry (an unrecognized
	/// vendor family directory) must be rejected into the report WITHOUT preventing its
	/// sibling entry from being interpreted, reconciled, and promoted. Mirrors the
	/// pre-existing "one bad profile/control row must not fail the whole pull"
	/// discipline this handler already applies one level up (profiles/controls).
	/// </summary>
	[Fact]
	public async Task Execute_OneBadContentEntry_QuarantinesIt_SiblingStillPromotes()
	{
		PSObject goodProfile = ProfileObject("vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", "vCenter STIG", "2.3.0");
		PSObject badProfile = ProfileObject("totally-unrecognized-shape", "Bad", null);

		PSObject goodEntry = ContentEntryObject(
			"vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter", ValidVCenterManifest, hasControlsDirectory: true, "vcenter_control.rb");
		PSObject badEntry = ContentEntryObject("totally-unrecognized-shape", ValidVCenterManifest, hasControlsDirectory: true, "control.rb");

		PSObject output = SuccessWithContentEntries("commitH", [goodProfile, badProfile], goodEntry, badEntry);

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, _, _, FakeCatalogRepository catalog) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);

		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal(1, report.AcceptedCount);
		Assert.Equal(1, report.RejectedCount);

		Assert.Contains(catalog.Entries, e => e.Disposition == CatalogImportEntryDispositions.Accepted && e.ProfileKey == "vsphere/8.0.3/v2r3-stig/inspec/baseline/vcenter");
		CatalogImportReportEntry rejected = Assert.Single(catalog.Entries, e => e.Disposition == CatalogImportEntryDispositions.Rejected);
		Assert.Equal("totally-unrecognized-shape", rejected.ProfileKey);
		Assert.NotNull(rejected.Reason);
	}

	[Fact]
	public async Task Execute_NoContentEntries_StillSucceeds_RecordsEmptyReport()
	{
		// A module build that has not yet added ContentEntries (or a checkout with no
		// discoverable inspec.yml at all) must not fail the pull -- semantic import over
		// zero entries is a legitimate (if unhelpful) empty report.
		PSObject output = Success("commitI", ProfileObject("p1", "P1", null));

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, _, _, FakeCatalogRepository catalog) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		CatalogImportReport report = Assert.Single(catalog.Reports);
		Assert.Equal(0, report.AcceptedCount);
		Assert.Equal(0, report.WarningCount);
		Assert.Equal(0, report.RejectedCount);
		Assert.Empty(catalog.Entries);
	}

	[Fact]
	public async Task Execute_AggregateCandidate_IsAcceptedButNeverPromoted()
	{
		// The baseline directory itself (no object-kind/service leaf segment) is the
		// vSphere aggregate parent -- accepted by semantic import (it is a legitimate,
		// if non-executable, classification) but never promoted into a catalog
		// execution profile (issue #729 AC "aggregate ... profiles cannot be selected
		// for execution").
		PSObject profile = ProfileObject("vsphere/8.0.3/v2r3-stig/inspec/baseline", "vSphere (aggregate)", null);
		PSObject contentEntry = ContentEntryObject("vsphere/8.0.3/v2r3-stig/inspec/baseline", ValidVCenterManifest, hasControlsDirectory: false);
		PSObject output = SuccessWithContentEntries("commitJ", [profile], contentEntry);

		(ContentPullJobHandler handler, JobExecutionContext context, _, _, _, _, FakeCatalogRepository catalog) =
			Build(Ok(output), Config(ComplianceContentRefTypes.Branch, "main"));

		JobExecutionOutcome outcome = await handler.ExecuteAsync(context, CancellationToken.None);

		Assert.Equal(JobOutcomeKind.Succeeded, outcome.Kind);
		CatalogImportReportEntry entry = Assert.Single(catalog.Entries);
		Assert.Equal(CatalogImportEntryDispositions.Accepted, entry.Disposition);
		Assert.Null(entry.ExecutionProfileId);
		Assert.Contains("aggregate", entry.Reason, StringComparison.OrdinalIgnoreCase);
	}
}
