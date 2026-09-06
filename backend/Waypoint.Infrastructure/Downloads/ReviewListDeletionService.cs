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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IReviewListDeletionService"/>
public sealed partial class ReviewListDeletionService : IReviewListDeletionService
{
	private readonly string _connectionString;
	private readonly IRetainedContentStateRepository _states;
	private readonly IRetentionSweepService _sweep;
	private readonly IOptions<CatalogOptions> _catalogOptions;
	private readonly ILogger<ReviewListDeletionService> _logger;

	public ReviewListDeletionService(
		string connectionString,
		IRetainedContentStateRepository states,
		IRetentionSweepService sweep,
		IOptions<CatalogOptions> catalogOptions,
		ILogger<ReviewListDeletionService> logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(states);
		ArgumentNullException.ThrowIfNull(sweep);
		ArgumentNullException.ThrowIfNull(catalogOptions);
		ArgumentNullException.ThrowIfNull(logger);

		_connectionString = connectionString;
		_states = states;
		_sweep = sweep;
		_catalogOptions = catalogOptions;
		_logger = logger;
	}

	public async Task<ReviewListDeletionOutcome> DeleteAsync(
		ReviewListEntryKind kind, Guid? depotArtifactId, string? relativePath, string actor, string? reason, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(actor);

		return kind switch
		{
			ReviewListEntryKind.OutOfScope => await DeleteOutOfScopeAsync(depotArtifactId, actor, reason, cancellationToken).ConfigureAwait(false),
			ReviewListEntryKind.Orphan => await DeleteOrphanAsync(relativePath, actor, reason, cancellationToken).ConfigureAwait(false),
			_ => new ReviewListDeletionOutcome(false, $"unrecognized review-list entry kind '{kind}'."),
		};
	}

	private async Task<ReviewListDeletionOutcome> DeleteOutOfScopeAsync(Guid? depotArtifactId, string actor, string? reason, CancellationToken cancellationToken)
	{
		if (depotArtifactId is not { } id)
		{
			return new ReviewListDeletionOutcome(false, "a depot_artifact_id is required to delete an out-of-scope review-list entry.");
		}

		// Out-of-scope reports never call EnsureTrackedAsync (IReviewListService's own
		// doc comment) -- this explicit deletion is the first place that seam is
		// crossed, per IRetentionSweepService's own note that "the future API-process
		// caller that first names an artifact as retention-worthy (#1453) owns that
		// initial EnsureTrackedAsync call."
		Guid stateId = await _states.EnsureTrackedAsync(id, policyId: null, cancellationToken).ConfigureAwait(false);
		RetentionPurgeOutcome purgeOutcome = await _sweep.PurgeImmediatelyAsync(stateId, actor, reason, cancellationToken).ConfigureAwait(false);
		if (!purgeOutcome.Purged)
		{
			LogDeleteFailed(_logger, "out-of-scope", id.ToString(), actor, reason ?? "(none)", purgeOutcome.Error ?? "purge did not complete");
			return new ReviewListDeletionOutcome(false, purgeOutcome.Error ?? "purge did not complete.");
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand delete = new("DELETE FROM download_out_of_scope_content WHERE depot_artifact_id = $1", connection);
		delete.Parameters.AddWithValue(id);
		await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		LogDeleted(_logger, "out-of-scope", id.ToString(), actor, reason ?? "(none)");
		return new ReviewListDeletionOutcome(true, null);
	}

	private async Task<ReviewListDeletionOutcome> DeleteOrphanAsync(string? relativePath, string actor, string? reason, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
		{
			return new ReviewListDeletionOutcome(false, "a relative_path is required to delete an orphan review-list entry.");
		}

		(bool deleted, string? deleteError) = DeletePhysicalFile(relativePath);
		if (deleteError is not null)
		{
			LogDeleteFailed(_logger, "orphan", relativePath, actor, reason ?? "(none)", deleteError);
			return new ReviewListDeletionOutcome(false, deleteError);
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand delete = new("DELETE FROM unknown_catalog_files WHERE relative_path = $1", connection);
		delete.Parameters.AddWithValue(relativePath);
		await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

		LogDeleted(_logger, "orphan", relativePath, actor, reason ?? "(none)");
		return new ReviewListDeletionOutcome(true, null);
	}

	/// <summary>
	/// Same path-confinement defense as <c>RetentionSweepService.DeletePhysicalFileAsync</c>
	/// -- re-confines the resolved full path beneath <see cref="CatalogOptions.DepotPath"/>
	/// before any delete. An already-absent file counts as a successful delete
	/// (idempotent, matching the same convention).
	/// </summary>
	private (bool Deleted, string? Error) DeletePhysicalFile(string relativePath)
	{
		string root = _catalogOptions.Value.DepotPath;
		string fullRoot = Path.GetFullPath(root);
		string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
		bool confined = fullPath.StartsWith(fullRoot, StringComparison.Ordinal)
			&& (fullPath.Length == fullRoot.Length || fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
		if (!confined)
		{
			return (false, $"refused to delete '{relativePath}': resolves outside the configured depot root.");
		}

		try
		{
			if (File.Exists(fullPath))
			{
				File.Delete(fullPath);
			}

			return (true, null);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (false, $"'{relativePath}': {exception.Message}");
		}
	}

	[LoggerMessage(Level = LogLevel.Information, Message = "review-list: deleted {Kind} entry '{Identifier}', actor={Actor}, reason={Reason}")]
	private static partial void LogDeleted(ILogger logger, string kind, string identifier, string actor, string reason);

	[LoggerMessage(Level = LogLevel.Error, Message = "review-list: delete FAILED for {Kind} entry '{Identifier}', actor={Actor}, reason={Reason}: {Error}")]
	private static partial void LogDeleteFailed(ILogger logger, string kind, string identifier, string actor, string reason, string error);
}
