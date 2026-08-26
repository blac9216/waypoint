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
using Waypoint.Core.Components;
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Runs;

/// <summary>
/// Storage for <c>run_scope_snapshots</c> (migration 0056): plain Npgsql, no ORM --
/// same convention as every other repository in this codebase (cf.
/// <see cref="Waypoint.Infrastructure.Components.ComponentRepository"/>).
/// </summary>
public sealed class RunScopeSnapshotRepository : IRunScopeSnapshotRepository
{
	private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

	private readonly string _connectionString;

	public RunScopeSnapshotRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task RecordAsync(
		Guid runId,
		string requestedMode,
		string requestedScopeJson,
		IReadOnlyList<Guid> resolvedComponentIds,
		IReadOnlyList<ScopeOmission> omissions,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(requestedMode);
		ArgumentException.ThrowIfNullOrWhiteSpace(requestedScopeJson);
		ArgumentNullException.ThrowIfNull(resolvedComponentIds);
		ArgumentNullException.ThrowIfNull(omissions);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			INSERT INTO run_scope_snapshots (run_id, requested_mode, requested_scope_json, resolved_component_ids, omissions_json)
			VALUES ($1, $2, $3::jsonb, $4, $5::jsonb)
			""", connection);
		command.Parameters.AddWithValue(runId);
		command.Parameters.AddWithValue(requestedMode);
		command.Parameters.AddWithValue(requestedScopeJson);
		command.Parameters.AddWithValue(resolvedComponentIds.ToArray());
		command.Parameters.AddWithValue(JsonSerializer.Serialize(omissions.Select(ToWire), SerializerOptions));

		await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<RunScopeSnapshot?> GetForRunAsync(Guid runId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		await using NpgsqlCommand command = new(
			"""
			SELECT id, run_id, requested_mode, requested_scope_json, resolved_component_ids, omissions_json, created_at
			FROM run_scope_snapshots
			WHERE run_id = $1
			""", connection);
		command.Parameters.AddWithValue(runId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		Guid[] resolvedIds = reader.GetFieldValue<Guid[]>(4);
		string omissionsJson = reader.GetString(5);
		List<ScopeOmission> omissions = [.. (JsonSerializer.Deserialize<List<OmissionWire>>(omissionsJson, SerializerOptions) ?? [])
			.Select(FromWire)];

		return new RunScopeSnapshot(
			reader.GetGuid(0),
			reader.GetGuid(1),
			reader.GetString(2),
			reader.GetString(3),
			resolvedIds,
			omissions,
			reader.GetFieldValue<DateTimeOffset>(6));
	}

	/// <summary>JSON-serializable mirror of <see cref="ScopeOmission"/> (kept private/internal to this repository -- the domain type itself has no <c>System.Text.Json</c> attributes, matching this codebase's "domain types are plain, wire mapping lives at the boundary" convention).</summary>
	private sealed record OmissionWire(Guid? ComponentId, Guid? TargetId, string Reason, string Detail);

	private static OmissionWire ToWire(ScopeOmission omission) => new(omission.ComponentId, omission.TargetId, omission.Reason, omission.Detail);

	private static ScopeOmission FromWire(OmissionWire wire) => new(wire.ComponentId, wire.TargetId, wire.Reason, wire.Detail);
}
