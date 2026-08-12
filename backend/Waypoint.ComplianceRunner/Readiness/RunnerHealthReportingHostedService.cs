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
using Waypoint.Runner.Resources;

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// Periodically evaluates <see cref="ComplianceReadinessCheck"/>, pairs the verdict with
/// this host's registered capabilities (<see cref="JobHandlerRegistry.AllowedJobTypes"/>),
/// and writes the result to <see cref="RunnerHealthOptions.ReportFilePath"/> for
/// <see cref="RunnerHealthCheckProbe"/> to read back. See <see cref="RunnerHealthOptions"/>
/// for why a file sentinel rather than an HTTP endpoint.
///
/// Writes the first report in <see cref="StartAsync"/> (awaited before the host reports
/// started, ahead of the first <see cref="RunnerHealthOptions.RefreshInterval"/> tick) so a
/// Compose healthcheck polling shortly after container start does not see a missing file and
/// report unhealthy for the first refresh interval -- and so a StopAsync immediately after
/// start cannot race the file's existence.
/// </summary>
public sealed partial class RunnerHealthReportingHostedService : BackgroundService
{
	private readonly ComplianceReadinessCheck _readiness;
	private readonly JobHandlerRegistry _handlers;
	private readonly IOptions<RunnerHealthOptions> _options;
	private readonly ResourceAdmissionController _resourceAdmission;
	private readonly ILogger<RunnerHealthReportingHostedService> _logger;

	public RunnerHealthReportingHostedService(
		ComplianceReadinessCheck readiness,
		JobHandlerRegistry handlers,
		IOptions<RunnerHealthOptions> options,
		ResourceAdmissionController resourceAdmission,
		ILogger<RunnerHealthReportingHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(readiness);
		ArgumentNullException.ThrowIfNull(handlers);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(resourceAdmission);
		ArgumentNullException.ThrowIfNull(logger);

		_readiness = readiness;
		_handlers = handlers;
		_options = options;
		_resourceAdmission = resourceAdmission;
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
		// file into existence). Mirrors the download-runner's ReadinessReportingHostedService.
		WriteReport(_options.Value.ReportFilePath);
		await base.StartAsync(cancellationToken).ConfigureAwait(false);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		RunnerHealthOptions options = _options.Value;

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

			WriteReport(options.ReportFilePath);
		}
	}

	private void WriteReport(string reportFilePath)
	{
		ReadinessReport readiness = _readiness.Evaluate();
		RunnerHealthReport report = new(
			Ready: readiness.Ready,
			Capabilities: [.. _handlers.AllowedJobTypes.Order(StringComparer.Ordinal)],
			Problems: readiness.Problems,
			Capacity: BuildCapacityReport(),
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

	/// <summary>Issue #437: snapshots <see cref="ResourceAdmissionController"/>'s current discovered/effective/admitted state for the health report.</summary>
	private RunnerCapacityReport BuildCapacityReport() => new(
		Source: _resourceAdmission.Discovered.Source.ToString(),
		IsFallback: _resourceAdmission.Discovered.IsFallback,
		DiscoveredCpuCores: _resourceAdmission.Discovered.CpuCores,
		DiscoveredMemoryBytes: _resourceAdmission.Discovered.MemoryBytes,
		EffectiveCpuCores: _resourceAdmission.EffectiveBudget.CpuCores,
		EffectiveMemoryBytes: _resourceAdmission.EffectiveBudget.MemoryBytes,
		AdmittedCpuCores: _resourceAdmission.AdmittedCpuCores,
		AdmittedMemoryBytes: _resourceAdmission.AdmittedMemoryBytes,
		AdmittedJobCount: _resourceAdmission.AdmittedJobCount);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write runner health report to '{ReportFilePath}'")]
	private partial void LogReportWriteFailed(string reportFilePath, Exception exception);
}
