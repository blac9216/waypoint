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
using NpgsqlTypes;
using Waypoint.Core.ConfigDocs;
using Waypoint.Core.Scans;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Storage for <c>scan_plans</c>/<c>scan_plan_items</c> (migration 0057): plain Npgsql,
/// no ORM, transactional write of the plan header plus every accepted item -- same
/// convention as <see cref="RunScopeSnapshotRepository"/> one layer up.
/// </summary>
public sealed class ScanPlanRepository : IScanPlanRepository
{
	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

	private readonly string _connectionString;

	public ScanPlanRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task RecordAsync(Guid runId, Guid? runScopeSnapshotId, ScanPlan plan, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(plan);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		Guid planId;
		await using (NpgsqlCommand header = new(
			"""
			INSERT INTO scan_plans (run_id, plan_schema_version, run_scope_snapshot_id, plan_digest, explanation, skips_json)
			VALUES ($1, $2, $3, $4, $5, $6::jsonb)
			RETURNING id
			""", connection, transaction))
		{
			header.Parameters.AddWithValue(runId);
			header.Parameters.AddWithValue(plan.PlanSchemaVersion);
			header.Parameters.AddWithValue((object?)runScopeSnapshotId ?? DBNull.Value);
			header.Parameters.AddWithValue(plan.PlanDigest);
			header.Parameters.AddWithValue(plan.Explanation);
			header.Parameters.AddWithValue(JsonSerializer.Serialize(plan.Skips.Select(ToSkipWire), SerializerOptions));
			planId = (Guid)(await header.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
		}

		foreach (ScanPlanItem item in plan.Items)
		{
			await using NpgsqlCommand itemCommand = new(
				"""
				INSERT INTO scan_plan_items (
					scan_plan_id, component_id, catalog_execution_profile_id, baseline_id, benchmark_revision_id,
					transport, selector_kind, selector_name, report_group_key, priority, output_kind,
					required_purposes_json, declared_inputs_json, input_resolutions_json, attestation_resolution_json)
				VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12::jsonb, $13::jsonb, $14::jsonb, $15::jsonb)
				""", connection, transaction);
			itemCommand.Parameters.AddWithValue(planId);
			itemCommand.Parameters.AddWithValue(item.ComponentId);
			itemCommand.Parameters.AddWithValue(item.CatalogExecutionProfileId);
			itemCommand.Parameters.AddWithValue((object?)item.BaselineId ?? DBNull.Value);
			itemCommand.Parameters.AddWithValue((object?)item.BenchmarkRevisionId ?? DBNull.Value);
			itemCommand.Parameters.AddWithValue(item.Transport);
			itemCommand.Parameters.AddWithValue(item.SelectorKind);
			itemCommand.Parameters.AddWithValue((object?)item.SelectorName ?? DBNull.Value);
			itemCommand.Parameters.AddWithValue(item.ReportGroupKey);
			itemCommand.Parameters.AddWithValue(item.Priority);
			itemCommand.Parameters.AddWithValue(item.OutputKind);
			itemCommand.Parameters.AddWithValue(JsonSerializer.Serialize(item.RequiredPurposes, SerializerOptions));
			itemCommand.Parameters.AddWithValue(JsonSerializer.Serialize(item.DeclaredInputNames, SerializerOptions));
			itemCommand.Parameters.AddWithValue(JsonSerializer.Serialize(item.InputResolutionsOrEmpty, SerializerOptions));
			itemCommand.Parameters.Add(new NpgsqlParameter(parameterName: null, NpgsqlDbType.Jsonb)
			{
				Value = item.AttestationResolution is { } attestation
					? JsonSerializer.Serialize(attestation, SerializerOptions)
					: DBNull.Value,
			});
			await itemCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<ScanPlan?> GetForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		int planSchemaVersion;
		string planDigest;
		string explanation;
		string skipsJson;

		await using (NpgsqlCommand header = new(
			"""
			SELECT plan_schema_version, plan_digest, explanation, skips_json
			FROM scan_plans
			WHERE run_id = $1
			""", connection))
		{
			header.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await header.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return null;
			}

			planSchemaVersion = reader.GetInt32(0);
			planDigest = reader.GetString(1);
			explanation = reader.GetString(2);
			skipsJson = reader.GetString(3);
		}

		List<ScanPlanItem> items = [];
		await using (NpgsqlCommand itemsCommand = new(
			"""
			SELECT i.component_id, i.catalog_execution_profile_id, i.baseline_id, i.benchmark_revision_id,
				i.transport, i.selector_kind, i.selector_name, i.report_group_key, i.priority, i.output_kind,
				i.required_purposes_json, i.declared_inputs_json, i.input_resolutions_json, i.attestation_resolution_json
			FROM scan_plan_items i
			JOIN scan_plans p ON p.id = i.scan_plan_id
			WHERE p.run_id = $1
			ORDER BY i.component_id
			""", connection))
		{
			itemsCommand.Parameters.AddWithValue(runId);
			await using NpgsqlDataReader reader = await itemsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				items.Add(new ScanPlanItem(
					ComponentId: reader.GetGuid(0),
					CatalogExecutionProfileId: reader.GetGuid(1),
					BaselineId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
					BenchmarkRevisionId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
					Transport: reader.GetString(4),
					SelectorKind: reader.GetString(5),
					SelectorName: reader.IsDBNull(6) ? null : reader.GetString(6),
					ReportGroupKey: reader.GetString(7),
					Priority: reader.GetInt32(8),
					OutputKind: reader.GetString(9),
					RequiredPurposes: JsonSerializer.Deserialize<List<string>>(reader.GetString(10), SerializerOptions) ?? [],
					DeclaredInputNames: JsonSerializer.Deserialize<List<string>>(reader.GetString(11), SerializerOptions) ?? [],
					InputResolutions: JsonSerializer.Deserialize<List<PlanInputResolution>>(reader.GetString(12), SerializerOptions) ?? [],
					AttestationResolution: reader.IsDBNull(13)
						? null
						: JsonSerializer.Deserialize<PlanAttestationResolution>(reader.GetString(13), SerializerOptions)));
			}
		}

		List<ScanPlanSkip> skips = [.. (JsonSerializer.Deserialize<List<SkipWire>>(skipsJson, SerializerOptions) ?? []).Select(FromSkipWire)];

		return new ScanPlan(runId, planSchemaVersion, items, skips, planDigest, explanation);
	}

	private sealed record SkipWire(Guid ComponentId, string Reason, string Detail);

	private static SkipWire ToSkipWire(ScanPlanSkip skip) => new(skip.ComponentId, skip.Reason, skip.Detail);

	private static ScanPlanSkip FromSkipWire(SkipWire wire) => new(wire.ComponentId, wire.Reason, wire.Detail);
}
