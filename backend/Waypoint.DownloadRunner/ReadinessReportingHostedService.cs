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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Infrastructure.DependencyInjection;

namespace Waypoint.DownloadRunner;

/// <summary>
/// Periodically probes this host's required dependencies (database, artifact store,
/// depot path) and the optional managed tool, then writes a <see cref="ReadinessSnapshot"/>
/// to <see cref="DownloadRunnerOptions.ReadinessFilePath"/> for <c>--health-check</c>
/// to read back (see <see cref="HealthCheckProbe"/>'s doc comment for why this is a
/// file and not an HTTP endpoint). ADR-0014 §7: "runner readiness must include
/// registered capabilities and allocated-resource discovery; it must fail closed when
/// required dependencies or mounts are unavailable" -- a database or artifact-store
/// failure makes <see cref="ReadinessSnapshot.Ready"/> false; a missing managed tool
/// does not (see <see cref="ReadinessSnapshot"/>'s doc comment).
/// </summary>
public sealed partial class ReadinessReportingHostedService : BackgroundService
{
	private readonly string? _connectionString;
	private readonly IManagedToolPresenceChecker _toolPresence;
	private readonly IOptions<DownloadOptions> _downloadOptions;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly IOptions<DownloadRunnerOptions> _runnerOptions;
	private readonly ILogger<ReadinessReportingHostedService> _logger;

	public ReadinessReportingHostedService(
		IConfiguration configuration,
		IManagedToolPresenceChecker toolPresence,
		IOptions<DownloadOptions> downloadOptions,
		IOptions<CatalogOptions> catalogOptions,
		IOptions<DownloadRunnerOptions> runnerOptions,
		ILogger<ReadinessReportingHostedService> logger)
	{
		ArgumentNullException.ThrowIfNull(configuration);
		ArgumentNullException.ThrowIfNull(toolPresence);
		ArgumentNullException.ThrowIfNull(downloadOptions);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(runnerOptions);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = configuration.GetConnectionString(ServiceCollectionExtensions.ConnectionStringName);
		_toolPresence = toolPresence;
		_downloadOptions = downloadOptions;
		_catalogOptions = catalogOptions;
		_runnerOptions = runnerOptions;
		_logger = logger;
	}

	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		// Write the first snapshot synchronously as part of startup (awaited before
		// the host reports started) rather than deferring it into the fire-and-forget
		// ExecuteAsync loop. This makes a fresh container's very first health probe
		// meaningful -- the readiness file exists the moment StartAsync returns -- and
		// makes the reporting deterministic for callers (and tests) that stop the
		// service immediately after starting it.
		await WriteSnapshotAsync(cancellationToken).ConfigureAwait(false);
		await base.StartAsync(cancellationToken).ConfigureAwait(false);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		DownloadRunnerOptions options = _runnerOptions.Value;
		using PeriodicTimer timer = new(options.ReadinessInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
			{
				await WriteSnapshotAsync(stoppingToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// Expected on host shutdown.
		}
	}

	private async Task WriteSnapshotAsync(CancellationToken cancellationToken)
	{
		bool databaseReachable = await CheckDatabaseAsync(cancellationToken).ConfigureAwait(false);
		bool artifactStoreWritable = CheckDirectoryWritable(_downloadOptions.Value.ArtifactStorePath);
		bool depotPathReadable = Directory.Exists(_catalogOptions.Value.DepotPath);
		bool toolPresent = _toolPresence.IsPresent();

		// Deliberately excludes toolPresent -- see ReadinessSnapshot's doc comment.
		bool ready = databaseReachable && artifactStoreWritable && depotPathReadable;

		ReadinessSnapshot snapshot = new(
			Ready: ready,
			JobTypes: [.. DownloadRunnerJobTypes.Allowed],
			ToolPresent: toolPresent,
			ArtifactStoreWritable: artifactStoreWritable,
			DepotPathReadable: depotPathReadable,
			GeneratedAt: DateTimeOffset.UtcNow);

		try
		{
			string json = JsonSerializer.Serialize(snapshot);
			string path = _runnerOptions.Value.ReadinessFilePath;
			string? directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			// Write-then-move: --health-check must never observe a half-written file.
			string tempPath = path + ".tmp";
			await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
			File.Move(tempPath, path, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			LogReadinessWriteFailed(exception);
		}
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

	[LoggerMessage(Level = LogLevel.Error, Message = "Failed to write the readiness marker file")]
	private partial void LogReadinessWriteFailed(Exception exception);
}
