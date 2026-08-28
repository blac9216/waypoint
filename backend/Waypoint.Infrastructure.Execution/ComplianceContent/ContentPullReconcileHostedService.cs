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
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.Execution.ComplianceContent;

/// <summary>
/// Issue #1016 (epic #726): the periodic sweep that finalizes a content-pull once its
/// fanned-out <c>content-check</c> jobs have all reported. Structurally mirrors
/// <c>Waypoint.Infrastructure.Runs.RunPurgeFinalizeHostedService</c> ("nobody else can
/// resolve who-else's-job-finished, so a periodic sweep does"), but registered in
/// <c>compliance-runner</c> (via <c>AddWaypointExecution</c>), not the API: reconcile's
/// atomic staging step (<see cref="ContentPullReconcileService"/> -&gt;
/// <c>IContentRevisionStager</c>) touches the content working tree on disk, which only
/// the compliance-runner process mounts (ADR-0017's same placement reasoning that
/// already put <c>content-pull</c>/<c>content-import</c> execution here instead of the
/// API). One sweep pass is independent per pull: one pull's reconcile failure is logged
/// and does not block the others, and the row stays selectable for the next pass
/// (<c>ContentPullReconcileService.TryReconcileAsync</c> only marks rows reconciled on
/// success), so a transient fault self-heals on the following tick.
/// </summary>
public sealed partial class ContentPullReconcileHostedService : BackgroundService
{
	private readonly ContentPullReconcileService _reconcileService;
	private readonly IContentPullCheckFanOutRepository _checkFanOut;
	private readonly IOptions<ContentPullReconcileOptions> _options;
	private readonly ILogger<ContentPullReconcileHostedService> _logger;

	public ContentPullReconcileHostedService(
		ContentPullReconcileService reconcileService,
		IContentPullCheckFanOutRepository checkFanOut,
		IOptions<ContentPullReconcileOptions> options,
		ILogger<ContentPullReconcileHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(reconcileService);
		ArgumentNullException.ThrowIfNull(checkFanOut);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_reconcileService = reconcileService;
		_checkFanOut = checkFanOut;
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

	/// <summary>One sweep pass, exposed for tests -- mirrors <c>RunPurgeFinalizeHostedService.SweepOnceAsync</c>'s test seam.</summary>
	internal async Task SweepOnceAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<Guid> pending;
		try
		{
			pending = await _checkFanOut.ListPendingReconcileContentPullJobIdsAsync(cancellationToken).ConfigureAwait(false);
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

		foreach (Guid contentPullJobId in pending)
		{
			try
			{
				if (await _reconcileService.TryReconcileAsync(contentPullJobId, cancellationToken).ConfigureAwait(false))
				{
					LogReconciledPull(contentPullJobId);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception exception)
			{
				LogReconcileFailed(contentPullJobId, exception);
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Content-pull reconcile sweeping every {Interval}")]
	private partial void LogSweepStarting(TimeSpan interval);

	[LoggerMessage(Level = LogLevel.Information, Message = "Reconciled content-pull job {ContentPullJobId} (all check jobs completed)")]
	private partial void LogReconciledPull(Guid contentPullJobId);

	[LoggerMessage(Level = LogLevel.Error, Message = "Content-pull reconcile sweep could not list pending pulls")]
	private partial void LogListFailed(Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Reconcile failed for content-pull job {ContentPullJobId}; row remains for the next sweep")]
	private partial void LogReconcileFailed(Guid contentPullJobId, Exception exception);
}

/// <summary>How often <see cref="ContentPullReconcileHostedService"/> sweeps for pulls ready to reconcile.</summary>
public sealed class ContentPullReconcileOptions
{
	public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);
}
