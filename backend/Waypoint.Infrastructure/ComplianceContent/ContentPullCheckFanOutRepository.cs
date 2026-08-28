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
using Waypoint.Core.ComplianceContent;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="IContentPullCheckFanOutRepository"/>
public sealed class ContentPullCheckFanOutRepository : IContentPullCheckFanOutRepository
{
	private readonly string _connectionString;

	public ContentPullCheckFanOutRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task RecordFanOutAsync(
		Guid runId, Guid contentPullJobId, Guid checkJobId, string sourceCommit,
		IReadOnlyList<ContentCheckProfileDirectory> profileDirectories, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
		ArgumentNullException.ThrowIfNull(profileDirectories);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO content_pull_checks (run_id, content_pull_job_id, check_job_id, source_commit, profile_directories)
			VALUES ($1, $2, $3, $4, $5::jsonb)
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(contentPullJobId);
		command.Parameters.AddWithValue(checkJobId);
		command.Parameters.AddWithValue(sourceCommit);
		command.Parameters.AddWithValue(JsonSerializer.Serialize(profileDirectories.Select(p => new { profile_key = p.ProfileKey, profile_directory = p.ProfileDirectory })));
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<ContentPullCheckFanOut?> GetFanOutForCheckJobAsync(Guid checkJobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, run_id, content_pull_job_id, check_job_id, source_commit, profile_directories, status
			FROM content_pull_checks WHERE check_job_id = $1
			""", connection);
		command.Parameters.AddWithValue(checkJobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return Map(reader);
	}

	public async Task RecordCheckResultAsync(Guid checkJobId, ContentCheckResultRecord result, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(result);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO content_pull_check_results
				(check_job_id, profile_key, raw_yaml, has_controls_directory, has_files_directory, control_file_names, inspec_check_ran, inspec_check_passed, inspec_check_detail)
			VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8, $9)
			ON CONFLICT (check_job_id, profile_key) DO UPDATE SET
				raw_yaml = EXCLUDED.raw_yaml,
				has_controls_directory = EXCLUDED.has_controls_directory,
				has_files_directory = EXCLUDED.has_files_directory,
				control_file_names = EXCLUDED.control_file_names,
				inspec_check_ran = EXCLUDED.inspec_check_ran,
				inspec_check_passed = EXCLUDED.inspec_check_passed,
				inspec_check_detail = EXCLUDED.inspec_check_detail
			""", connection);
		command.Parameters.AddWithValue(checkJobId);
		command.Parameters.AddWithValue(result.ProfileKey);
		command.Parameters.AddWithValue((object?)result.RawYaml ?? DBNull.Value);
		command.Parameters.AddWithValue(result.HasControlsDirectory);
		command.Parameters.AddWithValue(result.HasFilesDirectory);
		command.Parameters.AddWithValue(JsonSerializer.Serialize(result.ControlFileNames));
		command.Parameters.AddWithValue(result.InspecCheckRan);
		command.Parameters.AddWithValue(result.InspecCheckPassed);
		command.Parameters.AddWithValue((object?)result.InspecCheckDetail ?? DBNull.Value);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<Guid>> ListPendingReconcileContentPullJobIdsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"SELECT DISTINCT content_pull_job_id FROM content_pull_checks WHERE status = 'pending'", connection);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<Guid> ids = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			ids.Add(reader.GetGuid(0));
		}

		return ids;
	}

	public async Task<IReadOnlyList<ContentPullCheckFanOut>> ListFanOutsForContentPullJobAsync(Guid contentPullJobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, run_id, content_pull_job_id, check_job_id, source_commit, profile_directories, status
			FROM content_pull_checks WHERE content_pull_job_id = $1 ORDER BY created_at
			""", connection);
		command.Parameters.AddWithValue(contentPullJobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<ContentPullCheckFanOut> fanOuts = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			fanOuts.Add(Map(reader));
		}

		return fanOuts;
	}

	public async Task<ContentPullCheckReconcileReadiness> GetReconcileReadinessAsync(Guid contentPullJobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT
				COUNT(*),
				COUNT(*) FILTER (WHERE j.state NOT IN ('done', 'failed', 'auth-failed', 'cancelled')),
				COUNT(*) FILTER (WHERE j.state IN ('failed', 'auth-failed', 'cancelled'))
			FROM content_pull_checks c
			JOIN jobs j ON j.id = c.check_job_id
			WHERE c.content_pull_job_id = $1
			""", connection);
		command.Parameters.AddWithValue(contentPullJobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

		int total = Convert.ToInt32(reader.GetInt64(0), System.Globalization.CultureInfo.InvariantCulture);
		int remaining = Convert.ToInt32(reader.GetInt64(1), System.Globalization.CultureInfo.InvariantCulture);
		int failed = Convert.ToInt32(reader.GetInt64(2), System.Globalization.CultureInfo.InvariantCulture);

		return new ContentPullCheckReconcileReadiness(AllTerminal: total > 0 && remaining == 0, TotalCheckJobs: total, FailedCheckJobs: failed);
	}

	public async Task<IReadOnlyList<ContentCheckResultRecord>> ListCheckResultsAsync(IReadOnlyList<Guid> checkJobIds, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(checkJobIds);
		if (checkJobIds.Count == 0)
		{
			return [];
		}

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT profile_key, raw_yaml, has_controls_directory, has_files_directory, control_file_names,
				inspec_check_ran, inspec_check_passed, inspec_check_detail
			FROM content_pull_check_results
			WHERE check_job_id = ANY($1)
			ORDER BY profile_key
			""", connection);
		command.Parameters.AddWithValue(checkJobIds.ToArray());
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

		List<ContentCheckResultRecord> results = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			List<string> controlFileNames = JsonSerializer.Deserialize<List<string>>(reader.GetString(4)) ?? [];
			results.Add(new ContentCheckResultRecord(
				ProfileKey: reader.GetString(0),
				RawYaml: reader.IsDBNull(1) ? null : reader.GetString(1),
				HasControlsDirectory: reader.GetBoolean(2),
				HasFilesDirectory: reader.GetBoolean(3),
				ControlFileNames: controlFileNames,
				InspecCheckRan: reader.GetBoolean(5),
				InspecCheckPassed: reader.GetBoolean(6),
				InspecCheckDetail: reader.IsDBNull(7) ? null : reader.GetString(7)));
		}

		return results;
	}

	public async Task MarkReconciledAsync(Guid contentPullJobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"UPDATE content_pull_checks SET status = 'reconciled' WHERE content_pull_job_id = $1 AND status = 'pending'", connection);
		command.Parameters.AddWithValue(contentPullJobId);
		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static ContentPullCheckFanOut Map(NpgsqlDataReader reader)
	{
		List<ContentCheckProfileDirectory> profileDirectories = [];
		using (JsonDocument document = JsonDocument.Parse(reader.GetString(5)))
		{
			foreach (JsonElement element in document.RootElement.EnumerateArray())
			{
				profileDirectories.Add(new ContentCheckProfileDirectory(
					element.GetProperty("profile_key").GetString() ?? string.Empty,
					element.GetProperty("profile_directory").GetString() ?? string.Empty));
			}
		}

		return new ContentPullCheckFanOut(
			Id: reader.GetGuid(0),
			RunId: reader.GetGuid(1),
			ContentPullJobId: reader.GetGuid(2),
			CheckJobId: reader.GetGuid(3),
			SourceCommit: reader.GetString(4),
			ProfileDirectories: profileDirectories,
			Status: reader.GetString(6));
	}
}
