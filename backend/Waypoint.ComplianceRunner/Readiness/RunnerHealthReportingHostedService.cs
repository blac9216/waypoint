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

using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Waypoint.Core.SystemState;
using Waypoint.Runner.Jobs;
using Waypoint.Runner.Readiness;
using Waypoint.Runner.Resources;

namespace Waypoint.ComplianceRunner.Readiness;

/// <summary>
/// Periodically evaluates <see cref="ComplianceReadinessCheck"/>, pairs the verdict with
/// this host's registered capabilities (<see cref="JobHandlerRegistry.AllowedJobTypes"/>),
/// and writes the result via the shared <see cref="RunnerReadinessReportingHostedService{TReport}"/>
/// (issue #461) to <see cref="RunnerHealthOptions.ReportFilePath"/> for
/// <see cref="RunnerHealthCheckProbe"/> to read back. See <see cref="RunnerHealthOptions"/>
/// for why a file sentinel rather than an HTTP endpoint.
///
/// This type is a thin domain-specific wrapper: it owns exactly the compliance-runner's
/// readiness evaluation and payload shape, and delegates the write-then-move sentinel
/// mechanics, StartAsync/ExecuteAsync scheduling, and worker_registry heartbeat to the
/// shared <see cref="RunnerReadinessReportingHostedService{TReport}"/> it composes.
/// </summary>
public sealed class RunnerHealthReportingHostedService : IHostedService, IDisposable
{
	private readonly ComplianceReadinessCheck _readiness;
	private readonly JobHandlerRegistry _handlers;
	private readonly ResourceAdmissionController _resourceAdmission;
	private readonly RunnerReadinessReportingHostedService<RunnerHealthReport> _inner;

	/// <summary>
	/// <paramref name="workerRegistry"/> is nullable and optional: Program.cs only
	/// registers it when a connection string is configured (see
	/// <c>Waypoint.DownloadRunner.ReadinessReportingHostedService</c>'s matching
	/// constructor doc comment for the "no connection string, no wiring" reasoning this
	/// mirrors).
	/// </summary>
	public RunnerHealthReportingHostedService(
		ComplianceReadinessCheck readiness,
		JobHandlerRegistry handlers,
		IOptions<RunnerHealthOptions> options,
		ResourceAdmissionController resourceAdmission,
		IWorkerRegistryWriter? workerRegistry,
		ILogger<RunnerHealthReportingHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(readiness);
		ArgumentNullException.ThrowIfNull(handlers);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(resourceAdmission);
		ArgumentNullException.ThrowIfNull(logger);

		_readiness = readiness;
		_handlers = handlers;
		_resourceAdmission = resourceAdmission;

		_inner = new RunnerReadinessReportingHostedService<RunnerHealthReport>(
			BuildReportAsync,
			report => report.Ready,
			report => report.Capabilities,
			Options.Create<IRunnerReadinessOptions>(options.Value),
			workerRegistry,
			logger,
			starvedJobTypes: report => report.Capacity?.StarvedJobTypes.Select(starved => new StarvedJobType(starved.JobType, starved.Permanent)).ToArray() ?? []);
	}

	public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

	public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

	public void Dispose() => _inner.Dispose();

	private Task<RunnerHealthReport> BuildReportAsync(CancellationToken cancellationToken)
	{
		ReadinessReport readiness = _readiness.Evaluate();
		RunnerHealthReport report = new(
			Ready: readiness.Ready,
			Capabilities: [.. _handlers.AllowedJobTypes.Order(StringComparer.Ordinal)],
			Problems: readiness.Problems,
			Capacity: RunnerCapacityReportFactory.FromController(_resourceAdmission),
			Timestamp: DateTimeOffset.UtcNow);
		return Task.FromResult(report);
	}
}
