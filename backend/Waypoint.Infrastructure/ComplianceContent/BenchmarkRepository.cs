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
using Waypoint.Core.ComplianceContent;
using Waypoint.Core.ComplianceContent.Xccdf;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="IBenchmarkRepository"/>
public sealed class BenchmarkRepository : IBenchmarkRepository
{
	private readonly string _connectionString;

	public BenchmarkRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<BenchmarkRevision> ImportRevisionAsync(BenchmarkImportCandidate candidate, string source, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		IReadOnlyList<string> sourceErrors = BenchmarkVocabularyValidator.ValidateSource(source);
		if (sourceErrors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", sourceErrors), nameof(source));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Digest addressing (issue #730 AC): re-importing byte-identical content for the
		// same benchmark_key must resolve to the SAME row rather than a duplicate. Check
		// first so a repeat import is a cheap read, not a failed insert relying on the
		// unique constraint to short-circuit.
		BenchmarkRevision? existing = await FindByDigestAsync(connection, transaction, candidate.BenchmarkKey, candidate.ContentDigest, cancellationToken)
			.ConfigureAwait(false);
		if (existing is not null)
		{
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return existing;
		}

		await using NpgsqlCommand insertRevision = new(
			"""
			INSERT INTO benchmark_revisions (benchmark_key, title, version, release, source, content_digest, rule_count, lifecycle_state)
			VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
			RETURNING id, benchmark_key, title, version, release, source, content_digest, rule_count, lifecycle_state, imported_at
			""", connection, transaction);
		insertRevision.Parameters.AddWithValue(candidate.BenchmarkKey);
		insertRevision.Parameters.AddWithValue(candidate.Title);
		insertRevision.Parameters.AddWithValue(candidate.Version);
		insertRevision.Parameters.AddWithValue(candidate.Release);
		insertRevision.Parameters.AddWithValue(source);
		insertRevision.Parameters.AddWithValue(candidate.ContentDigest);
		insertRevision.Parameters.AddWithValue(candidate.Rules.Count);
		insertRevision.Parameters.AddWithValue(BenchmarkLifecycleStates.Staged);

		BenchmarkRevision revision;
		await using (NpgsqlDataReader reader = await insertRevision.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			revision = MapRevision(reader, 0);
		}

		foreach (XccdfRule rule in candidate.Rules)
		{
			IReadOnlyList<string> severityErrors = BenchmarkVocabularyValidator.ValidateSeverity(rule.Severity);
			if (severityErrors.Count > 0)
			{
				throw new ArgumentException(string.Join("; ", severityErrors), nameof(candidate));
			}

			await using NpgsqlCommand insertRule = new(
				"""
				INSERT INTO benchmark_rules (benchmark_revision_id, rule_id, vuln_id, severity, title)
				VALUES ($1, $2, $3, $4, $5)
				ON CONFLICT (benchmark_revision_id, rule_id) DO NOTHING
				""", connection, transaction);
			insertRule.Parameters.AddWithValue(revision.Id);
			insertRule.Parameters.AddWithValue(rule.RuleId);
			insertRule.Parameters.AddWithValue((object?)rule.VulnId ?? DBNull.Value);
			insertRule.Parameters.AddWithValue(rule.Severity);
			insertRule.Parameters.AddWithValue(rule.Title);
			await insertRule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return revision;
	}

	public async Task<BenchmarkRevision?> GetRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, benchmark_key, title, version, release, source, content_digest, rule_count, lifecycle_state, imported_at
			FROM benchmark_revisions
			WHERE id = $1
			""", connection);
		command.Parameters.AddWithValue(revisionId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRevision(reader, 0) : null;
	}

	public async Task<IReadOnlyList<BenchmarkRevision>> ListRevisionsByBenchmarkKeyAsync(string benchmarkKey, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, benchmark_key, title, version, release, source, content_digest, rule_count, lifecycle_state, imported_at
			FROM benchmark_revisions
			WHERE benchmark_key = $1
			ORDER BY imported_at DESC, id DESC
			""", connection);
		command.Parameters.AddWithValue(benchmarkKey);

		List<BenchmarkRevision> revisions = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			revisions.Add(MapRevision(reader, 0));
		}

