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
using Npgsql;
using Waypoint.Core.Catalog;
using Waypoint.Core.Downloads;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IReviewListService"/>
public sealed class ReviewListService : IReviewListService
{
	private readonly string _connectionString;
	private readonly IUnknownCatalogFileRepository _unknownCatalogFiles;
	private readonly IDepotArtifactRepository _artifacts;
	private readonly IJobEventPublisher? _events;

	/// <summary>
	/// <paramref name="events"/> is optional (default null, same "best-effort
	/// observability, not every caller needs it" convention as
	/// <see cref="Waypoint.Infrastructure.Catalog.UnknownCatalogFileRepository"/>'s
	/// own <c>IJobEventPublisher?</c> constructor parameter).
	/// </summary>
	public ReviewListService(
		string connectionString,
		IUnknownCatalogFileRepository unknownCatalogFiles,
		IDepotArtifactRepository artifacts,
		IJobEventPublisher? events = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		ArgumentNullException.ThrowIfNull(unknownCatalogFiles);
		ArgumentNullException.ThrowIfNull(artifacts);

		_connectionString = connectionString;
		_unknownCatalogFiles = unknownCatalogFiles;
		_artifacts = artifacts;
		_events = events;
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
				// practice; skip defensively rather than surface a null path.
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
}
