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
				await _states.TransitionAsync(current.Id, RetainedContentStates.Grace, cancellationToken).ConfigureAwait(false);
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

			RetentionPolicy? policy = row.PolicyId is { } policyId
				? await _policies.GetAsync(policyId, cancellationToken).ConfigureAwait(false)
				: null;
			policy ??= await _policies.GetByScopeKeyAsync(RetentionPolicyScopes.Default, cancellationToken).ConfigureAwait(false);
			if (policy is null)
			{
				errors.Add($"no retention policy resolvable for retained-content-state '{row.Id}'; grace window cannot be evaluated.");
				continue;
			}

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
	/// back, not spaced by real time), deletes the underlying depot file, and logs the
	/// outcome per file regardless of whether the delete itself found a file to remove.
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
			if (string.Equals(state, RetainedContentStates.Tracked, StringComparison.Ordinal))
			{
				await _states.TransitionAsync(row.Id, RetainedContentStates.Grace, cancellationToken).ConfigureAwait(false);
				state = RetainedContentStates.Grace;
			}

			if (string.Equals(state, RetainedContentStates.Grace, StringComparison.Ordinal))
			{
				await _states.TransitionAsync(row.Id, RetainedContentStates.PendingPurge, cancellationToken).ConfigureAwait(false);
			}

			(bool deleted, string? deleteError) = await DeletePhysicalFileAsync(row.DepotArtifactId, cancellationToken).ConfigureAwait(false);

			await _states.TransitionAsync(row.Id, RetainedContentStates.Purged, cancellationToken).ConfigureAwait(false);

			LogPurged(_logger, row.DepotArtifactId, row.Id, actor, reason ?? "(none)", deleted);
			return new RetentionPurgeOutcome(row.Id, true, deleteError);
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

	[LoggerMessage(Level = LogLevel.Information, Message = "retention sweep skipped for scope '{ScopeKey}': listing/scan was unverified or partial")]
	private static partial void LogListingUnverified(ILogger logger, string scopeKey);

	[LoggerMessage(Level = LogLevel.Information, Message = "retention: purged depot artifact {DepotArtifactId} (retained-content-state {StateId}), actor={Actor}, reason={Reason}, fileDeleted={FileDeleted}")]
	private static partial void LogPurged(ILogger logger, Guid depotArtifactId, Guid stateId, string actor, string reason, bool fileDeleted);
}
