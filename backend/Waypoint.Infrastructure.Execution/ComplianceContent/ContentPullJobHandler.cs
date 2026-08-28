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

using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;
using Waypoint.Core.PowerShell;
using Waypoint.Infrastructure.PowerShell;

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
/// accepted executable-leaf candidate whose bounded <c>inspec check</c> genuinely ran
/// and passed is promoted into the migration 0050 catalog tables plus its declared
/// inputs -- additive ingestion only (ADR-0022): promotion upserts by natural key and
/// never mutates an already-active execution profile's identity. An accepted candidate
/// that failed (or never ran) its <c>inspec check</c> is quarantined with the check's
/// own diagnostics instead of promoted (issue #729 remainder deliverable 3, fail
/// closed; sibling candidates are unaffected -- per-entry containment). This pass never
/// fails the overall pull outcome: a semantic-import or inspec-check problem is
/// captured as a rejected/warning report entry, exactly like the pre-existing
/// per-profile/per-control parsing tolerance below.
/// </summary>
public sealed class ContentPullJobHandler : IJobHandler
{
	private const string SyncCommand = "Sync-WaypointComplianceContentTree";
	private const string EntriesCommand = "Get-WaypointComplianceContentEntries";

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
	private readonly IContentRevisionStager _revisionStager;

