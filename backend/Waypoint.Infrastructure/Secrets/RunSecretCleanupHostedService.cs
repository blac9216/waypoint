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
using Waypoint.Core.Secrets;

namespace Waypoint.Infrastructure.Secrets;

/// <summary>
/// Issue #434 AC "expiry + cleanup sweep for abandoned runs": periodically deletes
/// <c>run_secrets</c> rows whose <c>expires_at</c> has passed via
/// <see cref="IRunSecretStore.DeleteExpiredAsync"/>. Structurally mirrors
/// <see cref="Waypoint.Runner.Jobs.LeaseRecoveryHostedService"/> -- an
/// independent periodic sweep, safe to run from more than one Waypoint instance at
/// once (each pass's <c>FOR UPDATE SKIP LOCKED</c> makes concurrent sweeps disjoint,
/// not merely non-corrupting -- see <see cref="RunSecretStore.DeleteExpiredAsync"/>).
///
/// Runs unconditionally rather than behind <c>JobEngineOptions.Enabled</c>: the run
/// secret it protects is a security-sensitive persisted blob, independent of whether
/// this process also runs the job dispatcher/lease-recovery sweeps (a future
/// runner-only or API-only deployment topology could disable those while still wanting
/// this cleanup live wherever a connection string is configured).
/// </summary>
public sealed partial class RunSecretCleanupHostedService : BackgroundService
{
	private readonly IRunSecretStore _store;
	private readonly IOptions<RunSecretOptions> _options;
	private readonly ILogger<RunSecretCleanupHostedService> _logger;

	public RunSecretCleanupHostedService(IRunSecretStore store, IOptions<RunSecretOptions> options, ILogger<RunSecretCleanupHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_store = store;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		RunSecretOptions options = _options.Value;
		LogCleanupStarting(options.CleanupInterval, options.Expiry);

		using PeriodicTimer timer = new(options.CleanupInterval);
		do
		{
			await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
		}
		while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
	}

	/// <summary>One sweep pass, exposed for tests -- mirrors <see cref="Waypoint.Runner.Jobs.LeaseRecoveryHostedService.SweepAsync"/>'s test seam.</summary>
	internal async Task SweepOnceAsync(CancellationToken cancellationToken)
	{
		try
		{
			int deleted = await _store.DeleteExpiredAsync(cancellationToken).ConfigureAwait(false);
			if (deleted > 0)
			{
				LogSwept(deleted);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			LogSweepFailed(exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "Run secret cleanup sweeping every {Interval}, expiry {Expiry}")]
	private partial void LogCleanupStarting(TimeSpan interval, TimeSpan expiry);

	[LoggerMessage(Level = LogLevel.Information, Message = "Run secret cleanup swept {Count} abandoned row(s)")]
	private partial void LogSwept(int count);

	[LoggerMessage(Level = LogLevel.Error, Message = "Run secret cleanup sweep failed")]
	private partial void LogSweepFailed(Exception exception);
}
