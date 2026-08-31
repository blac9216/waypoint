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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IRetentionSweepService"/>
public sealed partial class RetentionSweepService : IRetentionSweepService
{
	private readonly IRetainedContentStateRepository _states;
	private readonly IRetentionPolicyRepository _policies;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobEventPublisher _events;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly TimeProvider _clock;
	private readonly ILogger<RetentionSweepService> _logger;

	public RetentionSweepService(
		IRetainedContentStateRepository states,
		IRetentionPolicyRepository policies,
		IDepotArtifactRepository artifacts,
		IJobEventPublisher events,
		IOptions<CatalogOptions> catalogOptions,
		ILogger<RetentionSweepService> logger,
		TimeProvider? clock = null)
	{
		ArgumentNullException.ThrowIfNull(states);
		ArgumentNullException.ThrowIfNull(policies);
		ArgumentNullException.ThrowIfNull(artifacts);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(logger);

		_states = states;
		_policies = policies;
		_artifacts = artifacts;
		_events = events;
		_catalogOptions = catalogOptions;
		_logger = logger;
		_clock = clock ?? TimeProvider.System;
	}

	public async Task<RetentionSweepReport> RunSweepAsync(RetentionSweepRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (!request.ListingVerified)
		{
			LogListingUnverified(_logger, request.ScopeKey ?? RetentionPolicyScopes.Default);
			return new RetentionSweepReport(
				Skipped: true,
				SkippedReason: "the underlying listing/scan was unverified or partial; the sweep took no action (never prune on incomplete state).",
				EnteredGrace: 0,
				AutoPruned: 0,
				UntrackedCandidatesSkipped: 0,
				Errors: []);
		}

		List<string> errors = [];
		int enteredGrace = 0;
		int untrackedSkipped = 0;

		// Resolved once per call (ScopeKey is a request-level input, not per-candidate):
		// which RetentionPolicy newly-entering-grace candidates are evaluated against,
		// per this type's doc comment on RetentionSweepRequest.ScopeKey. Null/blank
		// falls back to RetentionPolicyScopes.Default; an unresolvable explicit scope
		// key also falls back to Default rather than leaving every candidate this call
		// enters ungoverned.
		RetentionPolicy? entryPolicy = await ResolveScopePolicyAsync(request.ScopeKey, cancellationToken).ConfigureAwait(false);

		// Entry pass: candidates the caller has already identified as superseded/
		// out-of-window (see this type's doc comment -- discovery itself is out of
		// scope until #1421 lands). Only an already-tracked row can move; a candidate
		// with no row yet is skipped rather than inserted -- see IRetentionSweepService's
		// doc comment for why this service never calls EnsureTrackedAsync.
		foreach (Guid depotArtifactId in request.SupersededOrOutOfWindowDepotArtifactIds)
		{
			cancellationToken.ThrowIfCancellationRequested();

			RetainedContentState? current = await _states.GetByDepotArtifactIdAsync(depotArtifactId, cancellationToken).ConfigureAwait(false);
			if (current is null)
			{
				untrackedSkipped++;
				continue;
			}

			if (!string.Equals(current.State, RetainedContentStates.Tracked, StringComparison.Ordinal))
			{
				// Already grace/pinned/pending-purge/purged: nothing to do, not an error.
				continue;
			}

			try
			{
				await _states.TransitionAsync(current.Id, RetainedContentStates.Grace, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
				if (entryPolicy is not null)
				{
					await _states.SetPolicyAsync(current.Id, entryPolicy.Id, cancellationToken).ConfigureAwait(false);
				}
				await RaiseGraceAlertAsync(depotArtifactId, current.Id, cancellationToken).ConfigureAwait(false);
				enteredGrace++;
			}
			catch (InvalidOperationException exception)
			{
				errors.Add(exception.Message);
			}
		}

		// Auto-prune pass: every row currently in grace, regardless of whether this
		// call's own entry pass just put it there. Pinned rows are a different state
		// and are structurally never returned here -- "pinned content is never pruned"
		// holds by construction, not by an extra check.
		int autoPruned = 0;
		IReadOnlyList<RetainedContentState> graceRows = await _states
			.ListByStateAsync(RetainedContentStates.Grace, cancellationToken).ConfigureAwait(false);

		foreach (RetainedContentState row in graceRows)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (row.GraceStartedAt is null)
			{
				// Defensive: TransitionAsync always stamps grace_started_at on entry to
				// grace, so this should be unreachable; skip rather than crash the pass.
				continue;
			}

			RetentionPolicy? policy;
			if (row.PolicyId is { } policyId)
			{
				// An explicit policy_id that fails to resolve is a data defect (the
				// referenced download_retention_policies row was removed, or never
				// existed) -- surfaced as an error, not silently substituted with the
				// Default scope's window, which may be shorter and would therefore
				// prune this row early without anyone being told why.
				policy = await _policies.GetAsync(policyId, cancellationToken).ConfigureAwait(false);
				if (policy is null)
				{
					errors.Add($"retained-content-state '{row.Id}' references retention policy '{policyId}' which no longer resolves; grace window cannot be evaluated until the dangling policy_id is corrected.");
					continue;
				}
			}
			else
			{
				policy = await _policies.GetByScopeKeyAsync(RetentionPolicyScopes.Default, cancellationToken).ConfigureAwait(false);
				if (policy is null)
				{
					errors.Add($"no retention policy resolvable for retained-content-state '{row.Id}'; grace window cannot be evaluated.");
					continue;
				}
			}

			// Single time source: row.GraceStartedAt was stamped by this same _clock
			// (see the entry pass and PurgeRowInternalAsync, both of which pass
			// _clock.GetUtcNow() as TransitionAsync's occurredAt) rather than the
			// database's own now(), so this comparison never drifts against DB/app
			// clock skew. elapsed == the grace window counts as due (">=", not ">"):
			// a row is not entitled to outlive its own window by even one tick.
			TimeSpan elapsed = _clock.GetUtcNow() - row.GraceStartedAt.Value;
			if (elapsed < TimeSpan.FromDays(policy.GracePeriodDays))
			{
				continue; // not yet due
			}

			RetentionPurgeOutcome outcome = await PurgeRowInternalAsync(row, "retention-sweep", "grace window elapsed", cancellationToken)
				.ConfigureAwait(false);
			if (outcome.Purged)
			{
				autoPruned++;
			}
			else if (outcome.Error is not null)
			{
				errors.Add(outcome.Error);
			}
		}

		return new RetentionSweepReport(
			Skipped: false,
			SkippedReason: null,
			EnteredGrace: enteredGrace,
			AutoPruned: autoPruned,
			UntrackedCandidatesSkipped: untrackedSkipped,
			Errors: errors);
	}

