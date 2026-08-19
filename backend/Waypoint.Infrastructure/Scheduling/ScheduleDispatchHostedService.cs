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
using Waypoint.Core.Scheduling;

namespace Waypoint.Infrastructure.Scheduling;

/// <summary>
/// Issue #31: periodically sweeps for due schedules via <see cref="ScheduleDispatchService.SweepAsync"/>.
/// Structurally mirrors <see cref="Waypoint.Infrastructure.Secrets.RunSecretCleanupHostedService"/> --
/// an independent periodic sweep. API-surface only (see
/// <c>ServiceCollectionExtensions.AddWaypointApiSurface</c>'s doc comment): registered
/// alongside <c>RunSecretCleanupHostedService</c>/<c>JobEventStreamService</c>, never
/// inside <c>AddWaypointInfrastructure</c>, so the two dedicated runner hosts (which
/// also call that shared method for their control-plane repositories) never start a
/// second, redundant scheduler loop of their own -- exactly one control-plane producer
/// sweeps <c>schedules</c>, per ADR-0013.
/// </summary>
public sealed partial class ScheduleDispatchHostedService : BackgroundService
{
	private readonly ScheduleDispatchService _dispatch;
	private readonly IOptions<ScheduleDispatchOptions> _options;
	private readonly ILogger<ScheduleDispatchHostedService> _logger;

	public ScheduleDispatchHostedService(
		ScheduleDispatchService dispatch, IOptions<ScheduleDispatchOptions> options, ILogger<ScheduleDispatchHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(dispatch);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		_dispatch = dispatch;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		TimeSpan interval = _options.Value.PollInterval;
		LogStarting(interval);

		using PeriodicTimer timer = new(interval);
		do
		{
			await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	/// <summary>One sweep pass, exposed for tests -- same test seam shape as <c>RunSecretCleanupHostedService.SweepOnceAsync</c>.</summary>
	internal async Task SweepOnceAsync(CancellationToken cancellationToken)
	{
		try
		{
			await _dispatch.SweepAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			LogSweepFailed(exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Schedule dispatch sweeping every {Interval}")]
	private partial void LogStarting(TimeSpan interval);

	[LoggerMessage(Level = LogLevel.Error, Message = "Schedule dispatch sweep failed")]
	private partial void LogSweepFailed(Exception exception);
}
