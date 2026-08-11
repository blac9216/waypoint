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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.Jobs;

namespace Waypoint.Runner.Jobs;

/// <summary>
/// Periodically sweeps <c>jobs</c> for expired leases (a worker that claimed a job and
/// then crashed, was killed, or lost network to Postgres before it could heartbeat) and
/// either requeues or fails each one per <see cref="IJobRunnerRepository.RecoverExpiredLeasesAsync"/>.
/// Runs independently of <see cref="JobDispatcherHostedService"/> -- and independently
/// per process, so more than one Waypoint instance can run this sweep safely (that
/// safety, not just the sweep's existence, is what
/// <c>JobLeaseRecoveryConcurrencyTests</c> proves).
/// </summary>
public sealed partial class LeaseRecoveryHostedService : BackgroundService
{
	private readonly IJobRunnerRepository _repository;
	private readonly IJobEventPublisher _events;
	private readonly IOptions<JobEngineOptions> _options;
	private readonly ILogger<LeaseRecoveryHostedService> _logger;

	public LeaseRecoveryHostedService(
		IJobRunnerRepository repository, IJobEventPublisher events, IOptions<JobEngineOptions> options, ILogger<LeaseRecoveryHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_repository = repository;
		_events = events;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		JobEngineOptions options = _options.Value;
		if (!options.Enabled)
		{
			LogRecoveryDisabled();
			return;
		}

		LogRecoveryStarting(options.RecoveryInterval, options.RecoveryBatchSize);

		using PeriodicTimer timer = new(options.RecoveryInterval);
		do
		{
			try
			{
				await SweepAsync(options.RecoveryBatchSize, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				LogSweepFailed(exception);
			}
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	/// <summary>One sweep pass, exposed for tests -- drains up to a few batches so a large backlog of expired leases does not wait for multiple timer ticks.</summary>
	internal async Task SweepAsync(int batchSize, CancellationToken cancellationToken)
	{
		const int maxPassesPerTick = 5;

		for (int pass = 0; pass < maxPassesPerTick; pass++)
		{
			IReadOnlyList<RecoveredJob> recovered = await _repository.RecoverExpiredLeasesAsync(batchSize, cancellationToken).ConfigureAwait(false);
			if (recovered.Count == 0)
			{
				return;
			}

			foreach (RecoveredJob job in recovered)
			{
				string payload = JsonSerializer.Serialize(new
				{
					from = JobStates.Running,
					to = job.NewState,
					reason = "lease_expired",
					attempt = job.AttemptCount,
					max_attempts = job.MaxAttempts
				});
				await _events.EmitAsync(JobEventTypes.JobState, job.Id, job.RunId, payload, cancellationToken).ConfigureAwait(false);
			}

			if (recovered.Count < batchSize)
			{
				return;
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Lease recovery disabled (JobEngine:Enabled=false)")]
	private partial void LogRecoveryDisabled();

	[LoggerMessage(Level = LogLevel.Information, Message = "Lease recovery sweeping every {Interval}, batch size {BatchSize}")]
	private partial void LogRecoveryStarting(TimeSpan interval, int batchSize);

	[LoggerMessage(Level = LogLevel.Error, Message = "Lease recovery sweep failed")]
	private partial void LogSweepFailed(Exception exception);
}
