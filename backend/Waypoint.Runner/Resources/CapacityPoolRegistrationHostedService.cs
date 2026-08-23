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
/// Registration failure is logged, not fatal: with no pool row every pool claim is
/// denied (fail safe), the dispatcher keeps releasing claims back to the queue, and a
/// later successful registration (another replica, or this one after a restart)
/// unblocks admission. Readiness is not tied to this write because the correct
/// degraded behavior -- deny -- is already what a missing row produces.
/// </para>
/// </summary>
public sealed partial class CapacityPoolRegistrationHostedService : IHostedService
{
	private readonly ICapacityLeasePool _pool;
	private readonly IHostCapabilitySource _hostCapabilities;
	private readonly JobDispatcherHostedService _dispatcher;
	private readonly IOptions<CapacityPoolOptions> _options;
	private readonly ILogger<CapacityPoolRegistrationHostedService> _logger;

	public CapacityPoolRegistrationHostedService(
		ICapacityLeasePool pool,
		IHostCapabilitySource hostCapabilities,
		JobDispatcherHostedService dispatcher,
		IOptions<CapacityPoolOptions> options,
		ILogger<CapacityPoolRegistrationHostedService> logger)
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
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		CapacityPoolOptions options = _options.Value;
		if (!options.Enabled)
		{
			return;
		}

		bool operatorSet = options is { PoolCpuCores: not null, PoolMemoryBytes: not null };
		double cpuCores = operatorSet ? options.PoolCpuCores!.Value : _hostCapabilities.AvailableCpuCores();
		long memoryBytes = operatorSet ? options.PoolMemoryBytes!.Value : _hostCapabilities.TotalMemoryBytes();

		try
		{
			await _pool.RegisterPoolCapacityAsync(_dispatcher.WorkerId, cpuCores, memoryBytes, operatorSet, cancellationToken).ConfigureAwait(false);
			LogPoolRegistered(operatorSet ? "operator" : "derived", cpuCores, memoryBytes);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			LogPoolRegistrationFailed(exception);
		}
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	[LoggerMessage(Level = LogLevel.Information, Message = "Capacity pool registered: source={Source}, {CpuCores} cores / {MemoryBytes} bytes shareable appliance capacity (ADR-0020)")]
	private partial void LogPoolRegistered(string source, double cpuCores, long memoryBytes);

	[LoggerMessage(Level = LogLevel.Error, Message = "Capacity pool registration failed; pool admission stays denied (fail safe) until a registration succeeds")]
	private partial void LogPoolRegistrationFailed(Exception exception);
}
