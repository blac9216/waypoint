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
using Waypoint.Core.ComplianceContent.SemanticImport;

namespace Waypoint.Infrastructure.ComplianceContent;

/// <inheritdoc cref="ICatalogRepository"/>
public sealed class CatalogRepository : ICatalogRepository
{
	private const string ExecutionProfileDetailProjectionSql = """
		SELECT
			ep.id, ep.component_id, ep.content_release_id, ep.report_group_id, ep.profile_version, ep.is_operator_override, ep.output_kind, ep.created_at,
			c.id, c.product_version_id, c.parent_component_id, c.component_key, c.display_name, c.transport, c.selector_kind, c.selector_name, c.created_at,
			pv.id, pv.product_id, pv.version_key, pv.display_name, pv.created_at,
			p.id, p.source_revision_id, p.vendor, p.product_key, p.display_name, p.created_at,
			cr.id, cr.source_revision_id, cr.kind, cr.release_key, cr.display_name, cr.created_at,
			rg.id, rg.group_key, rg.display_name, rg.priority, rg.created_at
		FROM catalog_execution_profiles ep
		JOIN catalog_components c ON c.id = ep.component_id
		JOIN catalog_product_versions pv ON pv.id = c.product_version_id
		JOIN catalog_products p ON p.id = pv.product_id
		JOIN catalog_content_releases cr ON cr.id = ep.content_release_id
		JOIN catalog_report_groups rg ON rg.id = ep.report_group_id
		""";

	private readonly string _connectionString;