		return revisions;
	}

	public async Task<IReadOnlyList<string>> ListBenchmarkKeysAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new("SELECT DISTINCT benchmark_key FROM benchmark_revisions ORDER BY benchmark_key", connection);

		List<string> keys = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			keys.Add(reader.GetString(0));
		}

		return keys;
	}

	public async Task<IReadOnlyList<BenchmarkRule>> ListRulesAsync(Guid revisionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, benchmark_revision_id, rule_id, vuln_id, severity, title, created_at
			FROM benchmark_rules
			WHERE benchmark_revision_id = $1
			ORDER BY rule_id
			""", connection);
		command.Parameters.AddWithValue(revisionId);

		List<BenchmarkRule> rules = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			rules.Add(MapRule(reader, 0));
		}

		return rules;
	}

	public async Task<BenchmarkComponentMapping> SetMappingAsync(
		Guid catalogComponentId,
		Guid? benchmarkRevisionId,
		string status,
		bool isSrgNoBenchmark,
		bool isAdminOverride,
		int ambiguousCandidateCount,
		string? reason,
		string? actor,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<string> statusErrors = BenchmarkVocabularyValidator.ValidateMappingStatus(status);
		if (statusErrors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", statusErrors), nameof(status));
		}

		if (isSrgNoBenchmark && benchmarkRevisionId is not null)
		{
			throw new ArgumentException("a mapping cannot both declare 'SRG has no published benchmark' and reference a benchmark revision", nameof(benchmarkRevisionId));
		}

		if (status == BenchmarkMappingStatuses.Mapped && benchmarkRevisionId is null)
		{
			throw new ArgumentException("status 'mapped' requires a non-null benchmark revision id", nameof(benchmarkRevisionId));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

		// Versioned audit history (issue #730 AC): the previous current mapping (if any)
		// is superseded, never deleted or overwritten in place.
		await using (NpgsqlCommand supersede = new(
			"UPDATE benchmark_component_mappings SET is_current = false WHERE catalog_component_id = $1 AND is_current",
			connection, transaction))
		{
			supersede.Parameters.AddWithValue(catalogComponentId);
			await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO benchmark_component_mappings
				(catalog_component_id, benchmark_revision_id, status, is_srg_no_benchmark, is_admin_override, is_current, ambiguous_candidate_count, reason, actor)
			VALUES ($1, $2, $3, $4, $5, true, $6, $7, $8)
			RETURNING id, catalog_component_id, benchmark_revision_id, status, is_srg_no_benchmark, is_admin_override, is_current, ambiguous_candidate_count, reason, actor, created_at
			""", connection, transaction);
		insert.Parameters.AddWithValue(catalogComponentId);
		insert.Parameters.AddWithValue((object?)benchmarkRevisionId ?? DBNull.Value);
		insert.Parameters.AddWithValue(status);
		insert.Parameters.AddWithValue(isSrgNoBenchmark);
		insert.Parameters.AddWithValue(isAdminOverride);
		insert.Parameters.AddWithValue(ambiguousCandidateCount);
		insert.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
		insert.Parameters.AddWithValue((object?)actor ?? DBNull.Value);

		BenchmarkComponentMapping mapping;
		await using (NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
			mapping = MapMapping(reader, 0);
		}

		await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		return mapping;
	}

	public async Task<BenchmarkComponentMapping?> GetCurrentMappingAsync(Guid catalogComponentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, catalog_component_id, benchmark_revision_id, status, is_srg_no_benchmark, is_admin_override, is_current, ambiguous_candidate_count, reason, actor, created_at
			FROM benchmark_component_mappings
			WHERE catalog_component_id = $1 AND is_current
			""", connection);
		command.Parameters.AddWithValue(catalogComponentId);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapMapping(reader, 0) : null;
	}

	public async Task<IReadOnlyList<BenchmarkComponentMapping>> GetMappingHistoryAsync(Guid catalogComponentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, catalog_component_id, benchmark_revision_id, status, is_srg_no_benchmark, is_admin_override, is_current, ambiguous_candidate_count, reason, actor, created_at
			FROM benchmark_component_mappings
			WHERE catalog_component_id = $1
			ORDER BY created_at DESC, id DESC
			""", connection);
		command.Parameters.AddWithValue(catalogComponentId);

		List<BenchmarkComponentMapping> mappings = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			mappings.Add(MapMapping(reader, 0));
		}

		return mappings;
	}

	public async Task<IReadOnlyList<BenchmarkComponentMapping>> ListCurrentMappingsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, catalog_component_id, benchmark_revision_id, status, is_srg_no_benchmark, is_admin_override, is_current, ambiguous_candidate_count, reason, actor, created_at
			FROM benchmark_component_mappings
			WHERE is_current
			ORDER BY created_at DESC, id DESC
			""", connection);

		List<BenchmarkComponentMapping> mappings = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			mappings.Add(MapMapping(reader, 0));
		}

		return mappings;
	}

	private static async Task<BenchmarkRevision?> FindByDigestAsync(
		NpgsqlConnection connection, NpgsqlTransaction transaction, string benchmarkKey, string contentDigest, CancellationToken cancellationToken)
	{
		await using NpgsqlCommand command = new(
			"""
			SELECT id, benchmark_key, title, version, release, source, content_digest, rule_count, lifecycle_state, imported_at
			FROM benchmark_revisions
			WHERE benchmark_key = $1 AND content_digest = $2
			""", connection, transaction);
		command.Parameters.AddWithValue(benchmarkKey);
		command.Parameters.AddWithValue(contentDigest);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRevision(reader, 0) : null;
	}

	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return connection;
	}

	private static BenchmarkRevision MapRevision(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetString(offset + 1),
		reader.GetString(offset + 2),
		reader.GetString(offset + 3),
		reader.GetString(offset + 4),
		reader.GetString(offset + 5),
		reader.GetString(offset + 6),
		reader.GetInt32(offset + 7),
		reader.GetString(offset + 8),
		reader.GetFieldValue<DateTimeOffset>(offset + 9));

	private static BenchmarkRule MapRule(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.GetString(offset + 2),
		reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
		reader.GetString(offset + 4),
		reader.GetString(offset + 5),
		reader.GetFieldValue<DateTimeOffset>(offset + 6));

	private static BenchmarkComponentMapping MapMapping(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.IsDBNull(offset + 2) ? null : reader.GetGuid(offset + 2),
		reader.GetString(offset + 3),
		reader.GetBoolean(offset + 4),
		reader.GetBoolean(offset + 5),
		reader.GetBoolean(offset + 6),
		reader.GetInt32(offset + 7),
		reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
		reader.IsDBNull(offset + 9) ? null : reader.GetString(offset + 9),
		reader.GetFieldValue<DateTimeOffset>(offset + 10));
}