	public ContentPullJobHandler(
		IPowerShellExecutor executor,
		IComplianceContentRepository content,
		IProfileRepository profiles,
		IProfileControlRepository profileControls,
		ICatalogRepository catalog,
		IJobRunnerRepository jobs,
		IOptions<ComplianceContentOptions> options,
		IContentRevisionStager revisionStager)
	{
		ArgumentNullException.ThrowIfNull(executor);
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(profileControls);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(revisionStager);

		_executor = executor;
		_content = content;
		_profiles = profiles;
		_profileControls = profileControls;
		_catalog = catalog;
		_jobs = jobs;
		_options = options;
		_revisionStager = revisionStager;
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

		// Issue #993: phase 1 is a small, content-size-INDEPENDENT invocation (git
		// clone/fetch/checkout + directory enumeration, no `inspec check` at all) --
		// bounded by ComplianceContentOptions.ContentSyncTimeout, not the fixed
		// 00:30:00 PowerShellOptions.DefaultInvocationTimeout that used to also have to
		// cover every leaf's check on top of this.
		Dictionary<string, object?> syncParameters = new(StringComparer.Ordinal)
		{
			["RepositoryUrl"] = config.RepositoryUrl,
			["RefType"] = config.RefType,
			["RefValue"] = config.RefValue,
			["ContentPath"] = _options.Value.ContentPath,
		};

		PowerShellRequest syncRequest = new(
			SyncCommand, PowerShellRequestKind.Command, syncParameters, context.Job.Id, context.Job.RunId,
			Timeout: _options.Value.ContentSyncTimeout);
		PowerShellExecutionResult syncResult = await _executor.ExecuteAsync(syncRequest, cancellationToken).ConfigureAwait(false);

		if (!syncResult.Succeeded)
		{
			string note = syncResult.FailureReason ?? "content-pull sync invocation failed with no failure reason.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		(string? commit, IReadOnlyList<ProfileUpsert> discoveredProfiles, IReadOnlyList<ProfileDirectoryEntry> profileDirectories) =
			ParseSyncOutput(syncResult.Output, config);
		if (commit is null)
		{
			const string note = "content-pull sync invocation returned no commit.";
			await _content.RecordPullAsync(
				context.Job.Id, config.RefType, config.RefValue, commit: null,
				ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
			return JobExecutionOutcome.Failed(note);
		}

		// Issue #993: phase 2 runs the bounded per-leaf `inspec check` (issue #989's
		// per-unit protection, unchanged) in CHUNKS of ComplianceContentOptions
		// .ContentPullChunkSize leaves per invocation, each sized to exactly that
		// chunk's own worst case (chunk size x per-check timeout + fixed overhead) --
		// never the whole tree's. The cancellation check between chunks is what makes a
		// run-abort (ADR-0008) honored PROMPTLY: a stop request simply skips starting
		// the next chunk rather than trying to interrupt one already in flight, so the
		// "ignored Stop() for 5s; runspace poisoned" failure mode this issue closes
		// cannot recur here -- each chunk call is its own bounded, independently
		// completing PowerShellExecutor.ExecuteAsync invocation.
		List<VendorContentEntry> contentEntries = new(profileDirectories.Count);
		Dictionary<string, IReadOnlyList<ProfileControlUpsert>> controlsByProfileKey = new(StringComparer.Ordinal);
		int chunkSize = Math.Max(1, _options.Value.ContentPullChunkSize);
		TimeSpan chunkTimeout = TimeSpan.FromSeconds(
			(chunkSize * (double)_options.Value.InspecCheckTimeoutSeconds) + _options.Value.ContentPullChunkOverheadSeconds);

		for (int offset = 0; offset < profileDirectories.Count; offset += chunkSize)
		{
			cancellationToken.ThrowIfCancellationRequested();

			IReadOnlyList<ProfileDirectoryEntry> chunk = profileDirectories.Skip(offset).Take(chunkSize).ToList();
			Dictionary<string, object?> profileKeysByDirectory = new(StringComparer.Ordinal);
			foreach (ProfileDirectoryEntry entry in chunk)
			{
				profileKeysByDirectory[entry.ProfileDirectory] = entry.ProfileKey;
			}

			Dictionary<string, object?> entriesParameters = new(StringComparer.Ordinal)
			{
				["ProfileDirectories"] = chunk.Select(p => p.ProfileDirectory).ToArray(),
				["ProfileKeysByDirectory"] = profileKeysByDirectory,
				["InspecCheckTimeoutSeconds"] = _options.Value.InspecCheckTimeoutSeconds,
			};

			PowerShellRequest entriesRequest = new(
				EntriesCommand, PowerShellRequestKind.Command, entriesParameters, context.Job.Id, context.Job.RunId,
				Timeout: chunkTimeout);
			PowerShellExecutionResult entriesResult = await _executor.ExecuteAsync(entriesRequest, cancellationToken).ConfigureAwait(false);

			if (!entriesResult.Succeeded)
			{
				// Issue #993 AC 3: a genuine overrun (a chunk whose leaves collectively
				// exceed even this scaled bound -- e.g. a pathological profile that
				// somehow evades #989's own per-check bound) is an honest, actionable job
				// failure, not a silent 0-profile discard: nothing has been staged yet
				// (profiles/controls/catalog all still commit atomically below), so this
				// return leaves the prior pull's staged state completely untouched.
				string note = entriesResult.FailureReason
					?? $"content-pull entries invocation failed with no failure reason (chunk starting at offset {offset}).";
				await _content.RecordPullAsync(
					context.Job.Id, config.RefType, config.RefValue, commit: null,
					ComplianceContentPullStatuses.Failed, note, actor, cancellationToken).ConfigureAwait(false);
				return JobExecutionOutcome.Failed(note);
			}

			foreach (object? item in entriesResult.Output)
			{
				VendorContentEntry? entry = TryParseContentEntry(item);
				if (entry is null)
				{
					continue;
				}

				contentEntries.Add(entry);
				controlsByProfileKey[entry.ProfileKey] = TryParseControls(item);
			}
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

		(int promotedCount, string contentDigest) = await RunSemanticImportAsync(commit, contentEntries, cancellationToken).ConfigureAwait(false);

		// Issue #731: stage an immutable digest-addressed snapshot of the working tree
		// content-pull just checked out. This happens AFTER every prior step (git
		// checkout, profile/control replace, semantic import/promotion) succeeded, and
		// staging itself never touches any existing active baseline -- a failure in any
		// EARLIER step already returned Failed above without reaching here, and this
		// step's own failure (e.g. a filesystem error) throws out of ExecuteAsync
		// entirely rather than recording a success, so neither path can leave a
		// partially-staged revision recorded as staged.
		ContentRevision revision = await _revisionStager
			.StageAsync(_options.Value.ContentPath, commit, contentDigest, cancellationToken)
			.ConfigureAwait(false);

		await _content.RecordPullAsync(
			context.Job.Id, config.RefType, config.RefValue, commit,
			ComplianceContentPullStatuses.Succeeded, note: null, actor, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new
		{
			commit,
			profile_count = discoveredProfiles.Count,
			catalog_promoted_count = promotedCount,
			staged_revision_id = revision.Id,
		});
		await context.Events
			.EmitAsync(JobEventTypes.RunProgress, null, context.Job.RunId, progressPayload, cancellationToken)
			.ConfigureAwait(false);

		return JobExecutionOutcome.Succeeded(
			$"Pulled '{config.RefValue}' at {commit}; {discoveredProfiles.Count} profile(s) found; {promotedCount} catalog execution profile(s) promoted; staged revision {revision.Id}.");
	}

	/// <summary>
	/// Runs the semantic-import pipeline (interpretation, closed-vocabulary
	/// reconciliation, bounded structure validation already folded into
	/// <see cref="SemanticImportReconciler"/>) over this pull's content entries,
	/// persists the resulting <see cref="SemanticImportReport"/> (migration 0051), and
	/// promotes every accepted executable-leaf candidate into the catalog. Returns the
	/// number of candidates successfully promoted plus the report's deterministic
	/// <see cref="SemanticImportReport.SourceDigest"/> (issue #731 reuses this same
	/// digest as the staged <see cref="ContentRevision.ContentDigest"/> -- one
	/// deterministic whole-import digest, not two independently-computed ones that
	/// could silently diverge). Never throws for a bad INPUT -- interpretation/
	/// reconciliation already quarantine malformed entries into the report's own
	/// rejected list (issue #729's whole design point); this method's job is purely to
	/// persist that report and act on its accepted list.
	/// </summary>
	private async Task<(int PromotedCount, string ContentDigest)> RunSemanticImportAsync(string sourceCommit, IReadOnlyList<VendorContentEntry> contentEntries, CancellationToken cancellationToken)
	{
		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret(contentEntries);
		SemanticImportReport report = SemanticImportReconciler.Reconcile(sourceCommit, interpretation, contentEntries);

		// Issue #729 deliverable 3: the reconciler's structural checks are I/O-free and
		// already ran above; the bounded `inspec check` result was computed in
		// PowerShell (Test-WaypointInspecCheck) while the checkout's real directory
		// still existed and is carried on each VendorContentEntry. First-writer-wins
		// dedup mirrors SemanticImportReconciler's own defensive lookup build -- a
		// duplicate ProfileKey in the raw entry list must not throw here either.
		Dictionary<string, VendorContentEntry> entriesByKey = new(StringComparer.Ordinal);
		foreach (VendorContentEntry entry in contentEntries)
		{
			entriesByKey.TryAdd(entry.ProfileKey, entry);
		}

		CatalogImportReport persistedReport = await _catalog.RecordImportReportAsync(
			report.SourceCommit, report.SourceDigest, report.Accepted.Count, report.Warnings.Count, report.Rejected.Count, cancellationToken)
			.ConfigureAwait(false);

		int promotedCount = 0;
		foreach (SemanticImportAccepted accepted in report.Accepted)
		{
			Guid? executionProfileId = null;
			string? rejectionReason;

			// Fail closed (issue #729 AC): an executable-leaf candidate is promoted only
			// when its bounded `inspec check` genuinely ran and passed. A candidate whose
			// check never ran (no inspec binary staged, or an aggregate that was never
			// checked at all) or that ran and failed is quarantined here with the check's
			// own diagnostics -- structurally valid enough to survive reconciliation, but
			// not structurally valid enough to run. This mirrors, at the execution-boundary
			// layer, the same "quarantine with actionable diagnostics rather than guessed"
			// discipline reconciliation already applies at the I/O-free layer. A sibling
			// candidate's check outcome never affects this one (per-entry containment).
			bool hasInspecEntry = entriesByKey.TryGetValue(accepted.Candidate.ProfileKey, out VendorContentEntry? contentEntry);
			if (accepted.Candidate.IsExecutableLeaf && (!hasInspecEntry || !contentEntry!.InspecCheckRan || !contentEntry.InspecCheckPassed))
			{
				string detail = hasInspecEntry && contentEntry is not null && !string.IsNullOrWhiteSpace(contentEntry.InspecCheckDetail)
					? contentEntry.InspecCheckDetail!
					: "inspec check did not run for this candidate";
				rejectionReason = $"inspec check failed structure validation, quarantined rather than promoted: {Truncate(detail)}";
			}
			else
			{
				// PromoteCandidateAsync itself rejects a non-executable-leaf (aggregate)
				// candidate with an actionable reason (issue #729 AC "aggregate ...
				// profiles cannot be selected for execution") -- called unconditionally
				// here rather than pre-filtering on IsExecutableLeaf so that reason always
				// lands on the persisted report entry, not just in a code comment.
				CatalogPromotionRequest promotionRequest = BuildPromotionRequest(accepted.Candidate);
				CatalogPromotionOutcome outcome = await _catalog.PromoteCandidateAsync(accepted.Candidate, promotionRequest, cancellationToken).ConfigureAwait(false);
				executionProfileId = outcome.ExecutionProfileId;
				rejectionReason = outcome.RejectionReason;
			}

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

		return (promotedCount, report.SourceDigest);
	}

	/// <summary>
	/// Builds the catalog-authored classification facts (vendor natural key, product/
	/// content-release display names, report group, output kind) a candidate's own
	/// evidence does not carry -- see <see cref="CatalogPromotionRequest"/>'s doc
	/// comment. Report-group priority/key and output kind follow
	/// docs/compliance-parity.md's documented table (NSX STIG 1 / VCSA STIG 2 / vCenter
	/// STIG 3 / ESXi STIG 4 / VM STIG 5 / every SRG 6; STIG emits hdf_ckl, SRG emits hdf
	/// -- SRGs are never CKL/upload-eligible, ADR-0022).
	///
	/// Issue #1007: <see cref="CatalogPromotionRequest.Vendor"/> is the
	/// <c>catalog_products.vendor</c> NATURAL-KEY value (<see cref="CatalogVendors.VMware"/>,
	/// the literal the seed migrations write), never the human-readable display string --
	/// passing the display name here previously created a second <c>catalog_products</c>
	/// row (and therefore an entire parallel product/version/component tree) under the
	/// same <c>product_key</c> but a different <c>vendor</c> string, defeating the
	/// <c>catalog_products_vendor_key_unique</c> upsert this promotion path relies on to
	/// attach to the seeded catalog instead of duplicating it. The display string is kept
	/// ONLY for <see cref="CatalogPromotionRequest.ProductDisplayName"/>, which is cosmetic
	/// and never part of any natural key.
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
			Vendor: CatalogVendors.VMware,
			ProductDisplayName: vendorDisplayName,
			ProductVersionDisplayName: candidate.ProductVersionKey,
			ContentReleaseDisplayName: $"{candidate.Kind} {candidate.ProductVersionKey}",
			ReportGroupKey: groupKey,
			ReportGroupDisplayName: groupDisplayName,
			ReportGroupPriority: priority,
			OutputKind: isStig ? CatalogOutputKinds.HdfAndCkl : CatalogOutputKinds.Hdf);
	}

	/// <summary>
	/// One profile directory discovered by the phase-1 sync call: <see cref="ProfileKey"/>
	/// is issue #617's content-root-relative identity, <see cref="ProfileDirectory"/> is
	/// the real absolute directory phase 2's chunked
	/// <c>Get-WaypointComplianceContentEntries</c> calls need to run the bounded
	/// <c>inspec check</c> and read manifest/control files -- carried across the two
	/// invocations explicitly (rather than recomputed) because it is the sync call's own
	/// filesystem walk that discovered it.
	/// </summary>
	private sealed record ProfileDirectoryEntry(string ProfileKey, string ProfileDirectory);

	/// <summary>
	/// Every profile discovered by a successful pull is labeled
	/// <see cref="ProfileStates.Pinned"/> when the config tracks a tag,
	/// <see cref="ProfileStates.Current"/> when it tracks a branch (issue #40 AC
	/// "current / update pending / pinned"). <see cref="ProfileStates.UpdatePending"/>
	/// is not produced by this handler -- it describes a profile whose recorded commit
	/// predates the latest available upstream commit, a comparison this slice's
	/// GET /compliance-content/check (part of the same PR's API surface) computes by
	/// diffing against upstream without mutating stored rows.
	///
	/// Issue #993: this now parses <c>Sync-WaypointComplianceContentTree</c>'s output
	/// only (Commit + Profiles, no ContentEntries/Controls -- those come from phase 2's
	/// chunked <c>Get-WaypointComplianceContentEntries</c> calls instead). Each parsed
	/// profile's real directory is returned alongside its <see cref="ProfileUpsert"/> so
	/// the caller can drive phase 2 without recomputing paths from profile_key.
	/// </summary>
	private static (string? Commit, IReadOnlyList<ProfileUpsert> Profiles, IReadOnlyList<ProfileDirectoryEntry> ProfileDirectories)
		ParseSyncOutput(IReadOnlyList<object?> output, ComplianceContentConfig config)
	{
		string state = config.RefType == ComplianceContentRefTypes.Tag ? ProfileStates.Pinned : ProfileStates.Current;

		foreach (object? item in output)
		{
			if (item is not System.Management.Automation.PSObject psObject)
			{
				continue;
			}

			string? commit = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["Commit"]?.Value);
			if (string.IsNullOrWhiteSpace(commit))
			{
				continue;
			}

			List<ProfileUpsert> profiles = [];
			List<ProfileDirectoryEntry> directories = [];
			foreach (object? rawProfile in PowerShellValueUnwrap.UnwrapEach(psObject.Properties["Profiles"]?.Value))
			{
				ProfileUpsert? parsed = TryParseProfile(rawProfile, commit, state);
				if (parsed is null)
				{
					continue;
				}

				profiles.Add(parsed);

				if (rawProfile is System.Management.Automation.PSObject profileObject)
				{
					string? profileDirectory = PowerShellValueUnwrap.UnwrapAs<string>(profileObject.Properties["_ProfileDirectory"]?.Value);
					if (!string.IsNullOrWhiteSpace(profileDirectory))
					{
						directories.Add(new ProfileDirectoryEntry(parsed.ProfileKey, profileDirectory));
					}
				}
			}

			return (commit, profiles, directories);
		}

		return (null, [], []);
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

		string? profileKey = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["ProfileKey"]?.Value);
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			return null;
		}

