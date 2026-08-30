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
				not_reviewed_count, skipped_count, execution_error_count, detail
			)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
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
			header.Parameters.AddWithValue(record.ExecutionErrorCount);
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
	///
	/// Issue #1132: <c>evaluated_zero_component_count</c> is a <c>count(*) FILTER</c>
	/// evaluated PER COMPONENT ROW inside the bucket, not re-derived from the summed
	/// counts. The sums cannot express it -- a mixed bucket (one component that
	/// evaluated nothing, others evaluated normally) aggregates to passed &gt; 0 and
	/// would read as fully evaluated, which is exactly the false-clean shape #1132
	/// exists to close.
	///
	/// The predicate is "produced no verdict": zero passed AND zero open (failed)
	/// findings, EXCEPT when the component is genuinely, entirely
	/// <c>not_applicable</c> -- N/A is a determinate outcome, not a failure to
	/// evaluate. Expressed positively, a zero-verdict component is counted when it
	/// carries a <c>not_reviewed</c>, <c>skipped</c>, or (issue #1144, migration 0080)
	/// <c>execution_error</c> finding, or when it has no <c>not_applicable</c> finding
	/// at all (which covers the zero-findings component -- there is no column that can
	/// be positive for it).
	///
	/// Issue #1144 closes the one gap this predicate used to have: a component mixing
	/// <c>not_applicable</c> with only <c>execution_error</c> findings used to be
	/// indistinguishable from a genuine all-<c>not_applicable</c> one, because
	/// <c>execution_error</c> landed in no count column and the predicate could only
	/// infer it from <c>not_applicable_count = 0</c>. <c>execution_error_count &gt; 0</c>
	/// is now its own explicit disjunct, so that mixed shape is correctly flagged.
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
					not_applicable_count, not_reviewed_count, skipped_count, execution_error_count
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
				coalesce(sum(skipped_count), 0),
				coalesce(sum(execution_error_count), 0),
				count(*) FILTER (
					WHERE passed_count + cat_i_open + cat_ii_open + cat_iii_open = 0
					  AND (not_reviewed_count > 0 OR skipped_count > 0 OR execution_error_count > 0 OR not_applicable_count = 0)
				) AS evaluated_zero_component_count
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
					SkippedCount: (int)reader.GetInt64(8),
					ExecutionErrorCount: (int)reader.GetInt64(9),
					EvaluatedZeroComponentCount: (int)reader.GetInt64(10)));
			}
		}

		return new RunResultRollup(runId, plannedCount, rows);
	}

	/// <summary>Resolves the header row for a job's highest <c>attempt_number</c> -- shared by both new read methods so "latest attempt" is defined in exactly one place.</summary>
	private static async Task<ComponentResultHeader?> GetLatestHeaderAsync(NpgsqlConnection connection, Guid jobId, CancellationToken cancellationToken)
	{
		// Issue #743: output_kind is LEFT JOINed from the frozen scan_plan_items row so
		// the read APIs can attach the SRG-vs-STIG statement from the plan's own frozen
		// catalog kind (never the target's connection kind). LEFT (not INNER) so a
		// result whose plan row was purged still reads back -- OutputKind then honestly
		// null rather than the whole result vanishing.
		await using NpgsqlCommand command = new(
			"""
			SELECT r.id, r.run_id, r.job_id, r.scan_plan_item_id, r.component_id, r.attempt_number, r.status, r.detail, i.output_kind
			FROM component_results r
			LEFT JOIN scan_plan_items i ON i.id = r.scan_plan_item_id
			WHERE r.job_id = $1
			ORDER BY r.attempt_number DESC
			LIMIT 1
			""", connection);
		command.Parameters.AddWithValue(jobId);
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new ComponentResultHeader(
			Id: reader.GetGuid(0),
			RunId: reader.GetGuid(1),
			JobId: reader.GetGuid(2),
			ScanPlanItemId: reader.GetGuid(3),
			ComponentId: reader.GetGuid(4),
			AttemptNumber: reader.GetInt32(5),
			Status: reader.GetString(6),
			Detail: reader.IsDBNull(7) ? null : reader.GetString(7),
			OutputKind: reader.IsDBNull(8) ? null : reader.GetString(8));
	}

	public async Task<ComponentResultFindingsPage> GetLatestFindingsAsync(Guid jobId, int limit, int offset, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		ComponentResultHeader? header = await GetLatestHeaderAsync(connection, jobId, cancellationToken).ConfigureAwait(false);
		if (header is null)
		{
			return new ComponentResultFindingsPage(Result: null, Items: [], TotalCount: 0);
		}

		int total;
		await using (NpgsqlCommand countCommand = new("SELECT count(*) FROM component_result_findings WHERE component_result_id = $1", connection))
		{
			countCommand.Parameters.AddWithValue(header.Id);
			total = (int)(long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		List<ComponentResultFindingRecord> findings = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT control_id, rule_id, title, severity, status, evidence
			FROM component_result_findings
			WHERE component_result_id = $1
			ORDER BY control_id
			LIMIT $2 OFFSET $3
			""", connection))
		{
			command.Parameters.AddWithValue(header.Id);
			command.Parameters.AddWithValue(limit);
			command.Parameters.AddWithValue(offset);
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				findings.Add(new ComponentResultFindingRecord(
					ControlId: reader.GetString(0),
					RuleId: reader.IsDBNull(1) ? null : reader.GetString(1),
					Title: reader.IsDBNull(2) ? null : reader.GetString(2),
					Severity: reader.GetString(3),
					Status: reader.GetString(4),
					Evidence: reader.IsDBNull(5) ? null : reader.GetString(5)));
			}
		}

		return new ComponentResultFindingsPage(header, findings, total);
	}

	public async Task<ComponentResultArtifactsList> GetLatestArtifactsAsync(Guid jobId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		ComponentResultHeader? header = await GetLatestHeaderAsync(connection, jobId, cancellationToken).ConfigureAwait(false);
		if (header is null)
		{
			return new ComponentResultArtifactsList(Result: null, Items: []);
		}

		List<ComponentResultArtifactRecord> artifacts = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT kind, path, digest, size_bytes
			FROM component_result_artifacts
			WHERE component_result_id = $1
			ORDER BY kind
			""", connection))
		{
			command.Parameters.AddWithValue(header.Id);
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				artifacts.Add(new ComponentResultArtifactRecord(
					Kind: reader.GetString(0),
					Path: reader.GetString(1),
					Digest: reader.GetString(2),
					SizeBytes: reader.GetInt64(3)));
			}
		}

		return new ComponentResultArtifactsList(header, artifacts);
	}
}
