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
using Waypoint.Core.Jobs;

namespace Waypoint.Infrastructure.Jobs;

/// <summary>
/// <see cref="IComponentJobRepository"/> half of <see cref="JobQueueRepository"/> --
/// split into its own partial-class file (rather than appended to the already-large
/// main file) since issue #757's grouped-counts/paged-list queries share no state or
/// helper methods with the claim/control surface beyond <c>_connectionString</c>.
/// </summary>
public sealed partial class JobQueueRepository : IComponentJobRepository
{
	public async Task<IReadOnlyList<ComponentJobCountRow>> GetGroupedCountsAsync(
		Guid runId, ComponentJobFilter filter, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(filter);

		List<object> parameters = [runId];
		List<string> clauses = ["j.run_id = $1"];
		AppendComponentJobFilters(filter, clauses, parameters);

		string sql = $"""
			SELECT
				j.priority,
				COALESCE(spi.selector_kind, '{ComponentKindVocabulary.Unknown}') AS component_kind,
				j.state,
				COUNT(*) AS cnt
			FROM jobs j
			LEFT JOIN scan_plan_items spi ON spi.id = j.scan_plan_item_id
			WHERE {string.Join(" AND ", clauses)}
			GROUP BY j.priority, component_kind, j.state
			ORDER BY j.priority, component_kind, j.state
			""";

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(sql, connection);
		foreach (object parameter in parameters)
		{
			command.Parameters.AddWithValue(parameter);
		}

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<ComponentJobCountRow> rows = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			rows.Add(new ComponentJobCountRow(
				Priority: reader.GetInt16(0),
				ComponentKind: reader.GetString(1),
				State: reader.GetString(2),
				Count: reader.GetInt64(3)));
		}

		return rows;
	}

	public async Task<ComponentJobPage> ListComponentJobsAsync(ComponentJobListQuery query, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(query);

		List<object> parameters = [query.RunId];
		List<string> clauses = ["j.run_id = $1"];
		AppendComponentJobFilters(query.Filter, clauses, parameters);

		if (query.After is { } after)
		{
			// Keyset predicate matching ORDER BY priority, created_at, id ASC: strictly
			// after in that composite order means a higher priority number, or the same
			// priority with a later created_at, or the same (priority, created_at) with a
			// strictly greater id -- the same "every tie-break leg travels with the
			// cursor" rule RunHistoryCursor's doc comment establishes, extended one leg
			// further since this list sorts by priority first.
			int p1 = parameters.Count + 1;
			int p2 = parameters.Count + 2;
			int p3 = parameters.Count + 3;
			clauses.Add($"""
				(j.priority > ${p1}
					OR (j.priority = ${p1} AND j.created_at > ${p2})
					OR (j.priority = ${p1} AND j.created_at = ${p2} AND j.id > ${p3}))
				""");
			parameters.Add(after.Priority);
			parameters.Add(after.CreatedAt);
			parameters.Add(after.Id);
		}

		// Fetch Limit + 1 to detect "more rows exist" without a second COUNT query --
		// same idiom RunHistoryPage/JobEventHistoryPage use.
		int fetchLimit = query.Limit + 1;
		int limitParamIndex = parameters.Count + 1;
		parameters.Add(fetchLimit);

		string sql = $"""
			SELECT
				j.id, j.job_type, j.target_id, j.target_name,
				j.state, j.stage, j.priority,
				COALESCE(spi.selector_kind, '{ComponentKindVocabulary.Unknown}') AS component_kind,
				j.attempt_count,
				j.created_at::text, j.started_at::text, j.finished_at::text
			FROM jobs j
			LEFT JOIN scan_plan_items spi ON spi.id = j.scan_plan_item_id
			WHERE {string.Join(" AND ", clauses)}
			ORDER BY j.priority, j.created_at, j.id
			LIMIT ${limitParamIndex}
			""";

		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(sql, connection);
		foreach (object parameter in parameters)
		{
			command.Parameters.AddWithValue(parameter);
		}

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<ComponentJobRow> rows = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			rows.Add(new ComponentJobRow(
				Id: reader.GetGuid(0),
				JobType: reader.GetString(1),
				TargetId: reader.IsDBNull(2) ? null : reader.GetGuid(2).ToString(),
				TargetName: reader.IsDBNull(3) ? null : reader.GetString(3),
				State: reader.GetString(4),
				Stage: reader.IsDBNull(5) ? null : reader.GetString(5),
				Priority: reader.GetInt16(6),
				ComponentKind: reader.GetString(7),
				AttemptCount: reader.GetInt32(8),
				CreatedAt: reader.IsDBNull(9) ? null : reader.GetString(9),
				StartedAt: reader.IsDBNull(10) ? null : reader.GetString(10),
				FinishedAt: reader.IsDBNull(11) ? null : reader.GetString(11)));
		}

		ComponentJobCursorPosition? nextCursor = null;
		bool hasMore = rows.Count > query.Limit;
		if (hasMore)
		{
			rows.RemoveAt(rows.Count - 1);
		}

		if (hasMore && rows.Count > 0)
		{
			ComponentJobRow last = rows[^1];
			nextCursor = new ComponentJobCursorPosition(
				last.Priority,
				System.DateTimeOffset.Parse(last.CreatedAt!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal),
				last.Id);
		}

		return new ComponentJobPage(rows, nextCursor);
	}

	/// <summary>
	/// Appends <see cref="ComponentJobFilter"/>'s optional state/priority/component-kind/
	/// search predicates to <paramref name="clauses"/>/<paramref name="parameters"/> --
	/// shared verbatim by <see cref="GetGroupedCountsAsync"/> and
	/// <see cref="ListComponentJobsAsync"/> so counts and the paged list they gate never
	/// disagree about which rows are "in scope."
	/// </summary>
	private static void AppendComponentJobFilters(ComponentJobFilter filter, List<string> clauses, List<object> parameters)
	{
		if (filter.States is { Count: > 0 })
		{
			int idx = parameters.Count + 1;
			clauses.Add($"j.state = ANY(${idx})");
			parameters.Add(filter.States.ToArray());
		}

		if (filter.Priorities is { Count: > 0 })
		{
			int idx = parameters.Count + 1;
			clauses.Add($"j.priority = ANY(${idx})");
			parameters.Add(filter.Priorities.ToArray());
		}

		if (filter.ComponentKinds is { Count: > 0 })
		{
			int idx = parameters.Count + 1;
			clauses.Add($"COALESCE(spi.selector_kind, '{ComponentKindVocabulary.Unknown}') = ANY(${idx})");
			parameters.Add(filter.ComponentKinds.ToArray());
		}

		if (!string.IsNullOrWhiteSpace(filter.Search))
		{
			int idx = parameters.Count + 1;
			clauses.Add($"j.target_name ILIKE ${idx}");
			parameters.Add("%" + filter.Search.Replace("%", "\\%", System.StringComparison.Ordinal).Replace("_", "\\_", System.StringComparison.Ordinal) + "%");
		}
	}
}