		string? rawYaml = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["RawYaml"]?.Value);
		bool hasControlsDirectory = PowerShellValueUnwrap.Unwrap(psObject.Properties["HasControlsDirectory"]?.Value) is true;
		bool hasFilesDirectory = PowerShellValueUnwrap.Unwrap(psObject.Properties["HasFilesDirectory"]?.Value) is true;
		bool inspecCheckRan = PowerShellValueUnwrap.Unwrap(psObject.Properties["InspecCheckRan"]?.Value) is true;
		bool inspecCheckPassed = PowerShellValueUnwrap.Unwrap(psObject.Properties["InspecCheckPassed"]?.Value) is true;
		string? inspecCheckDetail = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["InspecCheckDetail"]?.Value);

		List<string> controlFileNames = [];
		foreach (object? rawName in PowerShellValueUnwrap.UnwrapEach(psObject.Properties["ControlFileNames"]?.Value))
		{
			if (rawName is string name && !string.IsNullOrWhiteSpace(name))
			{
				controlFileNames.Add(name);
			}
		}

		return new VendorContentEntry(
			profileKey, rawYaml, hasControlsDirectory, hasFilesDirectory, controlFileNames,
			inspecCheckRan, inspecCheckPassed, inspecCheckDetail);
	}

	private static ProfileUpsert? TryParseProfile(object? item, string commit, string state)
	{
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return null;
		}

		string? profileKey = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["ProfileKey"]?.Value);
		if (string.IsNullOrWhiteSpace(profileKey))
		{
			// One malformed profile row must not fail the whole pull -- same
			// "individual failures don't halt the batch" principle
			// CatalogIndexJobHandler.TryParseArtifact applies.
			return null;
		}

		string? name = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["Name"]?.Value);
		string? version = PowerShellValueUnwrap.UnwrapAs<string>(psObject.Properties["Version"]?.Value);
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
		if (item is not System.Management.Automation.PSObject psObject)
		{
			return controls;
		}

		foreach (object? rawControl in PowerShellValueUnwrap.UnwrapEach(psObject.Properties["Controls"]?.Value))
		{
			if (rawControl is not System.Management.Automation.PSObject controlObject)
			{
				// One malformed control row must not fail the whole pull -- same
				// "individual failures don't halt the batch" principle as profile rows.
				continue;
			}

			string? controlId = PowerShellValueUnwrap.UnwrapAs<string>(controlObject.Properties["ControlId"]?.Value);
			if (string.IsNullOrWhiteSpace(controlId))
			{
				continue;
			}

			string? title = PowerShellValueUnwrap.UnwrapAs<string>(controlObject.Properties["Title"]?.Value);
			string? severity = PowerShellValueUnwrap.UnwrapAs<string>(controlObject.Properties["Severity"]?.Value);
			controls.Add(new ProfileControlUpsert(
				controlId,
				string.IsNullOrWhiteSpace(title) ? null : title,
				string.IsNullOrWhiteSpace(severity) ? null : severity));
		}

		return controls;
	}

	/// <summary>
	/// Bounds a persisted rejection reason's length (issue #729 AC "bounded runner
	/// work" extends to what gets stored, not just what gets executed) -- an
	/// <c>inspec check</c> JSON diagnostic can be large; <see cref="CatalogImportReportEntry.Reason"/>
	/// is an operator-facing diagnostic column, not a full artifact store.
	/// </summary>
	private static string Truncate(string text) => text.Length <= 1000 ? text : text[..1000] + "... [truncated]";

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
