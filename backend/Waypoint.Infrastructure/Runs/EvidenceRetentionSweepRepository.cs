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
using Waypoint.Core.Runs;

namespace Waypoint.Infrastructure.Runs;

/// <inheritdoc cref="IEvidenceRetentionSweepRepository"/>
public sealed class EvidenceRetentionSweepRepository : IEvidenceRetentionSweepRepository
{
	private readonly string _connectionString;

	public EvidenceRetentionSweepRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<IReadOnlyList<Guid>> FindPurgeCandidatesAsync(DateTimeOffset olderThan, int maxRuns, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

		// The anti-join against run_retention_holds runs INSIDE this candidate query
		// (PR #1083's round-1 review verdict on the now-deleted
		// IRunRetentionHoldRepository.ListHeldRunIdsAsync -- see this interface's own
		// doc comment). run_type/state are checked against the same closed
		// vocabularies RunLifecycle.ComplianceRunTypes/TerminalRunStates enforce in
		// C#, duplicated here as literal SQL because a candidate query has to be one
		// self-contained statement to anti-join correctly -- RunPurgeService.PurgeRunAsync
		// re-validates both anyway (RunNotTerminal is one of its own outcomes), so a
		// drift between this literal list and RunLifecycle would fail closed (an
		// ineligible run simply never becomes a purge candidate), not open.
		await using NpgsqlCommand command = new(
			"""
			SELECT r.id
			FROM runs r
			WHERE r.run_type IN ('scan', 'remediate')
			  AND r.state IN ('completed', 'completed_with_failures', 'aborted')
			  AND r.purged_at IS NULL
			  AND COALESCE(r.completed_at, r.created_at) < $1
			  AND NOT EXISTS (SELECT 1 FROM run_retention_holds h WHERE h.run_id = r.id)
			ORDER BY COALESCE(r.completed_at, r.created_at) ASC
			LIMIT $2
			""", connection);
		command.Parameters.AddWithValue(olderThan);
		command.Parameters.AddWithValue(maxRuns);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		List<Guid> runIds = [];
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			runIds.Add(reader.GetGuid(0));
		}

		return runIds;
	}
}
