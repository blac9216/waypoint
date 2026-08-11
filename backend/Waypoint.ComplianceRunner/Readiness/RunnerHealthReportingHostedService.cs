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
using Waypoint.Runner.Jobs;

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// Periodically evaluates <see cref="ComplianceReadinessCheck"/>, pairs the verdict with
/// this host's registered capabilities (<see cref="JobHandlerRegistry.AllowedJobTypes"/>),
/// and writes the result to <see cref="RunnerHealthOptions.ReportFilePath"/> for
/// <see cref="RunnerHealthCheckProbe"/> to read back. See <see cref="RunnerHealthOptions"/>
/// for why a file sentinel rather than an HTTP endpoint.
///
/// Runs once immediately at startup (before the first <see cref="RunnerHealthOptions.RefreshInterval"/>
/// tick) so a Compose healthcheck polling shortly after container start does not see a
/// missing file and report unhealthy for the first refresh interval.
/// </summary>
public sealed partial class RunnerHealthReportingHostedService : BackgroundService
{
	private readonly ComplianceReadinessCheck _readiness;
	private readonly JobHandlerRegistry _handlers;
	private readonly IOptions<RunnerHealthOptions> _options;
	private readonly ILogger<RunnerHealthReportingHostedService> _logger;

	public RunnerHealthReportingHostedService(
		ComplianceReadinessCheck readiness,
		JobHandlerRegistry handlers,
		IOptions<RunnerHealthOptions> options,
		ILogger<RunnerHealthReportingHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(readiness);
		ArgumentNullException.ThrowIfNull(handlers);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_readiness = readiness;
		_handlers = handlers;
		_options = options;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		RunnerHealthOptions options = _options.Value;

		while (!stoppingToken.IsCancellationRequested)
		{
			WriteReport(options.ReportFilePath);

			try
			{
				await Task.Delay(options.RefreshInterval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Host stopping; loop condition ends the loop next.
			}
		}
	}

	private void WriteReport(string reportFilePath)
	{
		ReadinessReport readiness = _readiness.Evaluate();
		RunnerHealthReport report = new(
			Ready: readiness.Ready,
			Capabilities: [.. _handlers.AllowedJobTypes.Order(StringComparer.Ordinal)],
			Problems: readiness.Problems,
			Timestamp: DateTimeOffset.UtcNow);

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
			File.WriteAllText(tempPath, json);
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
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write runner health report to '{ReportFilePath}'")]
	private partial void LogReportWriteFailed(string reportFilePath, Exception exception);
}
