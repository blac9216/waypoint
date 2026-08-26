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

using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// The <c>content-pull</c> <see cref="JobShape.Simple"/> job handler (issue #40, ADR-0017:
/// compliance-runner executes content-pull/content-import). Reads the singleton
/// <c>compliance_content</c> config (repository/ref), clones or fetches the working
/// tree at <see cref="ComplianceContentOptions.ContentPath"/>, checks out the
/// configured ref, and replaces the <c>profiles</c> inventory with what it finds --
/// mirroring <see cref="Waypoint.Infrastructure.Catalog.CatalogIndexJobHandler"/>'s
/// "not payload-driven; everything needed is configuration or resolved at execution
/// time" shape. No credential is threaded through this slice (see the PowerShell
/// module's doc comment) -- a private/token-gated content source is out of scope.
///
/// Every attempt -- success or failure -- is recorded via
/// <see cref="IComplianceContentRepository.RecordPullAsync"/> so pull history
/// (issue #40 AC "who/when/commit") always reflects what actually happened, including
/// a failed run, rather than only successes.
///
/// Issue #729 (epic #726 Wave 1 remainder): a successful pull additionally runs the
/// validated <see cref="VendorHierarchyInterpreter"/>/<see cref="SemanticImportReconciler"/>
/// pipeline (PR #823) over the same checkout's <c>ContentEntries</c> (raw inspec.yml
/// text plus controls/files structural facts, added to the PowerShell module's output
/// alongside the pre-existing <c>Profiles</c>/<c>Controls</c> shape this handler already
/// consumes for <c>profiles</c>/<c>profile_controls</c> -- issue #598's job-engine
/// boundary/stage-marker/lease/cancellation contract, ADR-0013/0014, is unchanged; this
/// is additional work inside the SAME job attempt, not a new job type or stage). The
/// resulting <see cref="SemanticImportReport"/> is persisted (migration 0051) and every
/// accepted executable-leaf candidate is promoted into the migration 0050 catalog
/// tables plus its declared inputs -- additive ingestion only (ADR-0022): promotion
/// upserts by natural key and never mutates an already-active execution profile's
/// identity. This pass never fails the overall pull outcome: a semantic-import problem
/// is captured as a rejected/warning report entry, exactly like the pre-existing
/// per-profile/per-control parsing tolerance below.
/// </summary>
public sealed class ContentPullJobHandler : IJobHandler
{
	private const string InvocationCommand = "Invoke-WaypointComplianceContentPull";

	/// <summary>
	/// Classification facts issue #729's interpreter needs but the raw import evidence
	/// cannot supply on its own (docs/compliance-parity.md's catalog-authored
	/// vendor/kind naming) -- kept as one small closed table here (not the importer,
	/// which only proves shape/vocabulary) exactly like <see cref="CatalogPromotionRequest"/>'s
	/// doc comment describes.
	/// </summary>
	private static readonly Dictionary<string, string> VendorDisplayNames = new(StringComparer.OrdinalIgnoreCase)
	{
		["vsphere"] = "VMware vSphere",
		["vcsa"] = "VMware vCenter Server Appliance",
		["nsx"] = "VMware NSX",
		["photon"] = "VMware Photon OS",
		["aria-operations"] = "VMware Aria Operations",
		["aria-automation"] = "VMware Aria Automation",
		["aria-suite-lifecycle"] = "VMware Aria Suite Lifecycle",
		["vidm"] = "VMware Workspace ONE Access",
	};

	private readonly IPowerShellExecutor _executor;
	private readonly IComplianceContentRepository _content;
	private readonly IProfileRepository _profiles;
	private readonly IProfileControlRepository _profileControls;
	private readonly ICatalogRepository _catalog;
	private readonly IJobRunnerRepository _jobs;
	private readonly IOptions<ComplianceContentOptions> _options;

