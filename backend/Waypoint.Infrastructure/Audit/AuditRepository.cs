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

using System.Globalization;
using Npgsql;
using Waypoint.Core.Audit;

namespace Waypoint.Infrastructure.Audit;

/// <inheritdoc cref="IAuditRepository"/>
public sealed class AuditRepository : IAuditRepository
{
	private readonly string _connectionString;

	public AuditRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<AuditListResult> ListAsync(AuditQuery query, int limit, int offset, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		(string whereClause, List<object> parameters) = BuildWhere(query);

		await using NpgsqlCommand countCommand = new($"SELECT COUNT(*) FROM audit_log {whereClause}", connection);
		AddParameters(countCommand, parameters);
		long totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

		await using NpgsqlCommand command = new(
			$"""
			SELECT id, event_type, actor, credential_id, job_id, run_id, detail::text, occurred_at
			FROM audit_log
			{whereClause}
			ORDER BY occurred_at DESC, id DESC
			LIMIT ${parameters.Count + 1} OFFSET ${parameters.Count + 2}
			""", connection);
		AddParameters(command, parameters);
		command.Parameters.AddWithValue(limit);
		command.Parameters.AddWithValue(offset);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<AuditEntry> items = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			items.Add(Map(reader));
		}

		return new AuditListResult(items, totalCount);
	}

	/// <summary>
	/// Builds a shared parameterized WHERE clause for both the count and page queries
	/// above so the two can never drift out of sync with each other -- a mismatched
	/// filter between them would make <c>X-Total-Count</c> lie about the page returned.
	/// </summary>
	private static (string WhereClause, List<object> Parameters) BuildWhere(AuditQuery query)
	{
		List<string> clauses = [];
		List<object> parameters = [];

		if (!string.IsNullOrWhiteSpace(query.EventType))
		{
			parameters.Add(query.EventType);
			clauses.Add($"event_type = ${parameters.Count}");
		}

		if (!string.IsNullOrWhiteSpace(query.Actor))
		{
			parameters.Add(query.Actor);
			clauses.Add($"actor = ${parameters.Count}");
		}

		if (query.From is DateTimeOffset from)
		{
			parameters.Add(from);
			clauses.Add($"occurred_at >= ${parameters.Count}");
		}

		if (query.To is DateTimeOffset to)
		{
			parameters.Add(to);
			clauses.Add($"occurred_at <= ${parameters.Count}");
		}

		return clauses.Count == 0 ? (string.Empty, parameters) : ("WHERE " + string.Join(" AND ", clauses), parameters);
	}

	private static void AddParameters(NpgsqlCommand command, IReadOnlyList<object> parameters)
	{
		foreach (object parameter in parameters)
		{
			command.Parameters.AddWithValue(parameter);
		}
	}

	private static AuditEntry Map(NpgsqlDataReader reader)
	{
		return new AuditEntry(
			Id: reader.GetGuid(0),
			EventType: reader.GetString(1),
			Actor: reader.GetString(2),
			CredentialId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
			JobId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
			RunId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
			DetailJson: reader.GetString(6),
			OccurredAt: reader.GetFieldValue<DateTimeOffset>(7));
	}
}
