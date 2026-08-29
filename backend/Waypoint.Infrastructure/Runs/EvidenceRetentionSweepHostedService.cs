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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Runs;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Issue #1062 (epic #726 sections 6/7): the API-side periodic sweep that finds
/// compliance runs past the Admin-configured evidence retention period and drives
/// each one through the EXISTING <see cref="RunPurgeService.PurgeRunAsync"/> path --
/// the SAME entry point <c>POST /runs/{id}/purge</c> uses -- rather than
/// re-implementing or bypassing any part of that deletion. This hosted service only
/// decides WHICH runs are candidates and WHEN, mirroring
/// <see cref="RunHistoryRolloffHostedService"/>'s own division of responsibility for
/// its sibling (non-compliance) sweep.
///
/// Two independent layers keep a held run's evidence graph intact even if one of them
/// has a bug: <see cref="IEvidenceRetentionSweepRepository.FindPurgeCandidatesAsync"/>
/// excludes held runs at the SQL level (an anti-join against
/// <c>run_retention_holds</c>, per PR #1083's round-1 review verdict -- see that
/// interface's doc comment for why a C# "list held run ids" surface was rejected),
/// and <see cref="RunPurgeService.PurgeRunAsync"/> itself refuses
/// (<see cref="RunPurgeOutcome.Held"/>) any run it is called for that is held,
/// regardless of how it got selected. A <see cref="RunPurgeOutcome.Held"/> result is
/// therefore a benign no-op here, not a failure -- exactly like
/// <see cref="RunHistoryRolloffHostedService"/> treats its own analogous benign
/// outcomes (already-deleted, not-yet-terminal).
///
/// Policy-driven purges are audited distinctly from operator-initiated ones by the
/// SAME mechanism <see cref="RunHistoryRolloffHostedService"/> already established
/// for its own sweep: <see cref="SweepActor"/> (<c>"system:retention-sweep"</c>) is
/// threaded through as the <c>actor</c> on every call, landing in
/// <c>run_purges.requested_by</c> and the completed <c>run_purge_tombstones.actor</c>
/// -- the same append-only audit trail every purge already writes, distinguishable
/// from an operator purge (whose actor is always a real username, never this
/// reserved <c>system:</c>-prefixed string) without a second, parallel audit table.
///
/// Retention period is read fresh from <see cref="IRetentionPolicyRepository"/> at
/// the START of every pass, not cached across ticks or bound via <c>IOptions</c> --
/// an Admin changing the configured period (AC1) takes effect on the sweep's next
/// tick with no restart, matching the singleton's own "read fresh on every call"
/// posture <see cref="RunPurgeService"/> already uses for
/// <see cref="IRunRetentionHoldRepository"/>.
///
/// <see cref="EvidenceRetentionSweepOptions.Enabled"/> defaults to <c>false</c> --
/// same conservative posture as <see cref="RunHistoryRolloffOptions.Enabled"/>, this
/// service's own no-op-when-disabled test seam, and the same rationale: an operator
/// opts in once the configured retention period reflects a real decision, rather than
/// the appliance silently purging compliance evidence on a default schedule.
/// </summary>
public sealed partial class EvidenceRetentionSweepHostedService : BackgroundService
{
	/// <summary>
	/// Reserved actor string for every policy-driven purge this sweep requests --
	/// never a real username, so it is unambiguously distinguishable from an
	/// operator-initiated <c>POST /runs/{id}/purge</c> in <c>run_purges.requested_by</c>
	/// and <c>run_purge_tombstones.actor</c> (AC4). Mirrors
	/// <see cref="RunHistoryRolloffHostedService"/>'s own <c>SweepActor</c> constant.
	/// </summary>
	internal const string SweepActor = "system:retention-sweep";

	private readonly IRetentionPolicyRepository _policy;
	private readonly IEvidenceRetentionSweepRepository _candidates;
	private readonly RunPurgeService _purge;
	private readonly IOptions<EvidenceRetentionSweepOptions> _options;
	private readonly ILogger<EvidenceRetentionSweepHostedService> _logger;
	private readonly TimeProvider _clock;

