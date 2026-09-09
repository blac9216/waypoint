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
using Microsoft.Extensions.Logging;
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IReviewListService"/>
public sealed partial class ReviewListService : IReviewListService, IOutOfScopeContentEraser
{
	private readonly string _connectionString;
	private readonly IUnknownCatalogFileRepository _unknownCatalogFiles;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobEventPublisher? _events;
	private readonly ILogger<ReviewListService>? _logger;

	/// <summary>
	/// <paramref name="events"/> is optional (default null, same "best-effort
	/// observability, not every caller needs it" convention as
	/// <see cref="Waypoint.Infrastructure.Catalog.UnknownCatalogFileRepository"/>'s
	/// own <c>IJobEventPublisher?</c> constructor parameter). <paramref name="logger"/>
	/// is likewise optional -- issue #1819: <see cref="ListAsync"/> is a pure read
	/// and must never raise an alert, so the one signal it can still emit for an
	/// otherwise-impossible state (see <see cref="ListAsync"/>'s own doc comment) goes
	/// to structured logging, not the job-event alert channel <see cref="_events"/>
	/// backs.
	/// </summary>
	public ReviewListService(
		string connectionString,
		IUnknownCatalogFileRepository unknownCatalogFiles,
		IDepotArtifactRepository artifacts,
		IJobEventPublisher? events = null,
		ILogger<ReviewListService>? logger = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(unknownCatalogFiles);
		ArgumentNullException.ThrowIfNull(artifacts);

		_connectionString = connectionString;
		_unknownCatalogFiles = unknownCatalogFiles;
		_artifacts = artifacts;
		_events = events;
		_logger = logger;
	}

	public async Task<IReadOnlyList<ReviewListEntry>> ListAsync(CancellationToken cancellationToken)
	{
		List<ReviewListEntry> entries = [];

		// Orphans: unknown_catalog_files has no depot_artifacts row by definition
		// (that is what makes a file "unknown"), so DepotArtifactId is always null
		// here -- see this type's doc comment.
		IReadOnlyList<UnknownCatalogFile> orphans = await _unknownCatalogFiles.ListAsync(cancellationToken).ConfigureAwait(false);
		foreach (UnknownCatalogFile orphan in orphans)
		{
			entries.Add(new ReviewListEntry(
				ReviewListEntryKind.Orphan,
				DepotArtifactId: null,
				orphan.RelativePath,
				orphan.SizeBytes,
				Reason: null,
				orphan.FirstSeenAt,
				orphan.LastSeenAt));
		}

		// Out-of-scope: each row names a real depot_artifacts id (FK-enforced by
		// migration 0128), resolved here for its display path/size the same way
		// RetentionSweepService's own purge path resolves DepotArtifact.ExternalId
		// from a depot_artifact_id.
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT depot_artifact_id, reason, first_seen_at, last_seen_at
			FROM download_out_of_scope_content
			ORDER BY last_seen_at DESC, id
			""", connection);

		List<(Guid DepotArtifactId, string Reason, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt)> outOfScopeRows = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				outOfScopeRows.Add((
					reader.GetGuid(0),
					reader.GetString(1),
					reader.GetFieldValue<DateTimeOffset>(2),
					reader.GetFieldValue<DateTimeOffset>(3)));
			}
		}

		foreach ((Guid depotArtifactId, string reason, DateTimeOffset firstSeenAt, DateTimeOffset lastSeenAt) in outOfScopeRows)
		{
			DepotArtifact? artifact = await _artifacts.GetByIdAsync(depotArtifactId, cancellationToken).ConfigureAwait(false);
			if (artifact is null)
			{
				// The FK is ON DELETE CASCADE, so this should be unreachable in
				// practice -- but this is a safety-critical, never-auto-removed
				// list, and silently skipping means the row disappears from the one
				// surface whose entire purpose is that nothing disappears (issue
				// #1688). Issue #1819: ListAsync's own doc comment says it "never
				// raises an alert" -- a per-read SystemNotice job event (this
				// method's ONLY caller-visible read path, invoked by every poll of
				// GET /api/v1/download-retention/review-list) would flood the
				// alert channel in proportion to read traffic and contradict that
				// contract. A structured warning log is still loud (an impossible
				// state is never silent), but it is not an alert, so ListAsync
				// remains a side-effect-free read no matter how often it is called.
				if (_logger is not null)
				{
					LogUnresolvedOutOfScopeArtifact(_logger, depotArtifactId);
				}
				continue;
			}

			entries.Add(new ReviewListEntry(
				ReviewListEntryKind.OutOfScope,
				depotArtifactId,
				artifact.ExternalId,
				artifact.SizeBytes,
				reason,
				firstSeenAt,
				lastSeenAt));
		}

		return entries;
	}

	public async Task ReportOutOfScopeAsync(Guid depotArtifactId, string reason, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO download_out_of_scope_content (depot_artifact_id, reason)
			VALUES ($1, $2)
			ON CONFLICT (depot_artifact_id) DO UPDATE SET
				reason = EXCLUDED.reason,
				last_seen_at = now()
			RETURNING (xmax = 0) AS inserted
			""", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		command.Parameters.AddWithValue(reason);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		bool wasNewlyInserted = (bool)result!;

		if (wasNewlyInserted && _events is not null)
		{
			string payload = JsonSerializer.Serialize(new
			{
				kind = "download.retention.out_of_scope_reported",
				depot_artifact_id = depotArtifactId,
				reason,
			});
			await _events.EmitAsync(JobEventTypes.SystemNotice, null, null, payload, cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task<bool> IsOutOfScopeAsync(Guid depotArtifactId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT EXISTS (SELECT 1 FROM download_out_of_scope_content WHERE depot_artifact_id = $1)", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
	}

	public async Task<string?> GetOutOfScopeReasonAsync(Guid depotArtifactId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT reason FROM download_out_of_scope_content WHERE depot_artifact_id = $1", connection);
		command.Parameters.AddWithValue(depotArtifactId);

		// reason is NOT NULL (migration 0128), so a null scalar here means no row at
		// all -- exactly the "not on the list" answer this method's contract returns
		// null for, and the same answer IsOutOfScopeAsync would give as false.
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result as string;
	}

	/// <inheritdoc cref="IOutOfScopeContentEraser.EraseAsync"/>
	public async Task EraseAsync(Guid depotArtifactId, CancellationToken cancellationToken)
	{
		// Not exposed on IReviewListService (see that interface's own structural
		// never-deletes guarantee, ReviewListServiceTests.Interface_HasNoDeleteOrRemoveOrPurgeMethod)
		// -- this class also implements the separate, narrower IOutOfScopeContentEraser
		// seam (issue #1862) for RetentionSweepService's exclusive use. A missing row
		// deletes zero rows, which is success, not an error -- most purged artifacts
		// were never out-of-scope-reported at all.
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"DELETE FROM download_out_of_scope_content WHERE depot_artifact_id = $1", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "review-list: out-of-scope entry for depot artifact {DepotArtifactId} has no resolvable depot_artifacts row (should be unreachable under migration 0128's ON DELETE CASCADE FK) -- skipped, not shown")]
	private static partial void LogUnresolvedOutOfScopeArtifact(ILogger logger, Guid depotArtifactId);
}
