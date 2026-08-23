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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;
using Waypoint.Core.SystemState;
using Waypoint.Infrastructure.DependencyInjection;
using Waypoint.Runner.Readiness;
using Waypoint.Runner.Resources;

namespace Waypoint.DownloadRunner;

/// <summary>
/// Periodically probes this host's required dependencies (database, artifact store,
/// depot path) and the optional managed tool, then writes a <see cref="ReadinessSnapshot"/>
/// via the shared <see cref="RunnerReadinessReportingHostedService{TReport}"/> (issue
/// #461) to <see cref="DownloadRunnerOptions.ReadinessFilePath"/> for
/// <c>--health-check</c> to read back (see <see cref="HealthCheckProbe"/>'s doc comment
/// for why this is a file and not an HTTP endpoint). ADR-0014 §7: "runner readiness
/// must include registered capabilities and allocated-resource discovery; it must fail
/// closed when required dependencies or mounts are unavailable" -- a database or
/// artifact-store failure makes <see cref="ReadinessSnapshot.Ready"/> false; a missing
/// managed tool does not (see <see cref="ReadinessSnapshot"/>'s doc comment).
///
/// This type is a thin domain-specific wrapper: it owns exactly the download-runner's
/// dependency checks and payload shape, and delegates the write-then-move sentinel
/// mechanics, StartAsync/ExecuteAsync scheduling, and worker_registry heartbeat to the
/// shared <see cref="RunnerReadinessReportingHostedService{TReport}"/> it composes.
/// </summary>
public sealed partial class ReadinessReportingHostedService : IHostedService, IDisposable
{
	private readonly string? _connectionString;
	private readonly IManagedToolPresenceChecker _toolPresence;
	private readonly IOptions<DownloadOptions> _downloadOptions;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly ResourceAdmissionController _resourceAdmission;
	private readonly ILogger<ReadinessReportingHostedService> _logger;
	private readonly RunnerReadinessReportingHostedService<ReadinessSnapshot> _inner;

	/// <summary>
	/// <paramref name="workerRegistry"/> is nullable and optional (constructor-injected
	/// via <c>IWorkerRegistryWriter?</c>, resolved to null when nothing is registered)
	/// -- Program.cs only registers it when a connection string is configured, mirroring
	/// every other "no connection string, no wiring" registration in this codebase. A
	/// runner with no database configured has nothing to heartbeat to and nothing to
	/// dispatch from either; the file-based readiness snapshot this service already
	/// writes still reports <c>Ready: false</c> in that case via <c>databaseReachable</c>.
	/// </summary>
	public ReadinessReportingHostedService(
		IConfiguration configuration,
		IManagedToolPresenceChecker toolPresence,
		IOptions<DownloadOptions> downloadOptions,
		IOptions<CatalogOptions> catalogOptions,
		IOptions<DownloadRunnerOptions> runnerOptions,
		ResourceAdmissionController resourceAdmission,
		IWorkerRegistryWriter? workerRegistry,
		ILogger<ReadinessReportingHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(toolPresence);
		ArgumentNullException.ThrowIfNull(downloadOptions);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(runnerOptions);
		ArgumentNullException.ThrowIfNull(resourceAdmission);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = configuration.GetConnectionString(ServiceCollectionExtensions.ConnectionStringName);
		_toolPresence = toolPresence;
		_downloadOptions = downloadOptions;
		_catalogOptions = catalogOptions;
		_resourceAdmission = resourceAdmission;
		_logger = logger;

		_inner = new RunnerReadinessReportingHostedService<ReadinessSnapshot>(
			BuildSnapshotAsync,
			snapshot => snapshot.Ready,
			snapshot => snapshot.JobTypes,
			Options.Create<IRunnerReadinessOptions>(runnerOptions.Value),
			workerRegistry,
			logger,
			canHeartbeat: snapshot => snapshot.DatabaseReachable,
			starvedJobTypes: snapshot => snapshot.Capacity?.StarvedJobTypes.Select(starved => new StarvedJobType(starved.JobType, starved.Permanent)).ToArray() ?? [],
			// Issue #560: worker_registry.tool_present feeds GET /downloads/readiness's
			// combined tool-installed + depot-token-valid answer.
			toolPresent: snapshot => snapshot.ToolPresent);
	}

	public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);

	public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);

	public void Dispose() => _inner.Dispose();

	private async Task<ReadinessSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
	{
		bool databaseReachable = await CheckDatabaseAsync(cancellationToken).ConfigureAwait(false);
		bool artifactStoreWritable = CheckDirectoryWritable(_downloadOptions.Value.ArtifactStorePath);
		bool depotPathReadable = Directory.Exists(_catalogOptions.Value.DepotPath);
		bool toolPresent = _toolPresence.IsPresent();

		// Deliberately excludes toolPresent -- see ReadinessSnapshot's doc comment.
		bool ready = databaseReachable && artifactStoreWritable && depotPathReadable;

		return new ReadinessSnapshot(
			Ready: ready,
			JobTypes: [.. DownloadRunnerJobTypes.Allowed],
			ToolPresent: toolPresent,
			ArtifactStoreWritable: artifactStoreWritable,
			DepotPathReadable: depotPathReadable,
			DatabaseReachable: databaseReachable,
			Capacity: RunnerCapacityReportFactory.FromController(_resourceAdmission),
			GeneratedAt: DateTimeOffset.UtcNow);
	}

	private async Task<bool> CheckDatabaseAsync(CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(_connectionString))
		{
			return false;
		}

		try
		{
			await using NpgsqlConnection connection = new(_connectionString);
			await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			LogDatabaseUnreachable(exception);
			return false;
		}
	}

	private static bool CheckDirectoryWritable(string path)
	{
		try
		{
			Directory.CreateDirectory(path);
			string probePath = Path.Combine(path, $".waypoint-writable-probe-{Guid.NewGuid():N}");
			File.WriteAllBytes(probePath, []);
			File.Delete(probePath);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Readiness probe could not reach the database")]
	private partial void LogDatabaseUnreachable(Exception exception);
}