	public async Task<RetentionPurgeOutcome> PurgeImmediatelyAsync(
		Guid retainedContentStateId, string actor, string? reason, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		RetainedContentState? current = await _states.GetAsync(retainedContentStateId, cancellationToken).ConfigureAwait(false);
		if (current is null)
		{
			return new RetentionPurgeOutcome(retainedContentStateId, false, $"no retained-content-state row with id '{retainedContentStateId}'.");
		}

		return await PurgeRowInternalAsync(current, actor, reason, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Shared purge path for both the auto-prune pass and an explicit immediate purge:
	/// walks <paramref name="row"/> through whatever legal prefix of
	/// <c>tracked -&gt; grace -&gt; pending-purge -&gt; purged</c> it has not already
	/// completed (skipping the grace-window wait -- the transitions happen back to
	/// back, not spaced by real time), then deletes the underlying depot file.
	/// <c>purged</c> is reached ONLY when the delete itself reports success (including
	/// the idempotent already-absent-file case) -- a failed delete (an
	/// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>, a missing
	/// <c>depot_artifacts</c> row, or the path-confinement refusal) leaves the row at
	/// <c>pending-purge</c> rather than the terminal <c>purged</c> state, so a repeat
	/// call (an operator retry, or a future purge-now) can still complete it once the
	/// underlying problem is fixed -- <c>purged</c> has no transition out, so marking a
	/// row purged when its bytes were never actually removed would make the row
	/// unrecoverable and the leaked bytes permanently invisible to this service. Every
	/// outcome -- success or failure -- is logged per file.
	/// </summary>
	private async Task<RetentionPurgeOutcome> PurgeRowInternalAsync(
		RetainedContentState row, string actor, string? reason, CancellationToken cancellationToken)
	{
		if (string.Equals(row.State, RetainedContentStates.Purged, StringComparison.Ordinal))
		{
			return new RetentionPurgeOutcome(row.Id, false, "already purged; no action taken.");
		}

		if (string.Equals(row.State, RetainedContentStates.Pinned, StringComparison.Ordinal))
		{
			return new RetentionPurgeOutcome(row.Id, false, "content is pinned; unpin before purging (pin exists specifically to prevent removal).");
		}

		try
		{
			string state = row.State;
			DateTimeOffset occurredAt = _clock.GetUtcNow();
			if (string.Equals(state, RetainedContentStates.Tracked, StringComparison.Ordinal))
			{
				await _states.TransitionAsync(row.Id, RetainedContentStates.Grace, occurredAt, cancellationToken).ConfigureAwait(false);
				state = RetainedContentStates.Grace;
			}

			if (string.Equals(state, RetainedContentStates.Grace, StringComparison.Ordinal))
			{
				await _states.TransitionAsync(row.Id, RetainedContentStates.PendingPurge, occurredAt, cancellationToken).ConfigureAwait(false);
			}

			(bool deleted, string? deleteError) = await DeletePhysicalFileAsync(row.DepotArtifactId, cancellationToken).ConfigureAwait(false);

			if (deleteError is not null)
			{
				// The physical delete failed -- do NOT transition to purged (terminal,
				// no way back). The row stays at pending-purge: revisitable, not lost.
				LogPurgeFailed(_logger, row.DepotArtifactId, row.Id, actor, reason ?? "(none)", deleteError);
				return new RetentionPurgeOutcome(row.Id, false, deleteError);
			}

			await _states.TransitionAsync(row.Id, RetainedContentStates.Purged, occurredAt, cancellationToken).ConfigureAwait(false);

			LogPurged(_logger, row.DepotArtifactId, row.Id, actor, reason ?? "(none)", deleted);
			return new RetentionPurgeOutcome(row.Id, true, null);
		}
		catch (InvalidOperationException exception)
		{
			return new RetentionPurgeOutcome(row.Id, false, exception.Message);
		}
	}

	/// <summary>
	/// Deletes the depot file <paramref name="depotArtifactId"/> resolves to under
	/// <see cref="CatalogOptions.DepotPath"/>, re-confining the resolved full path
	/// beneath that root before any <see cref="File.Delete(string)"/> (same defense-
	/// in-depth as <c>Waypoint.Infrastructure.Scans.PurgeJobHandler.IsConfined</c>,
	/// even though <see cref="DepotArtifact.ExternalId"/> is never client-supplied).
	/// An already-absent file counts as a successful delete (idempotent, matching
	/// <c>PurgeJobHandler</c>'s own "missing is a normal, not exceptional, state"
	/// convention) -- a re-run of an interrupted purge must not report failure just
	/// because a prior pass already removed the bytes.
	/// </summary>
	private async Task<(bool Deleted, string? Error)> DeletePhysicalFileAsync(Guid depotArtifactId, CancellationToken cancellationToken)
	{
		DepotArtifact? artifact = await _artifacts.GetByIdAsync(depotArtifactId, cancellationToken).ConfigureAwait(false);
		if (artifact is null)
		{
			return (false, $"depot artifact '{depotArtifactId}' not found; nothing to delete on disk.");
		}

		string root = _catalogOptions.Value.DepotPath;
		string fullRoot = Path.GetFullPath(root);
		string fullPath = Path.GetFullPath(Path.Combine(root, artifact.ExternalId));
		bool confined = fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
			&& (fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
		if (!confined)
		{
			return (false, $"refused to delete '{artifact.ExternalId}': resolves outside the configured depot root.");
		}

		try
		{
			if (File.Exists(fullPath))
			{
				File.Delete(fullPath);
			}

			return (true, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (false, $"'{artifact.ExternalId}': {exception.Message}");
		}
	}

	private async Task RaiseGraceAlertAsync(Guid depotArtifactId, Guid retainedContentStateId, CancellationToken cancellationToken)
	{
		string payload = JsonSerializer.Serialize(new
		{
			kind = "download.retention.grace_entered",
			depot_artifact_id = depotArtifactId,
			retained_content_state_id = retainedContentStateId,
		});
		await _events.EmitAsync(JobEventTypes.SystemNotice, null, null, payload, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Resolves <paramref name="scopeKey"/> (null/blank falls back to
	/// <see cref="RetentionPolicyScopes.Default"/>) to a <see cref="RetentionPolicy"/>
	/// for the entry pass -- <see cref="RetentionSweepRequest.ScopeKey"/>'s documented
	/// contract: "resolves which RetentionPolicy newly-entering-grace candidates are
	/// evaluated against (falls back to Default when null/blank or unresolvable)". An
	/// unresolvable explicit scope key also falls back to Default rather than leaving
	/// this call's candidates with no resolvable policy at all.
	/// </summary>
	private async Task<RetentionPolicy?> ResolveScopePolicyAsync(string? scopeKey, CancellationToken cancellationToken)
	{
		string effectiveKey = string.IsNullOrWhiteSpace(scopeKey) ? RetentionPolicyScopes.Default : scopeKey;
		RetentionPolicy? policy = await _policies.GetByScopeKeyAsync(effectiveKey, cancellationToken).ConfigureAwait(false);
		if (policy is not null)
		{
			return policy;
		}

		if (!string.Equals(effectiveKey, RetentionPolicyScopes.Default, StringComparison.Ordinal))
		{
			return await _policies.GetByScopeKeyAsync(RetentionPolicyScopes.Default, cancellationToken).ConfigureAwait(false);
		}

		return null;
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "retention sweep skipped for scope '{ScopeKey}': listing/scan was unverified or partial")]
	private static partial void LogListingUnverified(ILogger logger, string scopeKey);

	[LoggerMessage(Level = LogLevel.Information, Message = "retention: purged depot artifact {DepotArtifactId} (retained-content-state {StateId}), actor={Actor}, reason={Reason}, fileDeleted={FileDeleted}")]
	private static partial void LogPurged(ILogger logger, Guid depotArtifactId, Guid stateId, string actor, string reason, bool fileDeleted);

	[LoggerMessage(Level = LogLevel.Error, Message = "retention: purge FAILED for depot artifact {DepotArtifactId} (retained-content-state {StateId}), actor={Actor}, reason={Reason}: {Error}")]
	private static partial void LogPurgeFailed(ILogger logger, Guid depotArtifactId, Guid stateId, string actor, string reason, string error);
}