	public ContentPullJobHandler(
		IPowerShellExecutor executor,
		IComplianceContentRepository content,
		IProfileRepository profiles,
		IProfileControlRepository profileControls,
		ICatalogRepository catalog,
		IJobRunnerRepository jobs,
		IOptions<ComplianceContentOptions> options)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(profileControls);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(options);

		_executor = executor;
		_content = content;
		_profiles = profiles;
		_profileControls = profileControls;
		_catalog = catalog;
		_jobs = jobs;
		_options = options;
	}

	public string JobType => "content-pull";

	public async Task<JobExecutionOutcome> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		ComplianceContentConfig? config = await _content.GetConfigAsync(cancellationToken).ConfigureAwait(false);
		if (config is null)
		{
			return JobExecutionOutcome.Failed("No compliance-content repository is configured (PUT /compliance-content first).");
		}

		string actor = await ResolveActorAsync(context.Job.RunId, cancellationToken).ConfigureAwait(false);

		Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
		{
			["RepositoryUrl"] = config.RepositoryUrl,
			["RefType"] = config.RefType,
			["RefValue"] = config.RefValue,
			["ContentPath"] = _options.Value.ContentPath,
		};

		PowerShellRequest request = new(InvocationCommand, PowerShellRequestKind.Command, parameters, context.Job.Id, context.Job.RunId);
		PowerShellExecutionResult result = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

		if (!result.Succeeded)
		{
			string note = result.FailureReason ?? "content-pull invocation failed with no failure reason.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		(string? commit, IReadOnlyList<ProfileUpsert> discoveredProfiles, IReadOnlyDictionary<string, IReadOnlyList<ProfileControlUpsert>> controlsByProfileKey, IReadOnlyList<VendorContentEntry> contentEntries) =
			ParseOutput(result.Output, config);
		if (commit is null)
		{
			const string note = "content-pull invocation returned no commit.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		await _profiles.ReplaceAllAsync(discoveredProfiles, cancellationToken).ConfigureAwait(false);

		// Controls are keyed to the profile's surrogate id, which ReplaceAllAsync just
		// assigned (or preserved, for an already-existing profile_key) -- re-read the
		// inventory rather than threading ids back out of the upsert, mirroring how
		// this handler already treats the profile list as the source of truth after a
		// replace. One profile's control-parse failure (empty Controls) must not touch
		// any other profile's already-stored controls -- ReplaceForProfileAsync is
		// scoped per profile_id for exactly this reason.
		IReadOnlyList<Profile> storedProfiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
		foreach (Profile profile in storedProfiles)
		{
			if (controlsByProfileKey.TryGetValue(profile.ProfileKey, out IReadOnlyList<ProfileControlUpsert>? controls))
			{
				await _profileControls.ReplaceForProfileAsync(profile.Id, controls, cancellationToken).ConfigureAwait(false);
			}
		}

		int promotedCount = await RunSemanticImportAsync(commit, contentEntries, cancellationToken).ConfigureAwait(false);

		await _content.RecordPullAsync(
			context.Job.Id, config.RefType, config.RefValue, commit,
			ComplianceContentPullStatuses.Succeeded, note: null, actor, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new { commit, profile_count = discoveredProfiles.Count, catalog_promoted_count = promotedCount });
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded($"Pulled '{config.RefValue}' at {commit}; {discoveredProfiles.Count} profile(s) found; {promotedCount} catalog execution profile(s) promoted.");
	}

	/// <summary>
	/// Runs the semantic-import pipeline (interpretation, closed-vocabulary
	/// reconciliation, bounded structure validation already folded into
	/// <see cref="SemanticImportReconciler"/>) over this pull's content entries,
	/// persists the resulting <see cref="SemanticImportReport"/> (migration 0051), and
	/// promotes every accepted executable-leaf candidate into the catalog. Returns the
	/// number of candidates successfully promoted. Never throws for a bad INPUT --
	/// interpretation/reconciliation already quarantine malformed entries into the
	/// report's own rejected list (issue #729's whole design point); this method's job
	/// is purely to persist that report and act on its accepted list.
	/// </summary>
	private async Task<int> RunSemanticImportAsync(string sourceCommit, IReadOnlyList<VendorContentEntry> contentEntries, CancellationToken cancellationToken)
	{
		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret(contentEntries);
		SemanticImportReport report = SemanticImportReconciler.Reconcile(sourceCommit, interpretation, contentEntries);

		CatalogImportReport persistedReport = await _catalog.RecordImportReportAsync(
			report.SourceCommit, report.SourceDigest, report.Accepted.Count, report.Warnings.Count, report.Rejected.Count, cancellationToken)
			.ConfigureAwait(false);

		int promotedCount = 0;
		foreach (SemanticImportAccepted accepted in report.Accepted)
		{
			// PromoteCandidateAsync itself rejects a non-executable-leaf (aggregate)
			// candidate with an actionable reason (issue #729 AC "aggregate ...
			// profiles cannot be selected for execution") -- called unconditionally
			// here rather than pre-filtering on IsExecutableLeaf so that reason always
			// lands on the persisted report entry, not just in a code comment.
			CatalogPromotionRequest promotionRequest = BuildPromotionRequest(accepted.Candidate);
			CatalogPromotionOutcome outcome = await _catalog.PromoteCandidateAsync(accepted.Candidate, promotionRequest, cancellationToken).ConfigureAwait(false);
			Guid? executionProfileId = outcome.ExecutionProfileId;
			string? rejectionReason = outcome.RejectionReason;
			if (executionProfileId is not null)
			{
				promotedCount++;
			}

			// The report entry's disposition stays "accepted" regardless of whether THIS
			// candidate was itself promoted -- an aggregate candidate (never promoted,
			// rejectionReason explains why) or a promotion-time vocabulary rejection
			// (defence-in-depth only, should not fire in normal operation -- reconciliation
			// already proved the vocabulary before marking this candidate accepted) are
			// both still legitimately "this candidate passed semantic import", distinct
			// from a candidate reconciliation itself rejected (recorded separately below).
			await _catalog.RecordImportReportEntryAsync(
				persistedReport.Id, CatalogImportEntryDispositions.Accepted, accepted.Candidate.ProfileKey, rejectionReason, executionProfileId, cancellationToken).ConfigureAwait(false);
		}

		foreach (SemanticImportWarning warning in report.Warnings)
		{
			await _catalog.RecordImportReportEntryAsync(
				persistedReport.Id, CatalogImportEntryDispositions.Warning, warning.ProfileKey, warning.Message, executionProfileId: null, cancellationToken).ConfigureAwait(false);
		}

		foreach (SemanticImportRejected rejected in report.Rejected)
		{
			await _catalog.RecordImportReportEntryAsync(
				persistedReport.Id, CatalogImportEntryDispositions.Rejected, rejected.ProfileKey, rejected.Reason, executionProfileId: null, cancellationToken).ConfigureAwait(false);
		}

		return promotedCount;
	}

	/// <summary>
	/// Builds the catalog-authored classification facts (vendor display name, product/
	/// content-release display names, report group, output kind) a candidate's own
	/// evidence does not carry -- see <see cref="CatalogPromotionRequest"/>'s doc
	/// comment. Report-group priority/key and output kind follow
	/// docs/compliance-parity.md's documented table (NSX STIG 1 / VCSA STIG 2 / vCenter
	/// STIG 3 / ESXi STIG 4 / VM STIG 5 / every SRG 6; STIG emits hdf_ckl, SRG emits hdf
	/// -- SRGs are never CKL/upload-eligible, ADR-0022).
	/// </summary>
	private static CatalogPromotionRequest BuildPromotionRequest(SemanticCandidate candidate)
	{
		string vendorDisplayName = VendorDisplayNames.TryGetValue(candidate.VendorFamily, out string? name) ? name : candidate.VendorFamily;
		bool isStig = candidate.Kind == CatalogKinds.Stig;

		(string groupKey, string groupDisplayName, int priority) = (candidate.VendorFamily, candidate.SelectorKind) switch
		{
			("nsx", _) when isStig => ("nsx-stig", "NSX STIG", 1),
			("vcsa", _) when isStig => ("vcsa-stig", "VCSA STIG", 2),
			(_, CatalogSelectorKinds.VCenter) when isStig => ("vcenter-stig", "vCenter STIG", 3),
			(_, CatalogSelectorKinds.Esxi) when isStig => ("esxi-stig", "ESXi STIG", 4),
			(_, CatalogSelectorKinds.Vm) when isStig => ("vm-stig", "VM STIG", 5),
			_ => ("srg", "SRG", 6),
		};

		return new CatalogPromotionRequest(
			SourceRevisionKey: "compliance-content",
			Vendor: vendorDisplayName,
			ProductDisplayName: vendorDisplayName,
			ProductVersionDisplayName: candidate.ProductVersionKey,
			ContentReleaseDisplayName: $"{candidate.Kind} {candidate.ProductVersionKey}",
			ReportGroupKey: groupKey,
			ReportGroupDisplayName: groupDisplayName,
			ReportGroupPriority: priority,
			OutputKind: isStig ? CatalogOutputKinds.HdfAndCkl : CatalogOutputKinds.Hdf);
	}

	/// <summary>
	/// Every profile discovered by a successful pull is labeled
	/// <see cref="ProfileStates.Pinned"/> when the config tracks a tag,
	/// <see cref="ProfileStates.Current"/> when it tracks a branch (issue #40 AC
	/// "current / update pending / pinned"). <see cref="ProfileStates.UpdatePending"/>
	/// is not produced by this handler -- it describes a profile whose recorded commit
	/// predates the latest available upstream commit, a comparison this slice's
	/// GET /compliance-content/check (part of the same PR's API surface) computes by
	/// diffing against upstream without mutating stored rows.
	/// </summary>
	private static (string? Commit, IReadOnlyList<ProfileUpsert> Profiles, IReadOnlyDictionary<string, IReadOnlyList<ProfileControlUpsert>> ControlsByProfileKey, IReadOnlyList<VendorContentEntry> ContentEntries)
		ParseOutput(IReadOnlyList<object?> output, ComplianceContentConfig config)
	{
		string state = config.RefType == ComplianceContentRefTypes.Tag ? ProfileStates.Pinned : ProfileStates.Current;

		foreach (object? item in output)
		{
			if (item is not System.Management.Automation.PSObject psObject)
			{
				continue;
			}

			string? commit = psObject.Properties["Commit"]?.Value as string;
			if (string.IsNullOrWhiteSpace(commit))
			{
				continue;
			}

			List<ProfileUpsert> profiles = [];
			Dictionary<string, IReadOnlyList<ProfileControlUpsert>> controlsByProfileKey = new(StringComparer.Ordinal);
			if (psObject.Properties["Profiles"]?.Value is System.Collections.IEnumerable rawProfiles)
			{
				foreach (object? rawProfile in rawProfiles)
				{
					ProfileUpsert? parsed = TryParseProfile(rawProfile, commit, state);
					if (parsed is not null)
					{
						profiles.Add(parsed);
						controlsByProfileKey[parsed.ProfileKey] = TryParseControls(rawProfile);
					}
				}
			}

			List<VendorContentEntry> contentEntries = [];
			if (psObject.Properties["ContentEntries"]?.Value is System.Collections.IEnumerable rawEntries)
			{
				foreach (object? rawEntry in rawEntries)
				{
					VendorContentEntry? parsed = TryParseContentEntry(rawEntry);
					if (parsed is not null)
					{
						contentEntries.Add(parsed);
					}
				}
			}

			return (commit, profiles, controlsByProfileKey, contentEntries);
		}

		return (null, [], new Dictionary<string, IReadOnlyList<ProfileControlUpsert>>(StringComparer.Ordinal), []);
	}

	/// <summary>
	/// Parses one <c>ContentEntries</c> row (issue #729: the module's
	/// <c>Get-WaypointComplianceContentRawManifest</c>/<c>Get-WaypointComplianceContentControlFileNames</c>
	/// additions) into a <see cref="VendorContentEntry"/> for the semantic importer. A
	/// missing/blank ProfileKey drops the row rather than failing the whole pull -- same
	/// "one malformed row must not fail the whole pull" discipline as
	/// <see cref="TryParseProfile"/>.
	/// </summary>
	private static VendorContentEntry? TryParseContentEntry(object? item)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? profileKey = psObject.Properties["ProfileKey"]?.Value as string;
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			return null;
		}

		string? rawYaml = psObject.Properties["RawYaml"]?.Value as string;
		bool hasControlsDirectory = psObject.Properties["HasControlsDirectory"]?.Value is true;
		bool hasFilesDirectory = psObject.Properties["HasFilesDirectory"]?.Value is true;

		List<string> controlFileNames = [];
		if (psObject.Properties["ControlFileNames"]?.Value is System.Collections.IEnumerable rawNames)
		{
			foreach (object? rawName in rawNames)
			{
				if (rawName is string name && !string.IsNullOrWhiteSpace(name))
				{
					controlFileNames.Add(name);
				}
			}
		}

		return new VendorContentEntry(profileKey, rawYaml, hasControlsDirectory, hasFilesDirectory, controlFileNames);
	}

	private static ProfileUpsert? TryParseProfile(object? item, string commit, string state)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? profileKey = psObject.Properties["ProfileKey"]?.Value as string;
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			// One malformed profile row must not fail the whole pull -- same
			// "individual failures don't halt the batch" principle
			// CatalogIndexJobHandler.TryParseArtifact applies.
			return null;
		}

		string? name = psObject.Properties["Name"]?.Value as string;
		string? version = psObject.Properties["Version"]?.Value as string;
		return new ProfileUpsert(profileKey, string.IsNullOrWhiteSpace(name) ? profileKey : name, version, commit, state);
	}

	/// <summary>
	/// Parses a profile row's Controls array. A missing Controls property (e.g. an
	/// older module build, or a profile with no controls/ directory at all) yields an
	/// empty list, not a failure -- issue #598 AC "empty vs. no-content distinction" is
	/// the API's job to surface, not this parser's.
	/// </summary>
	private static List<ProfileControlUpsert> TryParseControls(object? item)
	{
		List<ProfileControlUpsert> controls = [];
		if (item is not System.Management.Automation.PSObject psObject
			|| psObject.Properties["Controls"]?.Value is not System.Collections.IEnumerable rawControls)
		{
			return controls;
		}

		foreach (object? rawControl in rawControls)
		{
			if (rawControl is not System.Management.Automation.PSObject controlObject)
			{
				// One malformed control row must not fail the whole pull -- same
				// "individual failures don't halt the batch" principle as profile rows.
				continue;
			}

			string? controlId = controlObject.Properties["ControlId"]?.Value as string;
			if (string.IsNullOrWhiteSpace(controlId))
			{
				continue;
			}

			string? title = controlObject.Properties["Title"]?.Value as string;
			string? severity = controlObject.Properties["Severity"]?.Value as string;
			controls.Add(new ProfileControlUpsert(
				controlId,
				string.IsNullOrWhiteSpace(title) ? null : title,
				string.IsNullOrWhiteSpace(severity) ? null : severity));
		}

		return controls;
	}

	private async Task<string> ResolveActorAsync(Guid? runId, CancellationToken cancellationToken)
	{
		if (runId is null)
		{
			return "system";
		}

		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId.Value, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}
}
