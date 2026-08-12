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
using Waypoint.Core.SystemState;

namespace Waypoint.Runner.Readiness;

/// <summary>
/// The write-then-move file-sentinel readiness reporter shared by every runner host
/// (issue #461: unifies what were previously two independently-invented, near-identical
/// implementations -- the download-runner's <c>ReadinessReportingHostedService</c> and
/// the compliance-runner's <c>RunnerHealthReportingHostedService</c>). What differs
/// between hosts is only the report payload shape and how "ready" plus the claimed job
/// types are derived from it; both are supplied by the caller as <typeparamref name="TReport"/>
/// plus the constructor delegates below (issue #461's "small per-domain contribution
/// contract").
///
/// Writes the first report in <see cref="StartAsync"/> (awaited before the host reports
/// started, ahead of the first <see cref="IRunnerReadinessOptions.RefreshInterval"/>
/// tick) so a Compose healthcheck polling shortly after container start does not see a
/// missing file and report unhealthy for the first refresh interval -- and so a
/// StopAsync immediately after start cannot race the file's existence.
/// </summary>
/// <typeparam name="TReport">The host-specific, JSON-serializable readiness report shape.</typeparam>
public sealed partial class RunnerReadinessReportingHostedService<TReport> : BackgroundService
{
	private readonly Func<CancellationToken, Task<TReport>> _buildReport;
	private readonly Func<TReport, bool> _isReady;
	private readonly Func<TReport, IReadOnlyList<string>> _jobTypes;
	private readonly IOptions<IRunnerReadinessOptions> _options;
	private readonly IWorkerRegistryWriter? _workerRegistry;
	private readonly ILogger _logger;

	/// <summary>
	/// <paramref name="workerRegistry"/> is nullable and optional: every runner host's
	/// Program.cs registers it only when a connection string is configured -- a runner
	/// with no database configured has nothing to heartbeat to and nothing to dispatch
	/// from either. The file-based readiness sentinel this service writes still reports
	/// its own <c>Ready: false</c> in that case via whatever the caller's
	/// <paramref name="buildReport"/> derives from the missing connection.
	/// </summary>
	/// <param name="buildReport">Evaluates this host's domain-specific readiness and builds the report to serialize. Never expected to throw.</param>
	/// <param name="isReady">Extracts the report's overall ready/not-ready verdict, for the worker_registry heartbeat.</param>
	/// <param name="jobTypes">Extracts the report's claimed job-type list, for the worker_registry heartbeat.</param>
	/// <remarks>
	/// <paramref name="logger"/> is a plain <see cref="ILogger"/>, not
	/// <c>ILogger&lt;RunnerReadinessReportingHostedService&lt;TReport&gt;&gt;</c> --
	/// each domain wrapper passes its own <c>ILogger&lt;TWrapper&gt;</c> through
	/// unchanged so log lines keep that host's existing, already-documented category
	/// (e.g. <c>Waypoint.DownloadRunner.ReadinessReportingHostedService</c>) rather
	/// than a generic-typed category naming this shared implementation detail.
	/// </remarks>
	public RunnerReadinessReportingHostedService(
		Func<CancellationToken, Task<TReport>> buildReport,
		Func<TReport, bool> isReady,
		Func<TReport, IReadOnlyList<string>> jobTypes,
		IOptions<IRunnerReadinessOptions> options,
		IWorkerRegistryWriter? workerRegistry,
		ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(buildReport);
		ArgumentNullException.ThrowIfNull(isReady);
		ArgumentNullException.ThrowIfNull(jobTypes);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_buildReport = buildReport;
		_isReady = isReady;
		_jobTypes = jobTypes;
		_options = options;
		_workerRegistry = workerRegistry;
		_logger = logger;
	}

	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		// Write the first report synchronously as part of startup (awaited before the
		// host reports started) rather than deferring it into the fire-and-forget
		// ExecuteAsync loop. This makes a fresh container's very first health probe
		// meaningful -- the report file exists the moment StartAsync returns -- and
		// makes the reporting deterministic for callers (and tests) that stop the
		// service immediately after starting it, closing the race a first write inside
		// ExecuteAsync leaves open (an immediate StopAsync could otherwise beat the
		// file into existence).
		TReport report = await WriteReportAsync(cancellationToken).ConfigureAwait(false);
		await HeartbeatWorkerRegistryAsync(report, cancellationToken).ConfigureAwait(false);
		await base.StartAsync(cancellationToken).ConfigureAwait(false);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		IRunnerReadinessOptions options = _options.Value;

		// The first report was already written in StartAsync; subsequent refreshes are
		// spaced by RefreshInterval, so wait before writing again rather than
		// re-writing immediately here.
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(options.RefreshInterval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Host stopping; loop condition ends the loop next.
				break;
			}

			TReport report = await WriteReportAsync(stoppingToken).ConfigureAwait(false);
			await HeartbeatWorkerRegistryAsync(report, stoppingToken).ConfigureAwait(false);
		}
	}

	private async Task<TReport> WriteReportAsync(CancellationToken cancellationToken)
	{
		TReport report = await _buildReport(cancellationToken).ConfigureAwait(false);
		string reportFilePath = _options.Value.ReportFilePath;

		try
		{
			string json = JsonSerializer.Serialize(report);
			string? directory = Path.GetDirectoryName(reportFilePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			// Write-then-move: --health-check must never observe a half-written file.
			string tempPath = $"{reportFilePath}.tmp-{Guid.NewGuid():N}";
			await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
			File.Move(tempPath, reportFilePath, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The report file itself is unwritable -- log it, but do not crash the
			// dispatcher over a health-reporting side channel. A --health-check probe
			// that finds no file (or a stale one) already fails closed (see
			// RunnerHealthCheckProbe), which is the correct outcome here too.
			LogReportWriteFailed(reportFilePath, exception);
		}

		return report;
	}

	/// <summary>
	/// Issue #443: the database-persisted twin of the file-based report above. GET
	/// /system reads this table, not the file -- the file exists for this container's
	/// own health probe (no HTTP listener to ask instead), the table exists so the
	/// control plane can report per-domain runner availability without ever reaching
	/// into another container's filesystem. Best-effort, same as the file write: a
	/// failed heartbeat write just means this cycle's presence is not recorded, and
	/// worker_registry's own staleness read handles that the same way a missed job
	/// lease heartbeat does.
	/// </summary>
	private async Task HeartbeatWorkerRegistryAsync(TReport report, CancellationToken cancellationToken)
	{
		if (_workerRegistry is null)
		{
			return;
		}

		try
		{
			await _workerRegistry.HeartbeatAsync(
				_options.Value.WorkerId,
				_jobTypes(report),
				_isReady(report),
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			LogWorkerHeartbeatFailed(exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write runner health report to '{ReportFilePath}'")]
	private partial void LogReportWriteFailed(string reportFilePath, Exception exception);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write this worker's heartbeat to worker_registry")]
	private partial void LogWorkerHeartbeatFailed(Exception exception);
}
