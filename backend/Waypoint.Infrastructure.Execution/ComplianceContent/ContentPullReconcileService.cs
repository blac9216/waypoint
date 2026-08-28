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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.SemanticImport;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// Issue #1016 (epic #726), owner decision 2026-08-28: performs the completion step a
/// content-pull used to run inline at the end of its own single, long-lived invocation
/// -- profile-control replace, the semantic-import/promotion pipeline (issue #729,
/// unchanged), immutable revision staging (issue #731, unchanged), and pull-history
/// recording -- once every <c>content-check</c> job fanned out for that pull has
/// reached a terminal state (mirrors <c>JobQueueRepository.TryCompleteRunAsync</c>'s own
/// "remaining == 0" run-completion gate, applied here to the check-job subset of one
/// pull instead of a whole run). Called from <c>ContentPullReconcileHostedService</c>
/// (a periodic control-plane sweep, structurally the same shape as
/// <c>RunPurgeFinalizeHostedService</c>) rather than from any runner-executed job, so it
/// runs under the same owner connection every other compliance-content write already
/// uses for cross-cutting completion work.
///
/// Partial-failure semantics: a content-check job that itself lands on a failure
/// terminal (<c>failed</c>/<c>auth-failed</c>/<c>cancelled</c>) never blocks reconcile --
/// its chunk's profiles simply have no recorded check result, so
/// <see cref="RunSemanticImportAsync"/>'s existing fail-closed rule ("no <c>InspecCheckRan</c>
/// evidence" -&gt; quarantine, never promote) already produces the correct honest outcome
/// for them with ZERO new logic: this is exactly issue #729's pre-existing "check never
/// ran" path, now reached via a failed sibling job instead of a chunk invocation that
/// itself returned no entries. The content-pull's own run reaches
/// <c>completed_with_failures</c> through the ordinary run-completion mechanism (any
/// terminal-failure job in a run does that, docs/api-contract.md), which already reports
/// the honest overall outcome -- reconcile does not need its own separate "some checks
/// failed" run-state fork.
/// </summary>
public sealed partial class ContentPullReconcileService
{
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

	private readonly IContentPullCheckFanOutRepository _checkFanOut;
	private readonly IComplianceContentRepository _content;
	private readonly ICatalogRepository _catalog;
	private readonly IJobRunnerRepository _jobs;
	private readonly IContentRevisionStager _revisionStager;
	private readonly IOptions<ComplianceContentOptions> _options;
	private readonly IJobEventPublisher _events;
	private readonly ILogger<ContentPullReconcileService> _logger;

	public ContentPullReconcileService(
		IContentPullCheckFanOutRepository checkFanOut,
		IComplianceContentRepository content,
		ICatalogRepository catalog,
		IJobRunnerRepository jobs,
		IContentRevisionStager revisionStager,
		IOptions<ComplianceContentOptions> options,
		IJobEventPublisher events,
		ILogger<ContentPullReconcileService> logger)
	{
		ArgumentNullException.ThrowIfNull(checkFanOut);
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(jobs);
		ArgumentNullException.ThrowIfNull(revisionStager);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(logger);

		_checkFanOut = checkFanOut;
		_content = content;
		_catalog = catalog;
		_jobs = jobs;
		_revisionStager = revisionStager;
		_options = options;
		_events = events;
		_logger = logger;
	}