	public CatalogRepository(string connectionString)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
		_connectionString = connectionString;
	}

	public async Task<CatalogSourceRevision> UpsertSourceRevisionAsync(string revisionKey, string? description, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_source_revisions (revision_key, description)
			VALUES ($1, $2)
			ON CONFLICT (revision_key) DO UPDATE SET description = EXCLUDED.description
			RETURNING id, revision_key, description, recorded_at
			""", connection);
		command.Parameters.AddWithValue(revisionKey);
		command.Parameters.AddWithValue((object?)description ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogSourceRevision(
			reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
	}

	public async Task<CatalogProduct> UpsertProductAsync(Guid sourceRevisionId, string vendor, string productKey, string displayName, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_products (source_revision_id, vendor, product_key, display_name)
			VALUES ($1, $2, $3, $4)
			ON CONFLICT (vendor, product_key) DO UPDATE SET display_name = EXCLUDED.display_name, source_revision_id = EXCLUDED.source_revision_id
			RETURNING id, source_revision_id, vendor, product_key, display_name, created_at
			""", connection);
		command.Parameters.AddWithValue(sourceRevisionId);
		command.Parameters.AddWithValue(vendor);
		command.Parameters.AddWithValue(productKey);
		command.Parameters.AddWithValue(displayName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogProduct(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
	}

	public async Task<CatalogProductVersion> UpsertProductVersionAsync(Guid productId, string versionKey, string displayName, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_product_versions (product_id, version_key, display_name)
			VALUES ($1, $2, $3)
			ON CONFLICT (product_id, version_key) DO UPDATE SET display_name = EXCLUDED.display_name
			RETURNING id, product_id, version_key, display_name, created_at
			""", connection);
		command.Parameters.AddWithValue(productId);
		command.Parameters.AddWithValue(versionKey);
		command.Parameters.AddWithValue(displayName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogProductVersion(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogContentRelease> UpsertContentReleaseAsync(
		Guid sourceRevisionId, string kind, string releaseKey, string displayName, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> kindErrors = CatalogVocabularyValidator.ValidateKind(kind);
		if (kindErrors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", kindErrors), nameof(kind));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_content_releases (source_revision_id, kind, release_key, display_name)
			VALUES ($1, $2, $3, $4)
			ON CONFLICT (kind, release_key) DO UPDATE SET display_name = EXCLUDED.display_name, source_revision_id = EXCLUDED.source_revision_id
			RETURNING id, source_revision_id, kind, release_key, display_name, created_at
			""", connection);
		command.Parameters.AddWithValue(sourceRevisionId);
		command.Parameters.AddWithValue(kind);
		command.Parameters.AddWithValue(releaseKey);
		command.Parameters.AddWithValue(displayName);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogContentRelease(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
	}

	public async Task<CatalogComponent> UpsertComponentAsync(Guid productVersionId, CatalogComponentDefinition definition, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(definition);
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateComponent(definition.Transport, definition.SelectorKind, definition.SelectorName);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(definition));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

		// Issue #729: catalog_components has two distinct uniqueness backings, so the
		// upsert takes two atomic ON CONFLICT paths keyed on which one applies. Both are
		// race-safe: a single INSERT ... ON CONFLICT DO UPDATE cannot lose the dedup race
		// two concurrent compliance-runner pulls (POST /pull has no enqueue singleton
		// guard; replicas > 1 is supported) can otherwise run into.
		//
		//   * Parented case (parent_component_id IS NOT NULL): 0050's
		//     catalog_components_unique UNIQUE (product_version_id, parent_component_id,
		//     component_key) already constrains it, so ON CONFLICT on that triple matches.
		//   * NULL-parent case (the overwhelmingly common one -- vSphere object-kind,
		//     whole-appliance 'target', aggregate parents): a plain UNIQUE constraint does
		//     NOT constrain NULL parents (Postgres treats NULL as distinct from NULL), so
		//     0050's constraint provides no uniqueness there. Migration 0051 adds the
		//     partial unique index catalog_components_null_parent_unique
		//     (product_version_id, component_key) WHERE parent_component_id IS NULL; the
		//     NULL branch's ON CONFLICT binds to that index by predicate, which both backs
		//     the dedup and makes it atomic under concurrency.
		string conflictTarget = definition.ParentComponentId is null
			? "(product_version_id, component_key) WHERE parent_component_id IS NULL"
			: "(product_version_id, parent_component_id, component_key)";

		await using NpgsqlCommand upsert = new(
			$"""
			INSERT INTO catalog_components (product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name)
			VALUES ($1, $2, $3, $4, $5, $6, $7)
			ON CONFLICT {conflictTarget} DO UPDATE SET
				display_name = EXCLUDED.display_name,
				transport = EXCLUDED.transport,
				selector_kind = EXCLUDED.selector_kind,
				selector_name = EXCLUDED.selector_name
			RETURNING id, product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name, created_at
			""", connection);
		upsert.Parameters.AddWithValue(productVersionId);
		upsert.Parameters.AddWithValue((object?)definition.ParentComponentId ?? DBNull.Value);
		upsert.Parameters.AddWithValue(definition.ComponentKey);
		upsert.Parameters.AddWithValue(definition.DisplayName);
		upsert.Parameters.AddWithValue(definition.Transport);
		upsert.Parameters.AddWithValue(definition.SelectorKind);
		upsert.Parameters.AddWithValue((object?)definition.SelectorName ?? DBNull.Value);

		await using NpgsqlDataReader upsertReader = await upsert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await upsertReader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapComponent(upsertReader, 0);
	}

	public async Task<CatalogReportGroup> UpsertReportGroupAsync(string groupKey, string displayName, int priority, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_report_groups (group_key, display_name, priority)
			VALUES ($1, $2, $3)
			ON CONFLICT (group_key) DO UPDATE SET display_name = EXCLUDED.display_name, priority = EXCLUDED.priority
			RETURNING id, group_key, display_name, priority, created_at
			""", connection);
		command.Parameters.AddWithValue(groupKey);
		command.Parameters.AddWithValue(displayName);
		command.Parameters.AddWithValue(priority);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogReportGroup(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogExecutionProfile> CreateExecutionProfileAsync(
		Guid componentId, Guid contentReleaseId, Guid reportGroupId, string profileVersion, string outputKind, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateOutputKind(outputKind);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(outputKind));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

		await using (NpgsqlCommand exists = new(
			"SELECT 1 FROM catalog_execution_profiles WHERE component_id = $1 AND content_release_id = $2", connection))
		{
			exists.Parameters.AddWithValue(componentId);
			exists.Parameters.AddWithValue(contentReleaseId);
			object? found = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
			if (found is not null)
			{
				throw new InvalidOperationException(
					$"an execution profile already exists for component {componentId} and content release {contentReleaseId} -- execution profiles are immutable identity, never updated in place");
			}
		}

		await using NpgsqlCommand insert = new(
			"""
			INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
			VALUES ($1, $2, $3, $4, $5)
			RETURNING id, component_id, content_release_id, report_group_id, profile_version, is_operator_override, output_kind, created_at
			""", connection);
		insert.Parameters.AddWithValue(componentId);
		insert.Parameters.AddWithValue(contentReleaseId);
		insert.Parameters.AddWithValue(reportGroupId);
		insert.Parameters.AddWithValue(profileVersion);
		insert.Parameters.AddWithValue(outputKind);

		await using NpgsqlDataReader reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapExecutionProfile(reader, 0);
	}

	public async Task<CatalogCredentialRequirement> AddCredentialRequirementAsync(
		Guid executionProfileId, string purpose, bool isRequired, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateCredentialPurpose(purpose);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(purpose));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_credential_requirements (execution_profile_id, purpose, is_required)
			VALUES ($1, $2, $3)
			ON CONFLICT (execution_profile_id, purpose) DO UPDATE SET is_required = EXCLUDED.is_required
			RETURNING id, execution_profile_id, purpose, is_required, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(purpose);
		command.Parameters.AddWithValue(isRequired);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogCredentialRequirement(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogBenchmarkReference> SetBenchmarkReferenceAsync(
		Guid executionProfileId, string benchmarkKey, string benchmarkVersion, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_benchmark_references (execution_profile_id, benchmark_key, benchmark_version)
			VALUES ($1, $2, $3)
			ON CONFLICT (execution_profile_id) DO UPDATE SET benchmark_key = EXCLUDED.benchmark_key, benchmark_version = EXCLUDED.benchmark_version
			RETURNING id, execution_profile_id, benchmark_key, benchmark_version, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(benchmarkKey);
		command.Parameters.AddWithValue(benchmarkVersion);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogBenchmarkReference(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<CatalogRemediationDefinition> SetRemediationDefinitionAsync(
		Guid executionProfileId, bool isSupported, string? mechanismNote, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_remediation_definitions (execution_profile_id, is_supported, mechanism_note)
			VALUES ($1, $2, $3)
			ON CONFLICT (execution_profile_id) DO UPDATE SET is_supported = EXCLUDED.is_supported, mechanism_note = EXCLUDED.mechanism_note
			RETURNING id, execution_profile_id, is_supported, mechanism_note, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(isSupported);
		command.Parameters.AddWithValue((object?)mechanismNote ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return new CatalogRemediationDefinition(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
	}

	public async Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, source_revision_id, vendor, product_key, display_name, created_at
			FROM catalog_products
			ORDER BY vendor, product_key
			""", connection);

		List<CatalogProduct> products = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			products.Add(new CatalogProduct(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
		}

		return products;
	}

	public async Task<IReadOnlyList<CatalogProductVersion>> ListProductVersionsAsync(Guid productId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, product_id, version_key, display_name, created_at
			FROM catalog_product_versions
			WHERE product_id = $1
			ORDER BY version_key
			""", connection);
		command.Parameters.AddWithValue(productId);

		List<CatalogProductVersion> versions = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			versions.Add(new CatalogProductVersion(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
		}

		return versions;
	}

	public async Task<IReadOnlyList<CatalogComponent>> ListComponentsAsync(Guid productVersionId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, product_version_id, parent_component_id, component_key, display_name, transport, selector_kind, selector_name, created_at
			FROM catalog_components
			WHERE product_version_id = $1
			ORDER BY component_key
			""", connection);
		command.Parameters.AddWithValue(productVersionId);

		List<CatalogComponent> components = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			components.Add(MapComponent(reader, 0));
		}

		return components;
	}

	public async Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListExecutionProfilesByComponentAsync(Guid componentId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			ExecutionProfileDetailProjectionSql + " WHERE ep.component_id = $1 ORDER BY cr.release_key", connection);
		command.Parameters.AddWithValue(componentId);

		List<CatalogExecutionProfile> profiles = [];
		Dictionary<Guid, (CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup)> joined = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogExecutionProfile profile = MapExecutionProfile(reader, 0);
				profiles.Add(profile);
				joined[profile.Id] = (MapComponent(reader, 8), MapProductVersion(reader, 17), MapProduct(reader, 22), MapContentRelease(reader, 28), MapReportGroup(reader, 34));
			}
		}

		return await HydrateDetailsAsync(connection, profiles, joined, cancellationToken).ConfigureAwait(false);
	}

	public async Task<CatalogExecutionProfileDetail?> GetExecutionProfileAsync(Guid executionProfileId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(ExecutionProfileDetailProjectionSql + " WHERE ep.id = $1", connection);
		command.Parameters.AddWithValue(executionProfileId);

		CatalogExecutionProfile profile;
		(CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup) joined;
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				return null;
			}

			profile = MapExecutionProfile(reader, 0);
			joined = (MapComponent(reader, 8), MapProductVersion(reader, 17), MapProduct(reader, 22), MapContentRelease(reader, 28), MapReportGroup(reader, 34));
		}

		IReadOnlyList<CatalogExecutionProfileDetail> details = await HydrateDetailsAsync(
			connection, [profile], new Dictionary<Guid, (CatalogComponent, CatalogProductVersion, CatalogProduct, CatalogContentRelease, CatalogReportGroup)> { [profile.Id] = joined },
			cancellationToken).ConfigureAwait(false);
		return details.Count == 0 ? null : details[0];
	}

	public async Task<IReadOnlyList<CatalogExecutionProfileDetail>> ListAllExecutionProfilesAsync(CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(ExecutionProfileDetailProjectionSql + " ORDER BY p.vendor, p.product_key, pv.version_key, c.component_key, cr.release_key", connection);

		List<CatalogExecutionProfile> profiles = [];
		Dictionary<Guid, (CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup)> joined = [];
		await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
		{
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogExecutionProfile profile = MapExecutionProfile(reader, 0);
				profiles.Add(profile);
				joined[profile.Id] = (MapComponent(reader, 8), MapProductVersion(reader, 17), MapProduct(reader, 22), MapContentRelease(reader, 28), MapReportGroup(reader, 34));
			}
		}

		return await HydrateDetailsAsync(connection, profiles, joined, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Attaches credential requirements, the optional benchmark reference, and the
	/// optional remediation definition to each already-joined execution profile. Kept
	/// as a second pass (rather than a bigger single query) because credential
	/// requirements are one-to-many and would otherwise duplicate the joined
	/// component/product/release columns per requirement row.
	/// </summary>
	private static async Task<IReadOnlyList<CatalogExecutionProfileDetail>> HydrateDetailsAsync(
		NpgsqlConnection connection,
		List<CatalogExecutionProfile> profiles,
		Dictionary<Guid, (CatalogComponent Component, CatalogProductVersion ProductVersion, CatalogProduct Product, CatalogContentRelease ContentRelease, CatalogReportGroup ReportGroup)> joined,
		CancellationToken cancellationToken)
	{
		if (profiles.Count == 0)
		{
			return [];
		}

		Guid[] ids = [.. profiles.Select(p => p.Id)];

		Dictionary<Guid, List<CatalogCredentialRequirement>> requirementsByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, purpose, is_required, created_at
			FROM catalog_credential_requirements
			WHERE execution_profile_id = ANY($1)
			ORDER BY purpose
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogCredentialRequirement requirement = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4));
				if (!requirementsByProfile.TryGetValue(requirement.ExecutionProfileId, out List<CatalogCredentialRequirement>? list))
				{
					list = [];
					requirementsByProfile[requirement.ExecutionProfileId] = list;
				}

				list.Add(requirement);
			}
		}

		Dictionary<Guid, CatalogBenchmarkReference> benchmarksByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, benchmark_key, benchmark_version, created_at
			FROM catalog_benchmark_references
			WHERE execution_profile_id = ANY($1)
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogBenchmarkReference benchmark = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
				benchmarksByProfile[benchmark.ExecutionProfileId] = benchmark;
			}
		}

		Dictionary<Guid, CatalogRemediationDefinition> remediationByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, is_supported, mechanism_note, created_at
			FROM catalog_remediation_definitions
			WHERE execution_profile_id = ANY($1)
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogRemediationDefinition remediation = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4));
				remediationByProfile[remediation.ExecutionProfileId] = remediation;
			}
		}

		Dictionary<Guid, List<CatalogDeclaredInput>> declaredInputsByProfile = [];
		await using (NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, name, input_type, is_required, created_at
			FROM catalog_declared_inputs
			WHERE execution_profile_id = ANY($1)
			ORDER BY name
			""", connection))
		{
			command.Parameters.Add(new NpgsqlParameter { Value = ids, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid });
			await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
			while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
			{
				CatalogDeclaredInput input = MapDeclaredInput(reader, 0);
				if (!declaredInputsByProfile.TryGetValue(input.ExecutionProfileId, out List<CatalogDeclaredInput>? list))
				{
					list = [];
					declaredInputsByProfile[input.ExecutionProfileId] = list;
				}

				list.Add(input);
			}
		}

		List<CatalogExecutionProfileDetail> details = [];
		foreach (CatalogExecutionProfile profile in profiles)
		{
			(CatalogComponent component, CatalogProductVersion productVersion, CatalogProduct product, CatalogContentRelease contentRelease, CatalogReportGroup reportGroup) = joined[profile.Id];
			details.Add(new CatalogExecutionProfileDetail(
				profile,
				component,
				productVersion,
				product,
				contentRelease,
				reportGroup,
				requirementsByProfile.TryGetValue(profile.Id, out List<CatalogCredentialRequirement>? requirements) ? requirements : [],
				benchmarksByProfile.TryGetValue(profile.Id, out CatalogBenchmarkReference? benchmark) ? benchmark : null,
				remediationByProfile.TryGetValue(profile.Id, out CatalogRemediationDefinition? remediation) ? remediation : null,
				declaredInputsByProfile.TryGetValue(profile.Id, out List<CatalogDeclaredInput>? declaredInputs) ? declaredInputs : []));
		}

		return details;
	}

	public async Task<CatalogDeclaredInput> UpsertDeclaredInputAsync(
		Guid executionProfileId, string name, string? inputType, bool isRequired, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_declared_inputs (execution_profile_id, name, input_type, is_required)
			VALUES ($1, $2, $3, $4)
			ON CONFLICT (execution_profile_id, name) DO UPDATE SET input_type = EXCLUDED.input_type, is_required = EXCLUDED.is_required
			RETURNING id, execution_profile_id, name, input_type, is_required, created_at
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);
		command.Parameters.AddWithValue(name);
		command.Parameters.AddWithValue((object?)inputType ?? DBNull.Value);
		command.Parameters.AddWithValue(isRequired);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapDeclaredInput(reader, 0);
	}

	public async Task<IReadOnlyList<CatalogDeclaredInput>> ListDeclaredInputsAsync(Guid executionProfileId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, execution_profile_id, name, input_type, is_required, created_at
			FROM catalog_declared_inputs
			WHERE execution_profile_id = $1
			ORDER BY name
			""", connection);
		command.Parameters.AddWithValue(executionProfileId);

		List<CatalogDeclaredInput> inputs = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			inputs.Add(MapDeclaredInput(reader, 0));
		}

		return inputs;
	}

	public async Task<CatalogImportReport> RecordImportReportAsync(
		string sourceCommit, string sourceDigest, int acceptedCount, int warningCount, int rejectedCount, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_import_reports (source_commit, source_digest, accepted_count, warning_count, rejected_count)
			VALUES ($1, $2, $3, $4, $5)
			RETURNING id, source_commit, source_digest, accepted_count, warning_count, rejected_count, recorded_at
			""", connection);
		command.Parameters.AddWithValue(sourceCommit);
		command.Parameters.AddWithValue(sourceDigest);
		command.Parameters.AddWithValue(acceptedCount);
		command.Parameters.AddWithValue(warningCount);
		command.Parameters.AddWithValue(rejectedCount);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapImportReport(reader, 0);
	}

	public async Task<CatalogImportReportEntry> RecordImportReportEntryAsync(
		Guid reportId, string disposition, string profileKey, string? reason, Guid? executionProfileId, CancellationToken cancellationToken)
	{
		if (!CatalogImportEntryDispositions.IsValid(disposition))
		{
			throw new ArgumentException(
				$"disposition '{disposition}' is not in the closed vocabulary ({string.Join(", ", CatalogImportEntryDispositions.All)})", nameof(disposition));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_import_report_entries (report_id, disposition, profile_key, reason, execution_profile_id)
			VALUES ($1, $2, $3, $4, $5)
			RETURNING id, report_id, disposition, profile_key, reason, execution_profile_id, created_at
			""", connection);
		command.Parameters.AddWithValue(reportId);
		command.Parameters.AddWithValue(disposition);
		command.Parameters.AddWithValue(profileKey);
		command.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
		command.Parameters.AddWithValue((object?)executionProfileId ?? DBNull.Value);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapImportReportEntry(reader, 0);
	}

	public async Task<IReadOnlyList<CatalogImportReport>> ListImportReportsAsync(int limit, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, source_commit, source_digest, accepted_count, warning_count, rejected_count, recorded_at
			FROM catalog_import_reports
			ORDER BY recorded_at DESC
			LIMIT $1
			""", connection);
		command.Parameters.AddWithValue(limit);

		List<CatalogImportReport> reports = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			reports.Add(MapImportReport(reader, 0));
		}

		return reports;
	}

	public async Task<IReadOnlyList<CatalogImportReportEntry>> ListImportReportEntriesAsync(Guid reportId, CancellationToken cancellationToken)
	{
		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			SELECT id, report_id, disposition, profile_key, reason, execution_profile_id, created_at
			FROM catalog_import_report_entries
			WHERE report_id = $1
			ORDER BY profile_key
			""", connection);
		command.Parameters.AddWithValue(reportId);

		List<CatalogImportReportEntry> entries = [];
		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			entries.Add(MapImportReportEntry(reader, 0));
		}

		return entries;
	}

	public async Task<CatalogPromotionOutcome> PromoteCandidateAsync(
		SemanticCandidate candidate, CatalogPromotionRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		ArgumentNullException.ThrowIfNull(request);

		if (!candidate.IsExecutableLeaf)
		{
			return new CatalogPromotionOutcome(null, "candidate is an aggregate profile -- aggregate and unsupported profiles cannot be selected for execution (issue #729 AC)");
		}

		IReadOnlyList<string> vocabularyErrors = [
			.. CatalogVocabularyValidator.ValidateComponent(candidate.Transport, candidate.SelectorKind, candidate.SelectorName),
			.. CatalogVocabularyValidator.ValidateKind(candidate.Kind),
		];
		if (vocabularyErrors.Count > 0)
		{
			// Reconciliation already runs this same check before a candidate is ever
			// marked accepted, so this should never actually fire in normal operation --
			// it exists so promotion fails closed rather than throwing if a caller ever
			// promotes a candidate that skipped reconciliation.
			return new CatalogPromotionOutcome(null, string.Join("; ", vocabularyErrors));
		}

		CatalogSourceRevision sourceRevision = await UpsertSourceRevisionAsync(request.SourceRevisionKey, description: null, cancellationToken).ConfigureAwait(false);
		CatalogProduct product = await UpsertProductAsync(sourceRevision.Id, request.Vendor, candidate.VendorFamily, request.ProductDisplayName, cancellationToken).ConfigureAwait(false);
		CatalogProductVersion productVersion = await UpsertProductVersionAsync(product.Id, candidate.ProductVersionKey, request.ProductVersionDisplayName, cancellationToken).ConfigureAwait(false);
		CatalogComponentDefinition componentDefinition = new(
			candidate.ComponentKey, candidate.DisplayName, candidate.Transport, candidate.SelectorKind, candidate.SelectorName, ParentComponentId: null);
		CatalogComponent component = await UpsertComponentAsync(productVersion.Id, componentDefinition, cancellationToken).ConfigureAwait(false);
		CatalogContentRelease contentRelease = await UpsertContentReleaseAsync(
			sourceRevision.Id, candidate.Kind, $"{candidate.ProductVersionKey}:{candidate.Kind}:{candidate.ContentDigest[..12]}", request.ContentReleaseDisplayName, cancellationToken).ConfigureAwait(false);
		CatalogReportGroup reportGroup = await UpsertReportGroupAsync(request.ReportGroupKey, request.ReportGroupDisplayName, request.ReportGroupPriority, cancellationToken).ConfigureAwait(false);

		CatalogExecutionProfile executionProfile = await UpsertExecutionProfileForPromotionAsync(
			component.Id, contentRelease.Id, reportGroup.Id, candidate.ManifestVersion ?? "unknown", request.OutputKind, cancellationToken).ConfigureAwait(false);

		foreach (InspecManifestInput input in candidate.Inputs)
		{
			await UpsertDeclaredInputAsync(executionProfile.Id, input.Name, input.Type, input.Required, cancellationToken).ConfigureAwait(false);
		}

		return new CatalogPromotionOutcome(executionProfile.Id, null);
	}

	/// <summary>
	/// Issue #832 fix: candidate promotion's execution-profile write is an atomic
	/// <c>INSERT ... ON CONFLICT (component_id, content_release_id) DO UPDATE</c>
	/// against 0050's own <c>catalog_execution_profiles_unique</c> constraint, replacing
	/// the prior check-then-insert (<c>FindExecutionProfileAsync</c> then
	/// <see cref="CreateExecutionProfileAsync"/>) that raced under concurrent promotion
	/// of the same natural key -- the same defect class PR #831 fixed for the
	/// NULL-parent <c>catalog_components</c> case, filed as its own issue (#832) because
	/// this natural key's uniqueness gap is check-then-insert non-atomicity, not a
	/// NULL-distinctness gap (both columns here are NOT NULL, so 0050's plain UNIQUE
	/// constraint already provides real DB-level uniqueness -- no new index needed,
	/// see migration 0053). The DO UPDATE clause is a no-op re-write of the immutable
	/// identity columns (report_group_id/profile_version/output_kind) rather than a
	/// true no-touch, matching <see cref="UpsertComponentAsync"/>'s established
	/// "ON CONFLICT DO UPDATE that behaviorally re-asserts the same values" shape for a
	/// natural-key upsert -- a second promotion of byte-identical content therefore
	/// still dedupes to the SAME row id (RETURNING always returns the existing row's id
	/// on conflict), preserving the additive-ingestion guarantee this repository's own
	/// <see cref="PromoteCandidateAsync"/> doc comment describes. This method is
	/// deliberately NOT exposed on <see cref="ICatalogRepository"/> --
	/// <see cref="CreateExecutionProfileAsync"/> remains the public throw-on-duplicate
	/// contract issue #728's tests pin (execution profiles are immutable identity to a
	/// direct caller); only candidate promotion, which must tolerate a benign race
	/// rather than throw, uses the upsert path.
	/// </summary>
	private async Task<CatalogExecutionProfile> UpsertExecutionProfileForPromotionAsync(
		Guid componentId, Guid contentReleaseId, Guid reportGroupId, string profileVersion, string outputKind, CancellationToken cancellationToken)
	{
		IReadOnlyList<string> errors = CatalogVocabularyValidator.ValidateOutputKind(outputKind);
		if (errors.Count > 0)
		{
			throw new ArgumentException(string.Join("; ", errors), nameof(outputKind));
		}

		await using NpgsqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
		await using NpgsqlCommand command = new(
			"""
			INSERT INTO catalog_execution_profiles (component_id, content_release_id, report_group_id, profile_version, output_kind)
			VALUES ($1, $2, $3, $4, $5)
			ON CONFLICT (component_id, content_release_id) DO UPDATE SET
				report_group_id = EXCLUDED.report_group_id,
				profile_version = EXCLUDED.profile_version,
				output_kind = EXCLUDED.output_kind
			RETURNING id, component_id, content_release_id, report_group_id, profile_version, is_operator_override, output_kind, created_at
			""", connection);
		command.Parameters.AddWithValue(componentId);
		command.Parameters.AddWithValue(contentReleaseId);
		command.Parameters.AddWithValue(reportGroupId);
		command.Parameters.AddWithValue(profileVersion);
		command.Parameters.AddWithValue(outputKind);

		await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return MapExecutionProfile(reader, 0);
	}

	private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
	{
		NpgsqlConnection connection = new(_connectionString);
		await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
		return connection;
	}

	private static CatalogExecutionProfile MapExecutionProfile(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.GetGuid(offset + 2),
		reader.GetGuid(offset + 3),
		reader.GetString(offset + 4),
		reader.GetBoolean(offset + 5),
		reader.GetString(offset + 6),
		reader.GetFieldValue<DateTimeOffset>(offset + 7));

	private static CatalogComponent MapComponent(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.IsDBNull(offset + 2) ? null : reader.GetGuid(offset + 2),
		reader.GetString(offset + 3),
		reader.GetString(offset + 4),
		reader.GetString(offset + 5),
		reader.GetString(offset + 6),
		reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
		reader.GetFieldValue<DateTimeOffset>(offset + 8));

	private static CatalogProductVersion MapProductVersion(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetFieldValue<DateTimeOffset>(offset + 4));

	private static CatalogProduct MapProduct(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetFieldValue<DateTimeOffset>(offset + 5));

	private static CatalogContentRelease MapContentRelease(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetGuid(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetFieldValue<DateTimeOffset>(offset + 5));

	private static CatalogReportGroup MapReportGroup(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetInt32(offset + 3), reader.GetFieldValue<DateTimeOffset>(offset + 4));

	private static CatalogDeclaredInput MapDeclaredInput(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.GetString(offset + 2),
		reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
		reader.GetBoolean(offset + 4),
		reader.GetFieldValue<DateTimeOffset>(offset + 5));

	private static CatalogImportReport MapImportReport(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetString(offset + 1),
		reader.GetString(offset + 2),
		reader.GetInt32(offset + 3),
		reader.GetInt32(offset + 4),
		reader.GetInt32(offset + 5),
		reader.GetFieldValue<DateTimeOffset>(offset + 6));

	private static CatalogImportReportEntry MapImportReportEntry(NpgsqlDataReader reader, int offset) => new(
		reader.GetGuid(offset),
		reader.GetGuid(offset + 1),
		reader.GetString(offset + 2),
		reader.GetString(offset + 3),
		reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
		reader.IsDBNull(offset + 5) ? null : reader.GetGuid(offset + 5),
		reader.GetFieldValue<DateTimeOffset>(offset + 6));
}
