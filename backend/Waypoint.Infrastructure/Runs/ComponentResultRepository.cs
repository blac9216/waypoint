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
using Waypoint.Core.Scans;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Plain Npgsql storage for migration 0063's <c>component_results</c>/
/// <c>component_result_findings</c>/<c>component_result_artifacts</c> tables -- same
/// "no ORM, transactional multi-row write" convention as
/// <see cref="ScanPlanRepository"/>.
/// </summary>
public sealed class ComponentResultRepository : IComponentResultRepository
{
	private readonly string _connectionString;

	public ComponentResultRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<Guid?> GetComponentIdForPlanItemAsync(Guid scanPlanItemId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT component_id FROM scan_plan_items WHERE id = $1", connection);
		command.Parameters.AddWithValue(scanPlanItemId);
		object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
		return result is Guid componentId ? componentId : null;
	}

	public async Task<int> NextAttemptNumberAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT count(*) FROM component_results WHERE job_id = $1", connection);
		command.Parameters.AddWithValue(jobId);
		long count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		return (int)count + 1;
	}

	public async Task RecordAsync(ComponentResultRecord record, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(record);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		Guid resultId;
		await using (NpgsqlCommand header = new(
			"""
			INSERT INTO component_results (
				run_id, job_id, scan_plan_item_id, component_id, attempt_number, status,
				cat_i_open, cat_ii_open, cat_iii_open, passed_count, not_applicable_count,
				not_reviewed_count, skipped_count, detail
			)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
			RETURNING id
			""", connection, transaction))
		{
			header.Parameters.AddWithValue(record.RunId);
			header.Parameters.AddWithValue(record.JobId);
			header.Parameters.AddWithValue(record.ScanPlanItemId);
			header.Parameters.AddWithValue(record.ComponentId);
			header.Parameters.AddWithValue(record.AttemptNumber);
			header.Parameters.AddWithValue(record.Status);
			header.Parameters.AddWithValue(record.CatIOpen);
			header.Parameters.AddWithValue(record.CatIIOpen);
			header.Parameters.AddWithValue(record.CatIIIOpen);
			header.Parameters.AddWithValue(record.PassedCount);
			header.Parameters.AddWithValue(record.NotApplicableCount);
			header.Parameters.AddWithValue(record.NotReviewedCount);
			header.Parameters.AddWithValue(record.SkippedCount);
			header.Parameters.AddWithValue((object?)record.Detail ?? DBNull.Value);

			resultId = (Guid)(await header.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		foreach (ComponentResultFinding finding in record.Findings)
		{
			await using NpgsqlCommand findingCommand = new(
				"""
				INSERT INTO component_result_findings (component_result_id, control_id, rule_id, title, severity, status, evidence)
				VALUES ($1, $2, $3, $4, $5, $6, $7)
				""", connection, transaction);
			findingCommand.Parameters.AddWithValue(resultId);
			findingCommand.Parameters.AddWithValue(finding.ControlId);
			findingCommand.Parameters.AddWithValue((object?)finding.RuleId ?? DBNull.Value);
			findingCommand.Parameters.AddWithValue((object?)finding.Title ?? DBNull.Value);
			findingCommand.Parameters.AddWithValue(finding.Severity);
			findingCommand.Parameters.AddWithValue(finding.Status);
			findingCommand.Parameters.AddWithValue((object?)finding.Evidence ?? DBNull.Value);
			await findingCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		foreach (ComponentResultArtifact artifact in record.Artifacts)
		{
			await using NpgsqlCommand artifactCommand = new(
				"""
				INSERT INTO component_result_artifacts (component_result_id, kind, path, digest, size_bytes)
				VALUES ($1, $2, $3, $4, $5)
				""", connection, transaction);
			artifactCommand.Parameters.AddWithValue(resultId);
			artifactCommand.Parameters.AddWithValue(artifact.Kind);
			artifactCommand.Parameters.AddWithValue(artifact.Path);
			artifactCommand.Parameters.AddWithValue(artifact.Digest);
			artifactCommand.Parameters.AddWithValue(artifact.SizeBytes);
			await artifactCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// One SQL round trip, two CTEs: <c>latest</c> picks the highest <c>attempt_number</c>
	/// component_results row per scan_plan_item within the run (ADR-0024 "the latest
	/// completed attempt supplies the current component result"), then the outer query
	/// GROUP BYs those rows by status. The plan's total accepted-item count is a
	/// second, independent scalar query against <c>scan_plan_items</c> -- the "planned
	/// but no result row yet" coverage gap this rollup deliberately does not fold into
	/// the status GROUP BY (a row with no component_results at all has no status to
	/// group under). Bounded by the closed status vocabulary (3 rows max), never by
	/// component/job count -- issue #941's grouped-counts idiom.
	/// </summary>
	public async Task<RunResultRollup> GetRunRollupAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		int plannedCount;
		await using (NpgsqlCommand plannedCommand = new(
			"""
			SELECT count(*) FROM scan_plan_items spi
			JOIN scan_plans sp ON sp.id = spi.scan_plan_id
			WHERE sp.run_id = $1
			""", connection))
		{
			plannedCommand.Parameters.AddWithValue(runId);
			plannedCount = (int)(long)(await plannedCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<RunResultRollupRow> rows = [];
		await using (NpgsqlCommand rollupCommand = new(
			"""
			WITH latest AS (
				SELECT DISTINCT ON (scan_plan_item_id)
					status, cat_i_open, cat_ii_open, cat_iii_open, passed_count,
					not_applicable_count, not_reviewed_count, skipped_count
				FROM component_results
				WHERE run_id = $1
				ORDER BY scan_plan_item_id, attempt_number DESC
			)
			SELECT
				status,
				count(*) AS component_count,
				coalesce(sum(cat_i_open), 0),
				coalesce(sum(cat_ii_open), 0),
				coalesce(sum(cat_iii_open), 0),
				coalesce(sum(passed_count), 0),
				coalesce(sum(not_applicable_count), 0),
				coalesce(sum(not_reviewed_count), 0),
				coalesce(sum(skipped_count), 0)
			FROM latest
			GROUP BY status
			ORDER BY status
			""", connection))
		{
			rollupCommand.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await rollupCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				rows.Add(new RunResultRollupRow(
					Status: reader.GetString(0),
					ComponentCount: (int)reader.GetInt64(1),
					CatIOpen: (int)reader.GetInt64(2),
					CatIIOpen: (int)reader.GetInt64(3),
					CatIIIOpen: (int)reader.GetInt64(4),
					PassedCount: (int)reader.GetInt64(5),
					NotApplicableCount: (int)reader.GetInt64(6),
					NotReviewedCount: (int)reader.GetInt64(7),
					SkippedCount: (int)reader.GetInt64(8)));
			}
		}

		return new RunResultRollup(runId, plannedCount, rows);
	}
}
