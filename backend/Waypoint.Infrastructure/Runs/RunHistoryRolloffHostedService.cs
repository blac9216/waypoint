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
/// Issue #708 (epic #706): a configurable, API-side periodic sweep that rolls off
/// operational history for terminal runs whose generic-deletion gate is already
/// "none" -- it calls the EXISTING <see cref="RunHistoryDeletionService.DeleteHistoryAsync"/>
/// (issue #592) for each candidate rather than re-implementing or bypassing that
/// service's rules; this hosted service only decides WHICH runs to call it for and
/// WHEN, never how a deletion is performed.
///
/// <b>Compliance runs are never swept.</b> <see cref="IRunHistoryDeletionRepository.FindRolloffCandidatesAsync"/>
/// excludes <c>scan</c>/<c>remediate</c> at the query level -- this sweep does not
/// rely solely on <see cref="RunHistoryDeletionService"/>'s runtime 409
/// <c>requires_domain_purge_first</c> gate to reject them one at a time (though that
/// gate WOULD also reject an unpurged compliance run if one ever reached
/// <see cref="SweepOnceAsync"/>, belt-and-suspenders). This is a deliberate reading of
/// epic #706's Design section, which says compliance runs are "windowed out of
/// default views but never auto-deleted" -- windowing (a frontend default-view
/// filter, issue #708's frontend half) and deletion are independent operations, and
/// the epic's own words are "never auto-deleted", not "auto-deleted once purged". A
/// purged compliance run's operational history therefore stays available via
/// `DELETE /runs/{id}/history` as an explicit Admin action (issue #592's existing
/// surface) but is never picked up by this unattended sweep, even after purge.
///
/// Structurally mirrors <see cref="Waypoint.Infrastructure.Secrets.RunSecretCleanupHostedService"/>:
/// an independent periodic sweep, gated behind <see cref="RunHistoryRolloffOptions.Enabled"/>
/// (default false -- see that class's doc comment), failure-isolated per run (one bad
/// row does not halt the pass or the loop), and idempotent (re-sweeping an
/// already-deleted run is a no-op via <see cref="RunHistoryDeletionService"/>'s own
/// <c>AlreadyDeleted</c> outcome, so a run that outlives one sweep interval before its
/// deletion completes is never double-processed).
/// </summary>
public sealed partial class RunHistoryRolloffHostedService : BackgroundService
{
	private const string SweepActor = "system:rolloff-sweep";

	private readonly IRunHistoryDeletionRepository _repository;
	private readonly RunHistoryDeletionService _deletion;
	private readonly IOptions<RunHistoryRolloffOptions> _options;
	private readonly ILogger<RunHistoryRolloffHostedService> _logger;
	private readonly TimeProvider _clock;

	public RunHistoryRolloffHostedService(
		IRunHistoryDeletionRepository repository,
		RunHistoryDeletionService deletion,
		IOptions<RunHistoryRolloffOptions> options,
		ILogger<RunHistoryRolloffHostedService> logger,
		TimeProvider? clock = null)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(deletion);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_repository = repository;
		_deletion = deletion;
		_options = options;
		_logger = logger;
		_clock = clock ?? TimeProvider.System;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		RunHistoryRolloffOptions options = _options.Value;
		if (!options.Enabled)
		{
			LogDisabled();
			return;
		}

		LogSweepStarting(options.SweepInterval, options.MaxAge, options.MaxRunsPerSweep);

		using PeriodicTimer timer = new(options.SweepInterval);
		do
		{
			await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	/// <summary>
	/// One sweep pass, exposed for tests -- mirrors <c>RunSecretCleanupHostedService.SweepOnceAsync</c>'s
	/// test seam. Failure-isolated per candidate: an exception deleting one run's
	/// history is logged and the pass continues with the next candidate rather than
	/// aborting the whole sweep (a single malformed/racing row must not starve every
	/// other eligible run of roll-off indefinitely).
	/// </summary>
	internal async Task SweepOnceAsync(CancellationToken cancellationToken)
	{
		RunHistoryRolloffOptions options = _options.Value;
		DateTimeOffset olderThan = _clock.GetUtcNow() - options.MaxAge;

		IReadOnlyList<Guid> candidates;
		try
		{
			candidates = await _repository.FindRolloffCandidatesAsync(olderThan, options.MaxRunsPerSweep, cancellationToken).ConfigureAwait(false);
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

		int deleted = 0;
		int failed = 0;
		foreach (Guid runId in candidates)
		{
			try
			{
				RunHistoryDeletionResult result = await _deletion.DeleteHistoryAsync(runId, SweepActor, cancellationToken).ConfigureAwait(false);
				if (result.Outcome == RunHistoryDeletionOutcome.Completed)
				{
					deleted++;
				}
				// AlreadyDeleted: a racing manual DELETE or a prior sweep pass already
				// covered this run -- not a failure, just nothing to do (idempotency).
				// RunNotFound/RunNotTerminal/RequiresDomainPurgeFirst should not occur
				// given the candidate query's own filters, but are equally benign no-ops
				// if the run's state changed between the query and this call (e.g. a
				// concurrent purge or a schedule re-triggering); none of them halt the
				// sweep.
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				failed++;
				LogRunSweepFailed(runId, exception);
			}
		}

		if (deleted > 0 || failed > 0)
		{
			LogSwept(deleted, failed, candidates.Count);
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Run history roll-off sweep disabled (RunHistoryRolloff:Enabled=false)")]
	private partial void LogDisabled();

	[LoggerMessage(Level = LogLevel.Information, Message = "Run history roll-off sweeping every {Interval}, max age {MaxAge}, up to {MaxRunsPerSweep} run(s)/pass")]
	private partial void LogSweepStarting(TimeSpan interval, TimeSpan maxAge, int maxRunsPerSweep);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run history roll-off swept {Deleted} run(s), {Failed} failed, of {Candidates} candidate(s)")]
	private partial void LogSwept(int deleted, int failed, int candidates);

	[LoggerMessage(Level = LogLevel.Error, Message = "Run history roll-off candidate lookup failed")]
	private partial void LogCandidateLookupFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Run history roll-off failed to delete history for run {RunId}")]
	private partial void LogRunSweepFailed(Guid runId, Exception exception);
}