	public EvidenceRetentionSweepHostedService(
		IRetentionPolicyRepository policy,
		IEvidenceRetentionSweepRepository candidates,
		RunPurgeService purge,
		IOptions<EvidenceRetentionSweepOptions> options,
		ILogger<EvidenceRetentionSweepHostedService> logger,
		TimeProvider? clock = null)
	{
		ArgumentNullException.ThrowIfNull(policy);
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(purge);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_policy = policy;
		_candidates = candidates;
		_purge = purge;
		_options = options;
		_logger = logger;
		_clock = clock ?? TimeProvider.System;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		EvidenceRetentionSweepOptions options = _options.Value;
		if (!options.Enabled)
		{
			LogDisabled();
			return;
		}

		LogSweepStarting(options.SweepInterval, options.MaxRunsPerSweep);

		using PeriodicTimer timer = new(options.SweepInterval);
		do
		{
			await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	/// <summary>
	/// One sweep pass, exposed for tests -- mirrors
	/// <see cref="RunHistoryRolloffHostedService.SweepOnceAsync"/>'s own test seam.
	/// Failure-isolated per candidate: an exception purging one run is logged and the
	/// pass continues with the next candidate rather than aborting the whole sweep.
	/// </summary>
	internal async Task SweepOnceAsync(CancellationToken cancellationToken)
	{
		RetentionPolicy? policy;
		try
		{
			policy = await _policy.GetAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		catch (Exception exception)
		{
			LogPolicyLookupFailed(exception);
			return;
		}

		if (policy is null)
		{
			// Migration 0078 seeds the singleton unconditionally -- reaching here means
			// it was removed out of band. Skip this pass rather than guessing a
			// fallback retention period; the next pass tries again.
			LogPolicyMissing();
			return;
		}

		EvidenceRetentionSweepOptions options = _options.Value;
		DateTimeOffset olderThan = _clock.GetUtcNow() - TimeSpan.FromDays(policy.EvidenceRetentionDays);

		IReadOnlyList<Guid> candidates;
		try
		{
			candidates = await _candidates.FindPurgeCandidatesAsync(olderThan, options.MaxRunsPerSweep, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		catch (Exception exception)
		{
			LogCandidateLookupFailed(exception);
			return;
		}

		int purged = 0;
		int inProgress = 0;
		int held = 0;
		int failed = 0;
		foreach (Guid runId in candidates)
		{
			try
			{
				RunPurgeResult result = await _purge.PurgeRunAsync(runId, SweepActor, cancellationToken).ConfigureAwait(false);
				switch (result.Outcome)
				{
					case RunPurgeOutcome.Completed:
						purged++;
						break;
					case RunPurgeOutcome.InProgress:
						inProgress++;
						break;
					case RunPurgeOutcome.Held:
						// The anti-join above is not the only thing standing between a
						// sweep and a held run's evidence -- see this class's doc
						// comment. A hold placed between the candidate query and this
						// call lands here instead, which is the exact belt-and-suspenders
						// case that check exists for.
						held++;
						break;
					// AlreadyPurged/RunNotFound/RunNotTerminal should not occur given
					// the candidate query's own filters, but are equally benign no-ops
					// if the run's state changed between the query and this call (e.g.
					// a concurrent operator purge) -- same reasoning
					// RunHistoryRolloffHostedService.SweepOnceAsync already documents
					// for its own analogous outcomes.
					case RunPurgeOutcome.AlreadyPurged:
					case RunPurgeOutcome.RunNotFound:
					case RunPurgeOutcome.RunNotTerminal:
						break;
					case RunPurgeOutcome.Failed:
					default:
						failed++;
						break;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				failed++;
				LogRunPurgeFailed(runId, exception);
			}
		}

		if (purged > 0 || inProgress > 0 || held > 0 || failed > 0)
		{
			LogSwept(purged, inProgress, held, failed, candidates.Count);
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Evidence retention sweep disabled (EvidenceRetentionSweep:Enabled=false)")]
	private partial void LogDisabled();

	[LoggerMessage(Level = LogLevel.Information, Message = "Evidence retention sweep starting: every {Interval}, up to {MaxRunsPerSweep} run(s)/pass")]
	private partial void LogSweepStarting(TimeSpan interval, int maxRunsPerSweep);

	[LoggerMessage(Level = LogLevel.Error, Message = "Evidence retention sweep: retention_policy lookup failed")]
	private partial void LogPolicyLookupFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Evidence retention sweep: retention_policy singleton row is missing, skipping this pass")]
	private partial void LogPolicyMissing();

	[LoggerMessage(Level = LogLevel.Error, Message = "Evidence retention sweep candidate lookup failed")]
	private partial void LogCandidateLookupFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Information, Message = "Evidence retention sweep: {Purged} completed, {InProgress} started, {Held} held (skipped), {Failed} failed, of {Candidates} candidate(s)")]
	private partial void LogSwept(int purged, int inProgress, int held, int failed, int candidates);

	[LoggerMessage(Level = LogLevel.Error, Message = "Evidence retention sweep failed to purge run {RunId}")]
	private partial void LogRunPurgeFailed(Guid runId, Exception exception);
}