	/// <summary>
	/// Attempts to reconcile one content-pull job. Returns <c>false</c> (a no-op) when
	/// its fanned-out check jobs are not all terminal yet -- the caller's sweep simply
	/// tries again next tick, same "row stays selectable until it succeeds" discipline
	/// as <c>RunPurgeFinalizeHostedService</c>.
	/// </summary>
	public async Task<bool> TryReconcileAsync(Guid contentPullJobId, CancellationToken cancellationToken)
	{
		ContentPullCheckReconcileReadiness readiness = await _checkFanOut
			.GetReconcileReadinessAsync(contentPullJobId, cancellationToken).ConfigureAwait(false);
		if (!readiness.AllTerminal)
		{
			return false;
		}

		IReadOnlyList<ContentPullCheckFanOut> fanOuts = await _checkFanOut
			.ListFanOutsForContentPullJobAsync(contentPullJobId, cancellationToken).ConfigureAwait(false);
		if (fanOuts.Count == 0)
		{
			return false;
		}

		ComplianceContentConfig? config = await _content.GetConfigAsync(cancellationToken).ConfigureAwait(false);
		if (config is null)
		{
			// Should not happen (a pull could not have started without a config), but
			// reconcile must never throw an unrecoverable pull into a retry loop over a
			// config that vanished after the fact -- record the honest failure and move
			// on, matching every other "config missing" path in this slice.
			await _content.RecordPullAsync(
				contentPullJobId, refType: "branch", refValue: "unknown", commit: null,
				ComplianceContentPullStatuses.Failed, "compliance-content config no longer exists at reconcile time.",
				"system", cancellationToken).ConfigureAwait(false);
			await _checkFanOut.MarkReconciledAsync(contentPullJobId, cancellationToken).ConfigureAwait(false);
			return true;
		}

		string sourceCommit = fanOuts[0].SourceCommit;
		Guid runId = fanOuts[0].RunId;
		IReadOnlyList<Guid> checkJobIds = [.. fanOuts.Select(f => f.CheckJobId)];
		IReadOnlyList<ContentCheckResultRecord> resultRows = await _checkFanOut
			.ListCheckResultsAsync(checkJobIds, cancellationToken).ConfigureAwait(false);

		// profile_controls was already replaced per-profile by each content-check job as
		// it parsed its own chunk (ContentCheckJobHandler writes controls directly, the
		// same ReplaceForProfileAsync call the pre-#1016 single-pass handler made --
		// only WHERE it runs moved, not the write itself), so reconcile only needs the
		// content entries to run semantic import/promotion and revision staging.
		List<VendorContentEntry> contentEntries = new(resultRows.Count);
		foreach (ContentCheckResultRecord row in resultRows)
		{
			contentEntries.Add(new VendorContentEntry(
				row.ProfileKey, row.RawYaml, row.HasControlsDirectory, row.HasFilesDirectory, row.ControlFileNames,
				row.InspecCheckRan, row.InspecCheckPassed, row.InspecCheckDetail));
		}

		(int promotedCount, string contentDigest) = await RunSemanticImportAsync(sourceCommit, contentEntries, cancellationToken).ConfigureAwait(false);

		ContentRevision revision = await _revisionStager
			.StageAsync(_options.Value.ContentPath, sourceCommit, contentDigest, cancellationToken)
			.ConfigureAwait(false);

		string actor = await ResolveActorAsync(runId, cancellationToken).ConfigureAwait(false);
		await _content.RecordPullAsync(
			contentPullJobId, config.RefType, config.RefValue, sourceCommit,
			ComplianceContentPullStatuses.Succeeded, note: null, actor, cancellationToken).ConfigureAwait(false);

		await _checkFanOut.MarkReconciledAsync(contentPullJobId, cancellationToken).ConfigureAwait(false);

		string progressPayload = JsonSerializer.Serialize(new
		{
			commit = sourceCommit,
			catalog_promoted_count = promotedCount,
			staged_revision_id = revision.Id,
			checked_profile_count = resultRows.Count,
			failed_check_job_count = readiness.FailedCheckJobs,
		});
		await _events.EmitAsync(JobEventTypes.RunProgress, null, runId, progressPayload, cancellationToken).ConfigureAwait(false);

		LogReconciled(contentPullJobId, resultRows.Count, promotedCount, readiness.FailedCheckJobs);
		return true;
	}

	private async Task<(int PromotedCount, string ContentDigest)> RunSemanticImportAsync(
		string sourceCommit, IReadOnlyList<VendorContentEntry> contentEntries, CancellationToken cancellationToken)
	{
		VendorHierarchyInterpretation interpretation = VendorHierarchyInterpreter.Interpret(contentEntries);
		SemanticImportReport report = SemanticImportReconciler.Reconcile(sourceCommit, interpretation, contentEntries);

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
				CatalogPromotionRequest promotionRequest = BuildPromotionRequest(accepted.Candidate);
				CatalogPromotionOutcome outcome = await _catalog.PromoteCandidateAsync(accepted.Candidate, promotionRequest, cancellationToken).ConfigureAwait(false);
				executionProfileId = outcome.ExecutionProfileId;
				rejectionReason = outcome.RejectionReason;
			}

			if (executionProfileId is not null)
			{
				promotedCount++;
			}

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

	private static string Truncate(string text) => text.Length <= 1000 ? text : text[..1000] + "... [truncated]";

	private async Task<string> ResolveActorAsync(Guid runId, CancellationToken cancellationToken)
	{
		RunQueueState? state = await _jobs.GetRunQueueStateAsync(runId, cancellationToken).ConfigureAwait(false);
		return string.IsNullOrWhiteSpace(state?.InitiatedBy) ? "system" : state!.InitiatedBy!;
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Reconciled content-pull job {ContentPullJobId}: {ProfileCount} profile(s) checked, {PromotedCount} promoted, {FailedCheckJobCount} check job(s) failed")]
	private partial void LogReconciled(Guid contentPullJobId, int profileCount, int promotedCount, int failedCheckJobCount);
}
