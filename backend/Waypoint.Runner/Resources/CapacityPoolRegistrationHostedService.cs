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
using Waypoint.Core.Capacity;
using Waypoint.Runner.Jobs;

namespace Waypoint.Runner.Resources;

/// <summary>
/// Registers this runner's view of the appliance's shareable capacity into the
/// singleton <c>capacity_pool</c> row at startup (issue #569, ADR-0020). When the
/// operator set <see cref="CapacityPoolOptions.PoolCpuCores"/>/<see cref="CapacityPoolOptions.PoolMemoryBytes"/>
/// explicitly, that pair is written as the authoritative <c>source='operator'</c>
/// capacity; otherwise the host-derived numbers from <see cref="IHostCapabilitySource"/>
/// (host CPU/memory, ADR-0018 discovery -- deliberately NOT this container's
/// cgroup-capped budget, because the pool describes the appliance, while each runner's
/// own cgroup/cap budget stays its private upper bound) are contributed as
/// <c>source='derived'</c>, GREATEST-converged across replicas.
///
/// <para>
/// Issue #633: a fresh stack can lose the startup race against the backend's schema
/// migrator -- <c>capacity_pool</c> (migration 0036) may not exist yet when this host
/// starts, so the very first registration attempt can hit <c>42P01</c>. Registration is
/// therefore NOT one-shot: a failed attempt is retried with capped exponential backoff,
/// unbounded in attempt count (only the delay is bounded) -- this call is expected to
/// eventually succeed once the schema exists, so giving up is never correct, only
/// noisy. Until the first success, every pool claim is denied (fail safe), the
/// dispatcher keeps releasing claims back to the queue, and readiness is not tied to
/// this write because that degraded behavior is already what a missing row produces.
/// Once registered, the loop keeps re-registering on
/// <see cref="CapacityPoolOptions.LeaseDuration"/>'s cadence so a row lost to, e.g., an
/// operator truncating the table self-heals without a runner restart.
/// </para>
/// </summary>
public sealed partial class CapacityPoolRegistrationHostedService : BackgroundService
{
	/// <summary>Initial retry delay after a failed registration attempt.</summary>
	internal static readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(1);

	/// <summary>Retry delay ceiling -- backoff doubles from the initial delay up to this, then holds.</summary>
	internal static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(30);

	/// <summary>Only every Nth consecutive failure is logged at Error, bounding log noise across an unbounded retry loop while a schema race resolves itself.</summary>
	internal const int FailureLogEvery = 10;

	private readonly ICapacityLeasePool _pool;
	private readonly IHostCapabilitySource _hostCapabilities;
	private readonly JobDispatcherHostedService _dispatcher;
	private readonly IOptions<CapacityPoolOptions> _options;
	private readonly ILogger<CapacityPoolRegistrationHostedService> _logger;
	private readonly TimeSpan _initialRetryDelay;
	private readonly TimeSpan _maxRetryDelay;

	public CapacityPoolRegistrationHostedService(
		ICapacityLeasePool pool,
		IHostCapabilitySource hostCapabilities,
		JobDispatcherHostedService dispatcher,
		IOptions<CapacityPoolOptions> options,
		ILogger<CapacityPoolRegistrationHostedService> logger)
		: this(pool, hostCapabilities, dispatcher, options, logger, DefaultInitialRetryDelay, DefaultMaxRetryDelay)
	{
	}

	/// <summary>Test-only seam: lets unit tests use a fast retry cadence instead of waiting out the production 1s/30s backoff.</summary>
	internal CapacityPoolRegistrationHostedService(
		ICapacityLeasePool pool,
		IHostCapabilitySource hostCapabilities,
		JobDispatcherHostedService dispatcher,
		IOptions<CapacityPoolOptions> options,
		ILogger<CapacityPoolRegistrationHostedService> logger,
		TimeSpan initialRetryDelay,
		TimeSpan maxRetryDelay)
	{
		ArgumentNullException.ThrowIfNull(pool);
		ArgumentNullException.ThrowIfNull(hostCapabilities);
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);
		_pool = pool;
		_hostCapabilities = hostCapabilities;
		_dispatcher = dispatcher;
		_options = options;
		_logger = logger;
		_initialRetryDelay = initialRetryDelay;
		_maxRetryDelay = maxRetryDelay;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		CapacityPoolOptions options = _options.Value;
		if (!options.Enabled)
		{
			return;
		}

		bool operatorSet = options is { PoolCpuCores: not null, PoolMemoryBytes: not null };
		double cpuCores = operatorSet ? options.PoolCpuCores!.Value : _hostCapabilities.AvailableCpuCores();
		long memoryBytes = operatorSet ? options.PoolMemoryBytes!.Value : _hostCapabilities.TotalMemoryBytes();
		string source = operatorSet ? "operator" : "derived";

		int consecutiveFailures = 0;
		TimeSpan retryDelay = _initialRetryDelay;

		// Registration retries on its own clock, not the periodic capacity-report/
		// heartbeat cadence -- unlike worker_registry's heartbeat (which re-announces on
		// every RunnerReadinessReportingHostedService refresh regardless of prior
		// outcome), a missing capacity_pool row denies ALL job types, so this loop
		// retries aggressively (capped backoff, not a multi-minute refresh interval)
		// until the first success, then settles onto LeaseDuration so a later lost row
		// still self-heals without a restart.
		while (!stoppingToken.IsCancellationRequested)
		{
			bool registered = false;
			try
			{
				await _pool.RegisterPoolCapacityAsync(_dispatcher.WorkerId, cpuCores, memoryBytes, operatorSet, stoppingToken).ConfigureAwait(false);
				registered = true;
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				consecutiveFailures++;
				if (consecutiveFailures == 1 || consecutiveFailures % FailureLogEvery == 0)
				{
					LogPoolRegistrationFailed(exception, consecutiveFailures, retryDelay);
				}
			}

			if (registered)
			{
				if (consecutiveFailures > 0)
				{
					LogPoolRegisteredAfterRetries(source, cpuCores, memoryBytes, consecutiveFailures);
				}
				else
				{
					LogPoolRegistered(source, cpuCores, memoryBytes);
				}

				consecutiveFailures = 0;
				retryDelay = _initialRetryDelay;

				try
				{
					await Task.Delay(options.LeaseDuration, stoppingToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				continue;
			}

			try
			{
				await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			TimeSpan doubled = retryDelay * 2;
			retryDelay = doubled > _maxRetryDelay ? _maxRetryDelay : doubled;
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Capacity pool registered: source={Source}, {CpuCores} cores / {MemoryBytes} bytes shareable appliance capacity (ADR-0020)")]
	private partial void LogPoolRegistered(string source, double cpuCores, long memoryBytes);

	[LoggerMessage(Level = LogLevel.Information, Message = "Capacity pool registered: source={Source}, {CpuCores} cores / {MemoryBytes} bytes shareable appliance capacity (ADR-0020), after {FailedAttempts} failed attempt(s)")]
	private partial void LogPoolRegisteredAfterRetries(string source, double cpuCores, long memoryBytes, int failedAttempts);

	[LoggerMessage(Level = LogLevel.Error, Message = "Capacity pool registration failed (attempt {Attempt}, retrying in {RetryDelay}); pool admission stays denied (fail safe) until a registration succeeds")]
	private partial void LogPoolRegistrationFailed(Exception exception, int attempt, TimeSpan retryDelay);
}
