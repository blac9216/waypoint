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

using Npgsql;
using Waypoint.Core.Downloads;
using Waypoint.Core.Pagination;

namespace Waypoint.Infrastructure.Downloads;

/// <inheritdoc cref="IDownloadRepository"/>
public sealed class DownloadRepository : IDownloadRepository
{
	private const string ProjectionSql = """
		SELECT id, depot_artifact_id, job_id, run_id, state, bytes_total, bytes_downloaded,
		       download_rate_bps, eta_seconds, retry_count, max_retries, failure_reason,
		       requested_by, created_at, updated_at, completed_at
		FROM downloads
		""";

	private readonly string _connectionString;

	public DownloadRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid> CreateAsync(Guid depotArtifactId, Guid? jobId, string? requestedBy, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO downloads (depot_artifact_id, job_id, requested_by)
			VALUES ($1, $2, $3)
			RETURNING id
			""", connection);
		command.Parameters.AddWithValue(depotArtifactId);
		command.Parameters.AddWithValue((object?)jobId ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)requestedBy ?? DBNull.Value);

		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (Guid)result!;
	}

	public async Task SetJobAsync(Guid downloadId, Guid jobId, Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("UPDATE downloads SET job_id = $1, run_id = $2 WHERE id = $3", connection);
		command.Parameters.AddWithValue(jobId);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(downloadId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<Download?> GetAsync(Guid downloadId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE id = $1", connection);
		command.Parameters.AddWithValue(downloadId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task<Download?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new($"{ProjectionSql} WHERE job_id = $1 ORDER BY created_at DESC LIMIT 1", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
	}

	public async Task UpdateProgressAsync(
		Guid downloadId,
		string state,
		long? bytesTotal,
		long? bytesDownloaded,
		long? downloadRateBps,
		int? etaSeconds,
		string? failureReason,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(state);

		bool terminal = state is DownloadStates.Verified or DownloadStates.Failed or DownloadStates.Cancelled;

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			UPDATE downloads SET
				state = $1,
				bytes_total = COALESCE($2, bytes_total),
				bytes_downloaded = COALESCE($3, bytes_downloaded),
				download_rate_bps = COALESCE($4, download_rate_bps),
				eta_seconds = COALESCE($5, eta_seconds),
				failure_reason = COALESCE($6, failure_reason),
				completed_at = CASE WHEN $7 THEN now() ELSE completed_at END
			WHERE id = $8
			""", connection);
		command.Parameters.AddWithValue(state);
		command.Parameters.AddWithValue((object?)bytesTotal ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)bytesDownloaded ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)downloadRateBps ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)etaSeconds ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)failureReason ?? DBNull.Value);
		command.Parameters.AddWithValue(terminal);
		command.Parameters.AddWithValue(downloadId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<int> IncrementRetryCountAsync(Guid downloadId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE downloads SET retry_count = retry_count + 1 WHERE id = $1 RETURNING retry_count", connection);
		command.Parameters.AddWithValue(downloadId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return (int)result!;
	}

	public async Task<(IReadOnlyList<Download> Items, long TotalCount)> ListAsync(PageRequest page, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		long total;
		await using (NpgsqlCommand countCommand = new("SELECT count(*) FROM downloads", connection))
		{
			total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<Download> items = [];
		await using (NpgsqlCommand listCommand = new($"{ProjectionSql} ORDER BY created_at DESC, id LIMIT $1 OFFSET $2", connection))
		{
			listCommand.Parameters.AddWithValue(page.Limit);
			listCommand.Parameters.AddWithValue(page.Offset);
			await using NpgsqlDataReader reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				items.Add(Map(reader));
			}
		}

		return (items, total);
	}

	private static Download Map(NpgsqlDataReader reader)
	{
		return new Download(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.IsDBNull(2) ? null : reader.GetGuid(2),
			reader.IsDBNull(3) ? null : reader.GetGuid(3),
			reader.GetString(4),
			reader.IsDBNull(5) ? null : reader.GetInt64(5),
			reader.GetInt64(6),
			reader.IsDBNull(7) ? null : reader.GetInt64(7),
			reader.IsDBNull(8) ? null : reader.GetInt32(8),
			reader.GetInt32(9),
			reader.GetInt32(10),
			reader.IsDBNull(11) ? null : reader.GetString(11),
			reader.IsDBNull(12) ? null : reader.GetString(12),
			reader.GetFieldValue<DateTimeOffset>(13),
			reader.GetFieldValue<DateTimeOffset>(14),
			reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15));
	}
}
