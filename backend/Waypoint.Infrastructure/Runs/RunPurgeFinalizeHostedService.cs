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
/// Issue #1013: the API-side reconciler that finalizes a run purge once its async
/// artifact-purge job has reported success. A purge with on-disk artifacts completes
/// its two phases in two different processes: the API commits the database phase and
/// enqueues the artifact job; compliance-runner's <c>PurgeJobHandler</c> deletes the
/// files and durably reports <c>artifacts_phase = 'done'</c> (the ONLY
/// <c>run_purges</c> write migration 0042 grants <c>waypoint_compliance_runner</c> --
/// INSERT on <c>run_purge_tombstones</c> and DELETE on <c>run_purges</c> are API-only
/// by that migration's documented security posture, "nothing runner-side ever removes
/// a run_purges row"). That leaves nobody to run the finalization step
/// (<see cref="IRunPurgeRepository.CompleteAsync"/>: tombstone + <c>runs.purged_at</c>
/// + in-flight-row deletion) unless the operator manually re-POSTed the purge -- the
/// exact stuck lifecycle issue #1013 reports. This sweep closes that gap in the API
/// process, under the owner connection that already performs every other purge write,
/// keeping the runner's least-privilege boundary intact rather than widening it.
///
/// Structurally mirrors <see cref="Waypoint.Infrastructure.Secrets.RunSecretCleanupHostedService"/>:
/// an independent periodic sweep with an internal single-pass test seam, registered in
/// <c>AddWaypointApiSurface</c> ONLY -- the runner hosts must never start it, both
/// because their DB roles cannot perform these writes (the exact failure class that
/// method's doc comment documents from issue #443) and because finalization is a
/// control-plane responsibility. Finalization is idempotent and races safely with a
/// concurrent operator re-POST: <see cref="RunPurgeService.FinalizePendingAsync"/>
/// re-reads fresh state and no-ops on a vanished row, and
/// <see cref="IRunPurgeRepository.CompleteAsync"/>'s tombstone INSERT is
/// <c>ON CONFLICT DO NOTHING</c>. ADR-0019 decision 5's retryable contract is
/// untouched: a FAILED artifact phase is never swept
/// (<see cref="IRunPurgeRepository.ListPendingFinalizeRunIdsAsync"/> selects
/// <c>done</c> only) -- it stays an honest, operator-visible retryable failure.
/// </summary>
public sealed partial class RunPurgeFinalizeHostedService : BackgroundService
{
	private readonly RunPurgeService _purgeService;
	private readonly IRunPurgeRepository _purges;
	private readonly IOptions<RunPurgeFinalizeOptions> _options;
	private readonly ILogger<RunPurgeFinalizeHostedService> _logger;

	public RunPurgeFinalizeHostedService(
		RunPurgeService purgeService,
		IRunPurgeRepository purges,
		IOptions<RunPurgeFinalizeOptions> options,
		ILogger<RunPurgeFinalizeHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(purgeService);
		ArgumentNullException.ThrowIfNull(purges);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_purgeService = purgeService;
		_purges = purges;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TimeSpan interval = _options.Value.SweepInterval;
		LogSweepStarting(interval);

		using PeriodicTimer timer = new(interval);
		do
		{
			await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	/// <summary>
	/// One sweep pass, exposed for tests -- mirrors
	/// <see cref="Waypoint.Infrastructure.Secrets.RunSecretCleanupHostedService.SweepOnceAsync"/>'s
	/// test seam. Each pending run is finalized independently: one run's failure is
	/// logged and does not block the others, and the row stays selectable for the
	/// next pass (finalization only deletes it on success), so a transient fault
	/// self-heals on the following tick.
	/// </summary>
	internal async Task SweepOnceAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<Guid> pending;
		try
		{
			pending = await _purges.ListPendingFinalizeRunIdsAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		catch (Exception exception)
		{
			LogListFailed(exception);
			return;
		}

		foreach (Guid runId in pending)
		{
			try
			{
				if (await _purgeService.FinalizePendingAsync(runId, cancellationToken).ConfigureAwait(false))
				{
					LogFinalized(runId);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				LogFinalizeFailed(runId, exception);
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Run purge finalization sweeping every {Interval}")]
	private partial void LogSweepStarting(TimeSpan interval);

	[LoggerMessage(Level = LogLevel.Information, Message = "Finalized purge for run {RunId} (artifact job completed)")]
	private partial void LogFinalized(Guid runId);

	[LoggerMessage(Level = LogLevel.Error, Message = "Purge finalization sweep could not list pending purges")]
	private partial void LogListFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Purge finalization failed for run {RunId}; row remains for the next sweep")]
	private partial void LogFinalizeFailed(Guid runId, Exception exception);
}
